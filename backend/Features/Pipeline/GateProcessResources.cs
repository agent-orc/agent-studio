using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentStudio.Pipeline;

/// <summary>
/// Samples the shell and its descendants, including workers, every five seconds.
/// CPU is a sampled lower bound: children that start and exit between snapshots
/// cannot be counted. Missing host telemetry never earns an extension.
/// </summary>
internal interface IGateProcessResources : IDisposable
{
    GateResourceEvidence Sample();
}

internal sealed class GateProcessResources : IGateProcessResources
{
    private readonly int _rootPid;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, (Process Process, double Cpu)> _tracked = [];
    private SystemLoadThrottle.CpuTicks? _host;
    private double _cpu;
    private double _hostWeighted;
    private double _externalWeighted;
    private int _hostProcessorCount;
    private double _peak;
    private long _measuredMs;
    private long _lastMs;
    private long _externalMs;
    private long _recentExternalMs;
    private double _recentCpu;
    private readonly Queue<(long At, double Cpu)> _progress = new();
    private int _samples;
    private bool _available;

    internal GateProcessResources(int rootPid)
    {
        _rootPid = rootPid;
        if (SystemLoadThrottle.SystemCpuReader.TryRead(out var ticks)) _host = ticks;
        Sample();
    }

    public GateResourceEvidence Sample()
    {
        try { return SampleCore(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SilentCatch.Note(ex, "GateProcessResources: resource evidence unavailable");
            _available = false;
            _recentExternalMs = 0;
            _recentCpu = 0;
            return Snapshot(_clock.ElapsedMilliseconds);
        }
    }

    private GateResourceEvidence SampleCore()
    {
        var now = _clock.ElapsedMilliseconds;
        var elapsed = now - _lastMs;
        if (_samples > 0 && elapsed < 100) return Snapshot(now);
        var before = _cpu;
        var treeAvailable = true;
        try
        {
            var parents = ReadParents();
            var members = _tracked.Keys.Append(_rootPid).ToHashSet();
            bool added;
            do
            {
                added = false;
                foreach (var (pid, parent) in parents)
                    if (members.Contains(parent)) added |= members.Add(pid);
            } while (added);
            foreach (var pid in members)
            {
                if (_tracked.ContainsKey(pid)) continue;
                try { _tracked[pid] = (Process.GetProcessById(pid), 0); }
                catch (ArgumentException ex) { SilentCatch.Note(ex, "GateProcessResources: child exited before open"); }
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            treeAvailable = false;
            SilentCatch.Note(ex, "GateProcessResources: process snapshot unavailable");
        }
        foreach (var (pid, entry) in _tracked.ToArray())
        {
            try
            {
                entry.Process.Refresh();
                var cpu = entry.Process.TotalProcessorTime.TotalMilliseconds;
                _cpu += Math.Max(0, cpu - entry.Cpu);
                _tracked[pid] = (entry.Process, cpu);
                if (!entry.Process.HasExited) continue;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (ex is System.ComponentModel.Win32Exception) treeAvailable = false;
                SilentCatch.Note(ex, "GateProcessResources: child exited during CPU sample");
            }
            entry.Process.Dispose();
            _tracked.Remove(pid);
        }

        _available = false;
        if (SystemLoadThrottle.SystemCpuReader.TryRead(out var host))
        {
            if (_host is { } previous && elapsed > 0 && host.Total > previous.Total && host.Idle >= previous.Idle)
            {
                var total = host.Total - previous.Total;
                var idle = Math.Min(total, host.Idle - previous.Idle);
                var hostPercent = 100d * (total - idle) / total;
                // Host tick counters span all host CPUs. Linux container quotas
                // must not substitute the container's effective processor count.
                var cores = OperatingSystem.IsLinux()
                    ? File.ReadLines("/proc/stat").Count(line => line.Length > 3 && line.StartsWith("cpu") && char.IsDigit(line[3]))
                    : Environment.ProcessorCount;
                var deltaCpu = _cpu - before;
                _progress.Enqueue((now, deltaCpu));
                while (_progress.TryPeek(out var progress) && now - progress.At > 15_000) _progress.Dequeue();
                _recentCpu = _progress.Sum(sample => sample.Cpu);
                var treePercent = Math.Clamp(100d * deltaCpu / elapsed / Math.Max(1, cores), 0, 100);
                _hostProcessorCount = Math.Max(1, cores);
                _hostWeighted += hostPercent * elapsed;
                _externalWeighted += Math.Max(0, hostPercent - treePercent) * elapsed;
                _peak = Math.Max(_peak, hostPercent);
                _measuredMs += elapsed;
                _samples++;
                _available = treeAvailable;
                if (treeAvailable && GateContentionBudget.IsExternalContention(hostPercent, treePercent))
                {
                    _externalMs += elapsed;
                    _recentExternalMs += elapsed;
                }
                else { _recentExternalMs = 0; _recentCpu = 0; }
            }
            _host = host;
        }
        if (!_available) { _recentExternalMs = 0; _recentCpu = 0; }
        _lastMs = now;
        return Snapshot(now);
    }

    private GateResourceEvidence Snapshot(long now)
        => new(now, _cpu, _measuredMs > 0 ? _hostWeighted / _measuredMs : null,
            _samples > 0 ? _peak : null, _externalMs, _recentExternalMs, _recentCpu, _samples, _available, _hostProcessorCount,
            _measuredMs > 0 ? _externalWeighted / _measuredMs : null);

    private static Dictionary<int, int> ReadParents()
    {
        var result = new Dictionary<int, int>();
        if (OperatingSystem.IsLinux())
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (int.TryParse(fields[1], out var parent)) result[pid] = parent;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { SilentCatch.Note(ex, "GateProcessResources: child vanished from proc snapshot"); }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
                if (!Process32First(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception();
                do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; }
                while (Process32Next(snapshot, ref entry));
            }
            finally { CloseHandle(snapshot); }
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var entry in _tracked.Values) entry.Process.Dispose();
        _tracked.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
