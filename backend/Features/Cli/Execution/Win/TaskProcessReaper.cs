using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Cli;

/// <summary>
/// Contains a task-run's CLI process and <b>every</b> process it later spawns
/// — including ones that break away from the parent→child PID chain (the
/// agent's Playwright capture server, a detached <c>node serve.cjs</c>, an
/// <c>ng serve</c>) — and reaps the whole group at run-end.
///
/// <para>
/// Implemented on top of a Win32 <i>Job Object</i> or a Linux session/process
/// group. The Windows OS primitive whose
/// API names — <c>CreateJobObject</c> / <c>AssignProcessToJobObject</c> —
/// the P/Invoke layer below keeps verbatim). The wrapper is named after the
/// task-run it scopes, NOT the OS primitive, so it does not collide with the
/// domain's <c>Job</c>→<c>Task</c> naming.
/// </para>
/// <para>
/// <b>Why this exists.</b> The runner's tree-kill
/// (<c>Process.Kill(entireProcessTree)</c> / <c>taskkill /T</c>) walks the
/// live parent→child chain. A grandchild that re-parents or daemonises is no
/// longer in that chain, so tree-kill misses it and it keeps running —
/// holding the run's worktree directory open. The post-run
/// <c>git worktree remove</c> then fails "Device or resource busy", the
/// worktree directory is orphaned, and every later re-pick collides with it
/// (<c>git worktree add</c> on an existing dir) → <c>pick-reverted-no-run</c>
/// loop (AGT-1791). The observed leaker is the agent's Playwright capture
/// server, which outlives the CLI run by design.
/// </para>
/// <para>
/// Job-object membership is inherited by everything a member spawns and is
/// NOT severed by detaching, so <see cref="Terminate"/> at run-end reaps the
/// whole subtree regardless of re-parenting. The group is created with
/// <c>KILL_ON_JOB_CLOSE</c> so even if <see cref="Terminate"/> is never
/// reached, disposing the handle still kills any stragglers.
/// </para>
/// <para>
/// <b>Best-effort and zero-regression.</b> On Linux the caller first invokes
/// <see cref="WrapStartInfoForProcessGroup"/>, which launches the CLI through
/// <c>setsid --wait</c>. This establishes the group before any CLI child can
/// start, avoiding the race inherent in a parent-side <c>setpgid</c> call.
/// <see cref="CreateForProcess"/> returns <c>null</c> on unsupported platforms
/// or if the OS refuses ownership, and the caller keeps its existing tree-kill
/// behaviour. The group only ever contains the run's own process subtree, so
/// terminating it can never touch the backend or another run.
/// </para>
/// </summary>
internal sealed class TaskProcessReaper : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _handle;
    private readonly int? _unixProcessGroupId;
    private readonly Process? _rootProcess;
    private bool _terminated;

    private TaskProcessReaper(IntPtr handle, int? unixProcessGroupId = null, Process? rootProcess = null)
    {
        _handle = handle;
        _unixProcessGroupId = unixProcessGroupId;
        _rootProcess = rootProcess;
    }

    /// <summary>
    /// On Linux, replace the executable with <c>setsid --wait</c> while
    /// preserving the original argument vector. Returns true when wrapping was
    /// applied. Other platforms and hosts without util-linux remain on the
    /// managed tree-kill fallback.
    /// </summary>
    internal static bool WrapStartInfoForProcessGroup(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var setsid = File.Exists("/usr/bin/setsid")
            ? "/usr/bin/setsid"
            : File.Exists("/bin/setsid") ? "/bin/setsid" : null;
        if (setsid is null || string.IsNullOrWhiteSpace(startInfo.FileName)) return false;
        if (!string.IsNullOrWhiteSpace(startInfo.Arguments)) return false;

        var executable = startInfo.FileName;
        var arguments = startInfo.ArgumentList.ToArray();
        startInfo.FileName = setsid;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add(executable);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return true;
    }

    /// <summary>
    /// Create a kill-on-close process group and assign <paramref name="process"/>
    /// to it. Returns null (and leaves the caller on its tree-kill fallback) on
    /// non-Windows or any failure — assignment must happen right after spawn,
    /// before the CLI has a chance to spawn the helpers we want to contain.
    /// </summary>
    public static TaskProcessReaper? CreateForProcess(Process process, ILogger? logger = null)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var processGroupId = -1;
                for (var attempt = 0; attempt < 25; attempt++)
                {
                    processGroupId = getpgid(process.Id);
                    if (processGroupId == process.Id || process.HasExited) break;
                    Thread.Sleep(10);
                }
                if (processGroupId <= 0 || processGroupId != process.Id)
                {
                    logger?.LogDebug(
                        "TaskProcessReaper: Linux child PID {Pid} is in process group {ProcessGroupId}, not an owned group; falling back to tree-kill",
                        process.Id,
                        processGroupId);
                    return null;
                }
                return new TaskProcessReaper(IntPtr.Zero, processGroupId, process);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "TaskProcessReaper: Linux process-group discovery failed; falling back to tree-kill");
                return null;
            }
        }

        if (!OperatingSystem.IsWindows()) return null;

        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                logger?.LogDebug("TaskProcessReaper: CreateJobObject failed (Win32 {Err}); falling back to tree-kill", Marshal.GetLastWin32Error());
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, infoPtr, (uint)len))
                {
                    logger?.LogDebug("TaskProcessReaper: SetInformationJobObject failed (Win32 {Err}); falling back to tree-kill", Marshal.GetLastWin32Error());
                    CloseHandle(job);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                // Process already exited, access denied, or an OS nested-job
                // limit — nothing to contain or nothing we can do. Drop the
                // group; the caller's tree-kill stays in force.
                logger?.LogDebug("TaskProcessReaper: AssignProcessToJobObject failed (Win32 {Err}); falling back to tree-kill", Marshal.GetLastWin32Error());
                CloseHandle(job);
                return null;
            }

            return new TaskProcessReaper(job);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TaskProcessReaper.CreateForProcess: best-effort, falling back to tree-kill");
            if (job != IntPtr.Zero)
            {
                try { CloseHandle(job); } catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper: handle close best-effort"); }
            }
            return null;
        }
    }

    /// <summary>
    /// Kill every process in the group <b>now</b> — the direct CLI child and all
    /// descendants, including detached ones tree-kill would miss. Idempotent.
    /// </summary>
    public void Terminate()
    {
        lock (_gate)
        {
            if (_terminated) return;
            if (_unixProcessGroupId is { } processGroupId)
            {
                try
                {
                    if (kill(-processGroupId, SigTerm) != 0 && Marshal.GetLastWin32Error() != Esrch)
                        throw new InvalidOperationException($"kill(SIGTERM) failed with errno {Marshal.GetLastWin32Error()}.");
                    Thread.Sleep(100);
                    if (kill(-processGroupId, 0) == 0) kill(-processGroupId, SigKill);
                    try { _rootProcess?.WaitForExit(2_000); }
                    catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper.Terminate: Linux root wait best-effort"); }
                    WaitForUnixProcessGroupExit(processGroupId, TimeSpan.FromSeconds(2));
                }
                catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper.Terminate: Linux group best-effort"); }
                finally { _terminated = true; }
                return;
            }

            if (_handle == IntPtr.Zero) return;
            try { TerminateJobObject(_handle, 1); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper.Terminate: best-effort"); }
            finally { _terminated = true; }
        }
    }

    /// <summary>
    /// Close the group handle. With <c>KILL_ON_JOB_CLOSE</c> this also kills any
    /// member still alive that <see cref="Terminate"/> did not already reap.
    /// </summary>
    public void Dispose()
    {
        if (_unixProcessGroupId.HasValue)
        {
            Terminate();
            return;
        }
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;
            try { CloseHandle(_handle); }
            catch (Exception __ex) { SilentCatch.Note(__ex, "TaskProcessReaper.Dispose: best-effort"); }
            finally { _handle = IntPtr.Zero; }
        }
    }

    // ── Win32 P/Invoke surface (OS API names kept verbatim) ─────────────

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const int Esrch = 3;

    private static void WaitForUnixProcessGroupExit(int processGroupId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (kill(-processGroupId, 0) != 0 && Marshal.GetLastWin32Error() == Esrch) return;
            Thread.Sleep(25);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
