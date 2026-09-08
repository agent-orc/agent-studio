using System.Diagnostics;

namespace AgentRunner;

/// <summary>
/// Kills tracked-CLI (claude, codex) processes left running under a review or
/// coding work root with no daemon watching them any more - the general net
/// for the cases every attempt-scoped reap misses: a worker that crashed after
/// its own workspace directory was already removed (the process keeps a
/// "(deleted)" cwd forever, invisible to any cwd-prefix scan) or one whose
/// record predates this hygiene pass entirely. Runs at daemon startup and on
/// the same periodic cadence as workspace retention, independent of any
/// per-attempt bookkeeping (AGT-2759).
/// </summary>
public static class CliOrphanSweep
{
    private static readonly string[] TrackedBinaries = ["claude", "codex"];
    private const string DeletedSuffix = " (deleted)";

    public static int Sweep(
        IReadOnlyList<string> workRootPaths,
        TimeSpan maxAge,
        Action<string> log)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc")) return 0;

        var roots = workRootPaths
            .Where(Directory.Exists)
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))
            .ToArray();
        if (roots.Length == 0) return 0;

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
            if (!TrackedBinaries.Contains(comm, StringComparer.Ordinal)) continue;

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
            var underRoot = roots.Any(root =>
                string.Equals(normalizedCwd, root, StringComparison.Ordinal)
                || normalizedCwd.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            if (!underRoot) continue;

            if (TryReap(pid, deleted, normalizedCwd, maxAge, now, log))
                reaped++;
        }
        return reaped;
    }

    private static bool TryReap(
        int pid,
        bool deletedCwd,
        string cwd,
        TimeSpan maxAge,
        DateTime now,
        Action<string> log)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }

        using (process)
        {
            TimeSpan age;
            try { age = now - process.StartTime.ToUniversalTime(); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
            if (!deletedCwd && age < maxAge) return false;

            var reason = deletedCwd
                ? $"cwd was already removed ({cwd})"
                : $"age {age.TotalHours:F1}h exceeded the {maxAge.TotalHours:F0}h maximum review budget";
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                log($"cli-orphan-sweep-kill-failed pid={pid}: {exception.Message}");
                return false;
            }
            log($"cli-process-reaped pid={pid} age={age.TotalSeconds:F0}s attempt=orphan reason=\"{reason}\"");
            return true;
        }
    }
}
