using AgentStudio.Git;
using System.Diagnostics;

namespace AgentRunner;

/// <summary>Applies the server's stale lock decision to the runner-owned project cache.</summary>
internal sealed class ProjectCacheGitLockGuard
{
    private readonly Func<string, DateTime, GitLockOwnership> _ownership;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    internal ProjectCacheGitLockGuard(Func<string, DateTime, GitLockOwnership>? ownership = null,
        TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _ownership = ownership ?? ProbeOwnership;
        _time = time ?? TimeProvider.System;
        _wait = wait ?? ((delay, ct) => Task.Delay(delay, ct));
    }

    internal async Task EnsureWritableAsync(string repository, Action<string> log, CancellationToken ct)
    {
        var git = Path.Combine(repository, ".git");
        if (!Directory.Exists(git)) return;
        var remaining = new List<string>();
        foreach (var path in LockFiles(git))
        {
            try
            {
                var info = new FileInfo(path);
                var stamp = (info.CreationTimeUtc, info.LastWriteTimeUtc, info.Length);
                var age = _time.GetUtcNow().UtcDateTime - stamp.LastWriteTimeUtc;
                var owner = GitStaleLockPolicy.NeedsOwnershipProbe(age, GitStaleLockPolicy.DefaultThreshold)
                    ? _ownership(repository, stamp.LastWriteTimeUtc) : GitLockOwnership.Unknown;
                if (GitStaleLockPolicy.Decide(age, GitStaleLockPolicy.DefaultThreshold, owner)
                    != GitLockVerdict.Clear)
                {
                    remaining.Add(path);
                    continue;
                }
                info.Refresh();
                if (!info.Exists || (info.CreationTimeUtc, info.LastWriteTimeUtc, info.Length) != stamp)
                {
                    remaining.Add(path);
                    continue;
                }
                File.Delete(path);
                log($"project-cache-stale-git-lock-cleared repo={repository} lock={path} age={GitStaleLockPolicy.FormatAge(age)}");
            }
            catch (IOException) { remaining.Add(path); }
            catch (UnauthorizedAccessException) { remaining.Add(path); }
        }
        if (remaining.Count == 0) return;
        await _wait(GitStaleLockPolicy.DefaultWait, ct);
        foreach (var path in remaining.Where(File.Exists))
            log($"project-cache-git-lock-kept repo={repository} lock={path}");
    }

    private static IEnumerable<string> LockFiles(string git)
    {
        foreach (var path in GitStaleLockPolicy.FixedLockPaths(git, git))
        {
            if (File.Exists(path)) yield return path;
        }
        var refs = Path.Combine(git, "refs");
        if (!Directory.Exists(refs)) yield break;
        foreach (var path in Directory.EnumerateFiles(refs, "*.lock", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            yield return path;
    }

    private static GitLockOwnership ProbeOwnership(string repository, DateTime lockWrittenUtc)
    {
        try
        {
            var root = Path.GetFullPath(repository);
            if (OperatingSystem.IsLinux())
            {
                foreach (var processDir in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(processDir), out var pid)) continue;
                    string name;
                    try { name = File.ReadAllText(Path.Combine(processDir, "comm")).Trim(); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { return GitLockOwnership.Unknown; }
                    if (name != "git") continue;
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        if (process.HasExited || process.StartTime.ToUniversalTime()
                            > GitStaleLockPolicy.OwnerStartedBefore(lockWrittenUtc)) continue;
                    }
                    catch (ArgumentException) { continue; }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception) { return GitLockOwnership.Unknown; }
                    try
                    {
                        var cwd = Directory.ResolveLinkTarget(Path.Combine(processDir, "cwd"), true)?.FullName;
                        if (cwd is not null && (cwd == root || cwd.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                            return GitLockOwnership.Owned;
                        var command = File.ReadAllText(Path.Combine(processDir, "cmdline"));
                        if (command.Contains(root, StringComparison.Ordinal)) return GitLockOwnership.Owned;
                    }
                    catch (IOException)
                    {
                        if (!Directory.Exists(processDir)) continue;
                        return GitLockOwnership.Unknown;
                    }
                    catch (UnauthorizedAccessException) { return GitLockOwnership.Unknown; }
                }
                return GitLockOwnership.None;
            }
            foreach (var process in Process.GetProcessesByName("git"))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.StartTime.ToUniversalTime()
                            <= GitStaleLockPolicy.OwnerStartedBefore(lockWrittenUtc))
                            return GitLockOwnership.Unknown;
                    }
                    catch (InvalidOperationException) { /* process exited during inspection */ }
                    catch (System.ComponentModel.Win32Exception) { return GitLockOwnership.Unknown; }
                }
            }
            return GitLockOwnership.None;
        }
        catch (Exception) { return GitLockOwnership.Unknown; }
    }
}
