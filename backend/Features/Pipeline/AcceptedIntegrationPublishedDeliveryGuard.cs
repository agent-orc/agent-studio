using System.Collections.Concurrent;
using AgentStudio.Git;
using AgentStudio.Bus;

namespace AgentStudio.Pipeline;

/// <summary>Keeps completed cards stable when a published delivery outlives a lane error.</summary>
internal static class AcceptedIntegrationPublishedDeliveryGuard
{
    private static readonly ConcurrentDictionary<string, byte> SignaledLanes = new(StringComparer.OrdinalIgnoreCase);

    public static bool KeepCompleted(TaskInfo job, string branch, string? failureCode, GitService? git,
        AgentMessageBusBridge? bus, ILogger logger)
    {
        if (job.State != TaskStates.Completed || git is null ||
            failureCode is not (AcceptedIntegrationFailureCodes.BranchSyncFailed or
                AcceptedIntegrationFailureCodes.WorktreeUnavailable)) return false;
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        if (subject is null || !ReviewSubjectStore.IsValidResultSha(subject.ResultSha)) return false;
        var repo = git.ResolveRepoRootForWatchPath(job.WatchPath);
        var containment = repo is null
            ? PublishedDeliveryContainment.Unavailable
            : git.CheckPublishedDelivery(repo, branch, subject.ResultSha);
        if (containment == PublishedDeliveryContainment.Missing) return false;

        var key = $"{repo ?? job.WatchPath}|{branch}";
        if (SignaledLanes.TryAdd(key, 0))
        {
            logger.LogWarning(
                "Acceptance integration needs operator attention for {Project} {Branch} at {Repository}: delivery {DeliverySha} is {Containment} on the published branch; completed cards remain completed",
                job.ProjectName, branch, repo, subject.ResultSha, containment);
            if (bus is not null)
            {
                try
                {
                    bus.EmitAcceptanceLaneBlockedAsync(job.ProjectName, branch, repo,
                        subject.ResultSha, containment.ToString()).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    SignaledLanes.TryRemove(key, out _);
                    logger.LogWarning(ex, "Could not publish acceptance lane signal for {Project} {Branch}",
                        job.ProjectName, branch);
                }
            }
        }
        return true;
    }
}
