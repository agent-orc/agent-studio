using System.Diagnostics;

namespace AgentRunner;

/// <summary>
/// Kills processes left running under a review or coding work root with no
/// daemon watching them any more - the general net for the cases every
/// attempt-scoped reap misses: a worker that crashed after its own workspace
/// directory was already removed (the process keeps a "(deleted)" cwd forever,
/// invisible to any cwd-prefix scan) or one whose record predates this hygiene
/// pass entirely. Runs at daemon startup and on the same periodic cadence as
/// workspace retention, independent of any per-attempt bookkeeping (AGT-2759).
/// <para>
/// AGT-2820 (2026-09-15) widened it past the agent CLIs. On agent-runner-01 four
/// <c>sh -lc dotnet test ...</c> trees sat at <c>ppid=1</c> with a deleted cwd
/// and 0.0% CPU, the oldest for 6.6 hours, each one still occupying the review
/// slot it was launched from; the same host carried 26 orphaned MSBuild worker
/// nodes, the oldest idle for eight days. Neither shape was a <c>claude</c> or a
/// <c>codex</c> process, so the old binary allow-list stepped over all of them.
/// </para>
/// </summary>
public static class CliOrphanSweep
{
    private static readonly string[] TrackedBinaries = ["claude", "codex"];
    private const string DeletedSuffix = " (deleted)";

    /// <summary>
    /// An MSBuild worker node. Never a durable review or coding worker, so an
    /// orphaned one is unambiguously garbage even while its workspace is alive.
    /// </summary>
    private const string MsBuildNodeMarker = "/nodemode:";

    private const int InitPid = 1;

    /// <param name="protectedPaths">
    /// Workspace roots the daemon still owns. A durable worker survives a daemon
    /// restart on purpose and is re-adopted by reconciliation, so it must never
    /// be reaped for merely having been reparented to init.
    /// </param>
    public static int Sweep(
        IReadOnlyList<string> workRootPaths,
        TimeSpan maxAge,
        Action<string> log,
        IEnumerable<string>? protectedPaths = null)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc")) return 0;

        var roots = workRootPaths
            .Where(Directory.Exists)
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))
            .ToArray();
        if (roots.Length == 0) return 0;

        var protectedRoots = (protectedPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))
            .ToArray();

        var now = DateTime.UtcNow;
        var ownPid = Environment.ProcessId;
        var reaped = 0;
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out var pid) || pid == ownPid)
                continue;

            string comm;
            try { comm = File.ReadAllText(Path.Combine(procDir, "comm")).Trim(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string? cwd;
            try
            {
                cwd = new DirectoryInfo(Path.Combine(procDir, "cwd"))
                    .ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (cwd is null) continue;

            var deleted = cwd.EndsWith(DeletedSuffix, StringComparison.Ordinal);
            var normalizedCwd = deleted ? cwd[..^DeletedSuffix.Length] : cwd;
            if (!IsUnder(normalizedCwd, roots)) continue;
            if (IsUnder(normalizedCwd, protectedRoots)) continue;

            var reason = ReapReason(procDir, comm, deleted, normalizedCwd, maxAge, now);
            if (reason is null) continue;
            if (TryReap(pid, reason, log)) reaped++;
        }
        return reaped;
    }

    /// <summary>
    /// Why this process is garbage, or null when it is not. Three shapes, in
    /// order of certainty: its workspace is already gone, it is an orphaned
    /// MSBuild node, or it is a tracked agent CLI past the maximum review budget.
    /// </summary>
    private static string? ReapReason(
        string procDir,
        string comm,
        bool deletedCwd,
        string cwd,
        TimeSpan maxAge,
        DateTime now)
    {
        if (deletedCwd)
            return $"cwd was already removed ({cwd})";

        if (IsOrphanedMsBuildNode(procDir))
            return $"orphaned MSBuild worker node under {cwd}";

        if (!TrackedBinaries.Contains(comm, StringComparer.Ordinal)) return null;
        var age = AgeOf(procDir, now);
        return age is { } value && value >= maxAge
            ? $"age {value.TotalHours:F1}h exceeded the {maxAge.TotalHours:F0}h maximum review budget"
            : null;
    }

    private static bool IsOrphanedMsBuildNode(string procDir)
    {
        try
        {
            var cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' ');
            if (!cmdline.Contains(MsBuildNodeMarker, StringComparison.Ordinal)) return false;
            var status = File.ReadAllLines(Path.Combine(procDir, "status"));
            var parent = status.FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));
            return parent is not null
                   && int.TryParse(parent["PPid:".Length..].Trim(), out var ppid)
                   && ppid == InitPid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static TimeSpan? AgeOf(string procDir, DateTime now)
    {
        if (!int.TryParse(Path.GetFileName(procDir), out var pid)) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return now - process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsUnder(string candidate, IReadOnlyList<string> roots)
        => roots.Any(root =>
            string.Equals(candidate, root, StringComparison.Ordinal)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static bool TryReap(int pid, string reason, Action<string> log)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }

        using (process)
        {
            var age = AgeOf(Path.Combine("/proc", pid.ToString()), DateTime.UtcNow);
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log($"cli-orphan-sweep-kill-failed pid={pid}: {exception.Message}");
                return false;
            }
            log(
                $"cli-process-reaped pid={pid} " +
                $"age={(age is { } value ? value.TotalSeconds.ToString("F0") : "unknown")}s " +
                $"attempt=orphan reason=\"{reason}\"");
            return true;
        }
    }
}
