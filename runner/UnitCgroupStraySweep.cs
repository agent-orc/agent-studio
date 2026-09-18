using System.Diagnostics;
using System.Globalization;

namespace AgentRunner;

/// <summary>
/// One root of the daemon's own directory layout that can name the generation a
/// stray process belonged to.
///
/// <para>When <see cref="AttemptId"/> is set, <see cref="Path"/> is the
/// worktree, workspace, or worker directory of a slot this daemon is adopting,
/// so a process below it belongs to a live generation. When it is null,
/// <see cref="Path"/> is a work root (<c>RUNNER_WORKDIR</c>,
/// <c>RUNNER_REVIEW_WORKDIR</c>, <c>RUNNER_STATE_DIR</c>) and the generation is
/// the name of the directory directly below it, which is the attempt or
/// worktree the process was started in.</para>
/// </summary>
internal sealed record StrayGenerationRoot(string Path, string? AttemptId = null);

/// <summary>What <c>/proc</c> says about one process found in the unit cgroup.</summary>
internal sealed record StrayProcessFacts(
    int Pid,
    DateTime StartedAtUtc,
    string Command,
    string? WorkingDirectory);

/// <summary>A classified stray, ready to be logged and acted on.</summary>
internal sealed record CgroupStray(
    int Pid,
    TimeSpan Age,
    string Command,
    string? Generation,
    bool GenerationIsLive)
{
    public string Describe(string action) => string.Create(
        CultureInfo.InvariantCulture,
        $"[runner] unit cgroup stray pid={Pid} age={Age.TotalHours:0.0}h "
        + $"generation={Generation ?? "unknown"}{(GenerationIsLive ? " (live)" : string.Empty)} "
        + $"action={action} cmd='{Command}'");
}

/// <summary>
/// What one startup sweep did. <c>Found</c> counts every process it could still
/// read, in the unit cgroup and in the strays leaf together; <c>Moved</c> counts
/// only those that were blocking delegation.
/// </summary>
internal sealed record StraySweepOutcome(int Found, int Moved, int Terminated)
{
    internal static StraySweepOutcome None { get; } = new(0, 0, 0);

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"found={Found} moved={Moved} terminated={Terminated}");
}

/// <summary>The facts a sweep needs beyond the unit cgroup itself.</summary>
internal sealed record StraySweepContext(
    TimeSpan RunTimeout,
    IReadOnlyList<StrayGenerationRoot> GenerationRoots);

/// <summary>
/// Empties the role unit's own cgroup before the daemon asks for delegation
/// (AGT-2868).
///
/// <para><b>Why this exists.</b> cgroup v2 enforces the "no internal processes"
/// rule: a cgroup may hold processes or distribute controllers, never both. The
/// daemon itself is parked in <c>daemon/</c> by <c>DelegateSubgroup=</c>, so on
/// a fresh host the unit cgroup is empty and
/// <see cref="WorkerCgroup.TryPrepareDelegationRoot"/> can enable
/// <c>cpu pids</c>. On a host that has run before it is not: MSBuild nodes,
/// test-fixture dev servers, and a stopped <c>git remote-https</c> outlived
/// their workers, and <c>KillMode=process</c> (AGT-2750) carried them across
/// every restart into the unit cgroup. The kernel then answers the controller
/// write with <c>EBUSY</c>, the envelope logs <c>applied=no</c>, and every run
/// continues uncapped. An operator had to kill those processes by hand before
/// delegation succeeded on 18.09.2026.</para>
///
/// <para><b>What it does.</b> Every process directly in the unit cgroup that is
/// not this daemon is logged with its pid, age, command, and the worker
/// generation it belonged to when that can be resolved, and then moved into a
/// <c>strays/</c> leaf. Moving, not killing, is the default: a process that
/// cannot be attributed may still be doing something an operator wants, and the
/// unit cgroup is empty either way, which is all delegation needs. A stray that
/// demonstrably belongs to a finished generation and is older than the run
/// timeout is additionally killed, because nothing can be waiting for it any
/// more. The move happens first in both cases, so a process that resists the
/// signal, or lingers as a zombie, still cannot block the controller write.</para>
///
/// <para>Membership is the only signal used. A name-based <c>pkill</c> would
/// reach the operator's own shell, an unrelated service, or a concurrent build
/// on a shared host; the unit cgroup is exactly the set this daemon's unit is
/// responsible for.</para>
/// </summary>
internal static class UnitCgroupStraySweep
{
    /// <summary>The leaf strays are parked in. A sibling of <c>daemon/</c> and the worker cgroups.</summary>
    internal const string LeafName = "strays";

    // -------------------------------------------------------------- pure decision

    /// <summary>
    /// Name the generation a process belonged to, from the directory it was
    /// started in. The longest matching root wins, so a slot's own worktree
    /// outranks the work root that contains it.
    /// </summary>
    internal static (string? Generation, bool IsLive) Attribute(
        string? workingDirectory,
        IReadOnlyList<StrayGenerationRoot> roots)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return (null, false);
        string? generation = null;
        var isLive = false;
        var matchedLength = -1;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root.Path)) continue;
            var normalized = Normalize(root.Path);
            if (!IsAtOrBelow(workingDirectory, normalized)) continue;
            if (normalized.Length <= matchedLength) continue;
            var name = root.AttemptId ?? FirstSegmentBelow(workingDirectory, normalized);
            if (name is null) continue;
            matchedLength = normalized.Length;
            generation = name;
            isLive = root.AttemptId is not null;
        }
        return (generation, isLive);
    }

    /// <summary>
    /// A stray is killed only when both facts hold: it is attributable to a
    /// generation this daemon is not adopting, and it has outlived the longest a
    /// run may legitimately take. Anything else is parked, never killed, so an
    /// unattributable process is a hygiene item rather than a casualty.
    /// </summary>
    internal static bool ShouldTerminate(CgroupStray stray, TimeSpan runTimeout)
        => stray.Generation is not null
           && !stray.GenerationIsLive
           && stray.Age > runTimeout;

    // ----------------------------------------------------------- bounded side effects

    /// <summary>
    /// Move (and where warranted kill) everything sitting directly in
    /// <paramref name="unitRoot"/>, and re-judge what an earlier generation
    /// already parked. Returns what happened, so the caller can say it in one
    /// line. Never throws: a sweep that cannot run leaves delegation to fail with
    /// the message it already had.
    /// </summary>
    internal static StraySweepOutcome Sweep(
        string unitRoot,
        StraySweepContext context,
        Action<string> log,
        Func<int, StrayProcessFacts?>? inspect = null,
        Func<int, bool>? terminate = null,
        DateTime? nowUtc = null,
        int? selfPid = null)
    {
        inspect ??= ReadProcessFacts;
        terminate ??= pid => Kill(pid, log);
        var now = nowUtc ?? DateTime.UtcNow;
        var self = selfPid ?? Environment.ProcessId;

        var leaf = Path.Combine(unitRoot, LeafName);
        var procs = Path.Combine(leaf, "cgroup.procs");
        int[] blocking;
        int[] parked;
        try
        {
            blocking = ReadPids(Path.Combine(unitRoot, "cgroup.procs"))
                .Where(pid => pid != self)
                .ToArray();
            // Strays a previous generation parked are re-judged here. Parking is
            // deliberately reversible-safe rather than final, so without this
            // pass a process that was merely too young, or not yet attributable,
            // would sit in the leaf for the life of the host and keep holding the
            // file descriptors and inotify instances it was parked with.
            parked = ReadPids(procs)
                .Where(pid => pid != self && !blocking.Contains(pid))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return StraySweepOutcome.None;
        }
        if (blocking.Length == 0 && parked.Length == 0) return StraySweepOutcome.None;

        try
        {
            Directory.CreateDirectory(leaf);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"[runner] cannot park strays of {unitRoot}: {exception.Message}");
            return StraySweepOutcome.None;
        }

        var found = 0;
        var moved = 0;
        var doomed = new List<CgroupStray>();
        foreach (var pid in blocking.Concat(parked))
        {
            var facts = inspect(pid);
            if (facts is null) continue; // exited between the listing and the read
            found++;
            var (generation, isLive) = Attribute(facts.WorkingDirectory, context.GenerationRoots);
            var stray = new CgroupStray(
                pid,
                now - facts.StartedAtUtc,
                facts.Command,
                generation,
                isLive);
            var kill = ShouldTerminate(stray, context.RunTimeout);
            var alreadyParked = !blocking.Contains(pid);
            log(stray.Describe(kill ? "terminated" : alreadyParked ? "parked" : "moved"));
            if (!alreadyParked && TryMove(procs, pid)) moved++;
            if (kill) doomed.Add(stray);
        }

        var terminated = doomed.Count(stray => terminate(stray.Pid));
        var outcome = new StraySweepOutcome(found, moved, terminated);
        log($"[runner] unit cgroup sweep {unitRoot} {outcome.Describe()}");
        return outcome;
    }

    // ---------------------------------------------------------------------- local

    private static IEnumerable<int> ReadPids(string procsPath)
    {
        if (!File.Exists(procsPath)) return [];
        return File.ReadAllLines(procsPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => int.TryParse(line, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .Distinct()
            .ToArray();
    }

    private static bool TryMove(string procsPath, int pid)
    {
        try
        {
            // cgroup.procs takes exactly one pid per write; the kernel ignores
            // the truncation an ordinary file would perform.
            File.WriteAllText(procsPath, pid.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // SIGKILL, because a stray found in state T (stopped) ignores every signal a
    // stopped process may ignore. The 18.09.2026 host needed exactly that for its
    // leftover git remote-https. AGT-2870: the pid is read out of the unit's
    // cgroup.procs, so it goes through the shared guard, which refuses the
    // broadcast pids and this daemon's own pid and parent - the sweep runs inside
    // the very cgroup it is emptying.
    private static bool Kill(int pid, Action<string>? log)
        => ProcessSignalGuard.TryKillSingle(pid, "unit-cgroup-stray", log: log);


    /// <summary>
    /// Age, command line, and working directory of a pid, from <c>/proc</c>. A
    /// pid that vanishes mid-read answers null, which the sweep treats as "gone
    /// already".
    /// </summary>
    internal static StrayProcessFacts? ReadProcessFacts(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var command = CommandLine(pid) ?? process.ProcessName;
            string? cwd = null;
            if (OperatingSystem.IsLinux())
                cwd = new DirectoryInfo($"/proc/{pid}/cwd")
                    .ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return new StrayProcessFacts(pid, process.StartTime.ToUniversalTime(), command, cwd);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? CommandLine(int pid)
    {
        try
        {
            var raw = File.ReadAllText($"/proc/{pid}/cmdline");
            var command = string.Join(' ', raw.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            if (command.Length == 0) return null;
            return command.Length <= 160 ? command : command[..160];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsAtOrBelow(string candidate, string root)
    {
        var normalized = Normalize(candidate);
        return string.Equals(normalized, root, StringComparison.Ordinal)
               || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? FirstSegmentBelow(string candidate, string root)
    {
        var normalized = Normalize(candidate);
        if (normalized.Length <= root.Length) return null;
        var relative = normalized[(root.Length + 1)..];
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        var segment = separator < 0 ? relative : relative[..separator];
        return segment.Length == 0 ? null : segment;
    }
}
