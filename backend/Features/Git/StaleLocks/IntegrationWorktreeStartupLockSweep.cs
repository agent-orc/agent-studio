namespace AgentStudio.Git;

/// <summary>Checks existing Studio integration slots before startup recovery starts git.</summary>
public sealed class IntegrationWorktreeStartupLockSweep(
    TaskScannerService scanner,
    GitStaleLockGuard guard)
{
    public int RunOnce()
    {
        var cleared = 0;
        foreach (var root in scanner.GetWatchPaths()
                     .Select(entry => entry.RepositoryPath)
                     .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var slot in IntegrationWorktreePolicy.CandidatePaths(root))
            {
                if (Directory.Exists(slot) && guard.ClearStaleIntegrationIndexLock(slot))
                    cleared++;
            }
        }
        return cleared;
    }
}
