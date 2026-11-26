using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// RT-3 (RT-D10 B, fingerprint part only): describes the test envelope a
/// post-merge run's commands see, without values. Comparing fingerprints and
/// the reference envelope is a later slice (RT-6).
/// </summary>
internal static class PostMergeEnvelopeProbe
{
    internal const string WorkerKind = "worker";
    internal const string DaemonKind = "daemon";
    internal const string ProcessKind = "process";
    internal const string NoKind = "none";

    /// <summary>Digest of the sorted variable names passed from the host allowlist. Values are never hashed.</summary>
    internal static string AllowlistDigest(IEnumerable<string> passedNames)
        => Digest(string.Join("\n", passedNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));

    /// <summary>Digest of the project's declared environment as sorted <c>name=value</c> lines.</summary>
    internal static string DeclaredEnvironmentDigest(IReadOnlyDictionary<string, string> declared)
        => Digest(string.Join("\n", declared.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value)));

    /// <summary>
    /// The processor count a .NET test host in this envelope sees: a declared
    /// <c>DOTNET_PROCESSOR_COUNT</c>, else the <c>cpu.max</c> quota in whole cores
    /// capped by this process's count, else this process's count.
    /// </summary>
    internal static int ProcessorCount(IReadOnlyDictionary<string, string> declared, string? cpuMax, int own)
    {
        if (declared.TryGetValue("DOTNET_PROCESSOR_COUNT", out var value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0)
            return count;
        var parts = cpuMax?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is { Length: 2 } && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period) && period > 0)
            return (int)Math.Clamp((quota + period - 1) / period, 1, Math.Max(1, own));
        return own;
    }

    /// <summary>
    /// The cgroup of this process when commands do not get a worker leaf:
    /// <c>daemon</c> for the delegated daemon leaf, <c>process</c> for any other
    /// cgroup v2 path, <c>none</c> off Linux or without a readable cgroup.
    /// </summary>
    internal static (string Kind, string? CpuMax) OwnCgroup()
    {
        if (!OperatingSystem.IsLinux()) return (NoKind, null);
        try
        {
            var line = File.ReadAllLines("/proc/self/cgroup").FirstOrDefault(item => item.StartsWith("0::", StringComparison.Ordinal));
            if (line is null) return (NoKind, null);
            var path = line[3..].Trim();
            var kind = path.TrimEnd('/').EndsWith("/" + WorkerCgroup.DaemonLeafName, StringComparison.Ordinal)
                ? DaemonKind : ProcessKind;
            return (kind, ReadCpuMax(Path.Combine(WorkerCgroup.DefaultMountRoot, path.TrimStart('/'))));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (NoKind, null);
        }
    }

    internal static string? ReadCpuMax(string cgroupDirectory)
    {
        try
        {
            var file = Path.Combine(cgroupDirectory, "cpu.max");
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file).Trim();
            return text.Length is > 0 and <= 64 ? text : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>suite=shards</c> in run order, for example <c>backend-routine=4,runner-routine=1</c>.</summary>
    internal static string ShardPlan(IEnumerable<(string Suite, string Shards)> suites)
    {
        var plan = string.Join(",", suites.Select(item => item.Suite + "=" + item.Shards));
        return plan.Length <= 512 ? plan : plan[..512];
    }

    private static string Digest(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

/// <summary>Samples the host load per core every 15 seconds while a run executes and keeps the peak.</summary>
internal sealed class PostMergeLoadSampler : IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly Func<double?> _probe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private double? _peak;

    internal PostMergeLoadSampler(double? start, Func<double?> probe, TimeSpan? interval = null)
    {
        _peak = start;
        _probe = probe;
        _loop = Loop(interval ?? Interval);
    }

    internal double? Peak { get { lock (_gate) return _peak; } }

    private async Task Loop(TimeSpan interval)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                double? sample;
                try { sample = _probe(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { sample = null; }
                if (sample is not { } value || !double.IsFinite(value) || value < 0) continue;
                lock (_gate) _peak = _peak is { } peak ? Math.Max(peak, value) : value;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
