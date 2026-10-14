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
        var repo = git.ResolveRepoRootForWatchPath(job.WatchPath);
        var delivery = DeliveryRefResolver.Resolve(job.Id, job.FolderPath);
        var deliverySha = delivery.ExpectedResultSha;
        if (!ReviewSubjectStore.IsValidResultSha(deliverySha) && !delivery.IsRemote && repo is not null)
            deliverySha = git.GetBranchTip(repo, delivery.Ref);
        if (!ReviewSubjectStore.IsValidResultSha(deliverySha)) return false;
        var containment = repo is null
            ? PublishedDeliveryContainment.Unavailable
            : git.CheckPublishedDelivery(repo, branch, deliverySha!);
        if (containment == PublishedDeliveryContainment.Missing) return false;

        var key = $"{repo ?? job.WatchPath}|{branch}";
        if (SignaledLanes.TryAdd(key, 0))
        {
            logger.LogWarning(
                "Acceptance integration needs operator attention for {Project} {Branch} at {Repository}: delivery {DeliverySha} is {Containment} on the published branch; completed cards remain completed",
                job.ProjectName, branch, repo, deliverySha, containment);
            if (bus is not null)
            {
                try
                {
                    bus.EmitAcceptanceLaneBlockedAsync(job.ProjectName, branch, repo,
                        deliverySha!, containment.ToString()).GetAwaiter().GetResult();
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
