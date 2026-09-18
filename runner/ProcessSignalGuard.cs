using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AgentRunner;

/// <summary>Why a signal the runner was about to send was refused (AGT-2870).</summary>
public enum SignalRefusal
{
    /// <summary>Nothing objected; the signal may be sent.</summary>
    None,

    /// <summary>
    /// The target is not a process id a runner may ever signal: <c>-1</c> (every
    /// process of this uid), <c>0</c> (the caller's own process group) or
    /// <c>1</c> (init). <c>default(int)</c> and the <c>?? -1</c> fallback of an
    /// unrecorded worker identity both land here.
    /// </summary>
    NotASignallablePid,

    /// <summary>The target is this daemon itself, or the process that started it.</summary>
    SelfOrAncestor,

    /// <summary>
    /// The pid is live but it is not the process the slot recorded: the original
    /// worker died and the kernel handed its number to somebody else.
    /// </summary>
    IdentityMismatch,

    /// <summary>The target process group is <c>&lt;= 1</c> or is this daemon's own group.</summary>
    NotASignallableProcessGroup,

    /// <summary>The cgroup is not a <c>worker-*</c> directory below this daemon's own unit.</summary>
    ForeignCgroup,
}

/// <summary>A refusal decision and the sentence that explains it in the journal.</summary>
public readonly record struct SignalVerdict(SignalRefusal Refusal, string Reason)
{
    public static SignalVerdict Allowed { get; } = new(SignalRefusal.None, "allowed");

    public bool IsAllowed => Refusal == SignalRefusal.None;
}

/// <summary>
/// Pure policy for every signal the runner sends (AGT-2870).
///
/// <para><b>The incident.</b> On 18.09.2026 at 09:41:44 every process of uid 1000
/// on the runner host received SIGKILL in the same second: both daemons, the
/// user manager, the operator's ssh session scope and two coding workers. The
/// cause was <c>DurableAgentProcess.Attach(slot).Kill()</c> on a slot whose
/// worker identity had not been persisted. <c>Attach</c> substitutes <c>-1</c>
/// for a null <c>ProcessId</c>, and on Linux .NET that sentinel survives every
/// gate in the runtime: <c>Process.GetProcessById</c> admits a pid when
/// <c>kill(pid, 0)</c> succeeds or returns EPERM, and <c>kill(-1, 0)</c> succeeds
/// because signal 0 to "every process I may signal" is a no-op that reports
/// success. <c>HasExited</c> is then false and
/// <c>Kill(entireProcessTree: true)</c> issues <c>kill(-1, SIGSTOP)</c> followed
/// by <c>kill(-1, SIGKILL)</c>.</para>
///
/// <para><b>The rule.</b> The runtime will not refuse these targets, so the
/// runner refuses them itself, before the syscall, at every entry point rather
/// than at the one that happened to fire. A refusal is never fatal: the worst
/// case of not signalling is a leftover process that the next generation's
/// sweep finds, and the worst case of signalling wrongly is this incident.</para>
///
/// <para>Everything here is a decision over values the caller passes in, so the
/// matrix is tested directly and no test has to own a pid to exercise it.</para>
/// </summary>
public static class ProcessSignalPolicy
{
    /// <summary>The lowest pid a runner may signal. 1 is init; 0 and -1 are broadcasts.</summary>
    public const int LowestSignallablePid = 2;

    /// <summary>
    /// How far a process start time read from the host may differ from the one
    /// the slot persisted. Matches the tolerance
    /// <see cref="DurableAgentProcess.VerifyLive"/> already uses for liveness, so
    /// a worker is not proven live by one rule and unkillable by another.
    /// </summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>The name every worker cgroup carries, below the daemon's own unit cgroup.</summary>
    private const string WorkerCgroupPrefix = "worker-";

    /// <summary>
    /// May this daemon send a signal to <paramref name="pid"/>? Start times are
    /// optional: a sweep that found a pid in <c>/proc</c> has nothing to compare
    /// against, and the pid floor plus the self check still apply to it.
    /// </summary>
    public static SignalVerdict ForPid(
        int pid,
        int ownPid,
        int ownParentPid,
        DateTime? expectedStartUtc = null,
        DateTime? observedStartUtc = null)
    {
        if (pid < LowestSignallablePid)
            return new SignalVerdict(
                SignalRefusal.NotASignallablePid,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"pid {pid} is not a signallable process id: a pid below {LowestSignallablePid} means this uid's whole process list, the caller's own process group, or init"));

        if (pid == ownPid)
            return new SignalVerdict(
                SignalRefusal.SelfOrAncestor,
                string.Create(CultureInfo.InvariantCulture, $"pid {pid} is this daemon itself"));

        if (pid == ownParentPid)
            return new SignalVerdict(
                SignalRefusal.SelfOrAncestor,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"pid {pid} is the parent of this daemon"));

        if (expectedStartUtc is { } expected && expected != DateTime.MinValue)
        {
            if (observedStartUtc is not { } observed)
                return new SignalVerdict(
                    SignalRefusal.IdentityMismatch,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"pid {pid} has no readable start time to check against the recorded worker identity"));

            if ((observed - expected).Duration() > StartTimeTolerance)
                return new SignalVerdict(
                    SignalRefusal.IdentityMismatch,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"pid {pid} started at {observed:O} but the slot recorded {expected:O}, so the number was reused by another process"));
        }

        return SignalVerdict.Allowed;
    }

    /// <summary>
    /// May this daemon send a signal to the whole process group
    /// <paramref name="pgid"/>? A negative pid argument is a group broadcast, so
    /// a pgid of 1 would become the same <c>kill(-1, ...)</c> the incident was.
    /// </summary>
    public static SignalVerdict ForProcessGroup(int pgid, int ownProcessGroupId)
    {
        if (pgid < LowestSignallablePid)
            return new SignalVerdict(
                SignalRefusal.NotASignallableProcessGroup,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"process group {pgid} is not signallable because kill(-{pgid}) would reach every process of this uid rather than one group"));

        if (pgid == ownProcessGroupId)
            return new SignalVerdict(
                SignalRefusal.NotASignallableProcessGroup,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"process group {pgid} is this daemon's own group"));

        return SignalVerdict.Allowed;
    }

    /// <summary>
    /// May this daemon empty the cgroup at <paramref name="candidate"/>? Only a
    /// <c>worker-*</c> directory directly below its own delegated unit cgroup is
    /// the runner's to kill. Writing <c>cgroup.kill</c> one level up would take
    /// the daemon and every sibling worker with it, so a marker file that names
    /// a stale, foreign or truncated path is refused rather than followed.
    /// <paramref name="delegationRoot"/> is null on a host without a delegated
    /// subtree and in tests that model the hierarchy in a temporary directory;
    /// the structural checks still apply there.
    /// </summary>
    public static SignalVerdict ForCgroup(string? candidate, string? delegationRoot)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return new SignalVerdict(SignalRefusal.ForeignCgroup, "no cgroup path was recorded");

        var path = Normalize(candidate);
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || string.Equals(parent, path, StringComparison.Ordinal))
            return new SignalVerdict(
                SignalRefusal.ForeignCgroup,
                $"'{candidate}' is a filesystem root, not a worker cgroup");

        if (!Path.GetFileName(path).StartsWith(WorkerCgroupPrefix, StringComparison.Ordinal))
            return new SignalVerdict(
                SignalRefusal.ForeignCgroup,
                $"'{candidate}' is not a '{WorkerCgroupPrefix}*' cgroup, so it is not one worker's subtree");

        if (delegationRoot is not null
            && !string.Equals(parent, Normalize(delegationRoot), StringComparison.Ordinal))
            return new SignalVerdict(
                SignalRefusal.ForeignCgroup,
                $"'{candidate}' is not below this daemon's delegated root '{delegationRoot}'");

        return SignalVerdict.Allowed;
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

/// <summary>
/// The one place the runner turns a signal decision into a syscall (AGT-2870).
///
/// <para>Every kill entry point routes through here so the guard cannot be
/// forgotten at a new call site: the sentinel pid that caused the incident was
/// not introduced by the code that killed the host, it was introduced years
/// earlier and stayed harmless until a second caller appeared. Refusals are
/// logged with their reason, because a signal the runner declines to send is a
/// fact an operator has to be able to find in the journal.</para>
/// </summary>
internal static class ProcessSignalGuard
{
    /// <summary>The daemon's own pid, and the pid that started it. Read once.</summary>
    private static readonly int OwnPid = Environment.ProcessId;

    private static readonly Lazy<int> OwnParentPid = new(ReadOwnParentPid);
    private static readonly Lazy<int> OwnProcessGroupId = new(ReadOwnProcessGroupId);

    /// <summary>
    /// Kill one process and its descendants, unless policy refuses the pid. The
    /// caller passes the start time its slot recorded when it has one, which is
    /// what makes a recycled pid number distinguishable from the worker.
    /// Returns whether the signal was actually sent.
    /// </summary>
    public static bool TryKillTree(
        int pid,
        string context,
        DateTime? expectedStartUtc = null,
        Action<string>? log = null)
        => TryKill(pid, context, expectedStartUtc, entireProcessTree: true, log);

    /// <summary>
    /// Kill one process without touching its descendants, unless policy refuses
    /// the pid. Used where the caller enumerated a membership (a cgroup, a
    /// <c>/proc</c> sweep) and every member is signalled on its own.
    /// </summary>
    public static bool TryKillSingle(
        int pid,
        string context,
        DateTime? expectedStartUtc = null,
        Action<string>? log = null)
        => TryKill(pid, context, expectedStartUtc, entireProcessTree: false, log);

    /// <summary>
    /// Send <paramref name="signal"/> to one process, unless policy refuses the
    /// pid. For signals other than SIGKILL, where the runtime has no
    /// process-tree equivalent. Returns whether the signal was actually sent.
    /// </summary>
    public static bool TrySignal(int pid, int signal, string context, Action<string>? log = null)
    {
        var verdict = ProcessSignalPolicy.ForPid(pid, OwnPid, OwnParentPid.Value);
        if (!verdict.IsAllowed)
        {
            Refuse(context, verdict, log);
            return false;
        }
        try
        {
            return kill(pid, signal) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Send <paramref name="signal"/> to a whole process group, unless policy
    /// refuses the group. Returns whether the signal was actually sent.
    /// </summary>
    public static bool TrySignalProcessGroup(
        int processGroupId,
        int signal,
        string context,
        Action<string>? log = null)
    {
        var verdict = ProcessSignalPolicy.ForProcessGroup(processGroupId, OwnProcessGroupId.Value);
        if (!verdict.IsAllowed)
        {
            Refuse(context, verdict, log);
            return false;
        }
        try
        {
            return kill(-processGroupId, signal) == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this cgroup directory is one this daemon may empty. The caller
    /// keeps the side effect so the check reads at the call site.
    /// </summary>
    public static bool MayEmptyCgroup(
        string? cgroupDirectory,
        string? delegationRoot,
        string context,
        Action<string>? log = null)
    {
        var verdict = ProcessSignalPolicy.ForCgroup(cgroupDirectory, delegationRoot);
        if (verdict.IsAllowed) return true;
        Refuse(context, verdict, log);
        return false;
    }

    /// <summary>The daemon's own process group id, for callers that filter before they ask.</summary>
    public static int OwnGroupId => OwnProcessGroupId.Value;

    private static bool TryKill(
        int pid,
        string context,
        DateTime? expectedStartUtc,
        bool entireProcessTree,
        Action<string>? log)
    {
        // The pid floor is checked before Process.GetProcessById, because that
        // call is exactly the gate the sentinel walks through.
        var preliminary = ProcessSignalPolicy.ForPid(pid, OwnPid, OwnParentPid.Value);
        if (!preliminary.IsAllowed)
        {
            Refuse(context, preliminary, log);
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;

            var verdict = ProcessSignalPolicy.ForPid(
                pid,
                OwnPid,
                OwnParentPid.Value,
                expectedStartUtc,
                ReadStartTimeUtc(process));
            if (!verdict.IsAllowed)
            {
                Refuse(context, verdict, log);
                return false;
            }

            process.Kill(entireProcessTree);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or IOException)
        {
            // Already gone, or not ours to signal. Every caller treats an
            // unkilled process as a leftover for the next sweep.
            return false;
        }
    }

    private static void Refuse(string context, SignalVerdict verdict, Action<string>? log)
        => log?.Invoke($"[runner] signal-refused context={context} rule={verdict.Refusal} reason=\"{verdict.Reason}\"");

    private static DateTime? ReadStartTimeUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The daemon's parent pid. Unreadable outside Linux and on a hidepid host,
    /// where 0 is returned: 0 is below the pid floor, so it can never match a
    /// candidate that got this far.
    /// </summary>
    private static int ReadOwnParentPid()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        try
        {
            var stat = File.ReadAllText("/proc/self/stat");
            // The comm field is parenthesised and may itself contain spaces, so
            // the fields are counted from the last ')' rather than from the start.
            var afterComm = stat.LastIndexOf(')');
            if (afterComm < 0) return 0;
            var fields = stat[(afterComm + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // fields[0] is state, fields[1] is ppid.
            return fields.Length > 1
                   && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid)
                ? ppid
                : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The daemon's own process group. A failed read returns 0, which
    /// <see cref="ProcessSignalPolicy.ForProcessGroup"/> already refuses as a
    /// target, so an unreadable group cannot widen what may be signalled.
    /// </summary>
    private static int ReadOwnProcessGroupId()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        try { return getpgid(OwnPid); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);
}
