using System.Collections.Concurrent;
using AgentStudio.Git;
using AgentStudio.Bus;

namespace AgentStudio.Pipeline;

/// <summary>Keeps completed cards stable when a published delivery outlives a lane error.</summary>
internal static class AcceptedIntegrationPublishedDeliveryGuard
{
    private static readonly ConcurrentDictionary<string, byte> SignaledLanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A verified operator acceptance expires when its containing tree leaves publication.</summary>
    public static string? LostOperatorAcceptance(TaskInfo job, string branch, GitService? git)
    {
        if (job.State != TaskStates.Completed || git is null) return null;
        var records = CurrentOperatorAcceptances(job, branch).ToArray();
        if (records.Length == 0) return null;
        var repo = git.ResolveRepoRootForWatchPath(job.WatchPath);
        if (repo is null) return null;
        var check = git.CheckPublishedDelivery(repo, branch, records[^1].IntegrationSha!);
        if (check == PublishedDeliveryContainment.Unavailable) return null;
        var published = git.GetBranchTip(repo, $"refs/remotes/origin/{branch}");
        if (published is not null && records.Any(record =>
                git.IsAncestor(repo, record.IntegrationSha!, published))) return null;
        return $"The operator-accepted containing tree {records[^1].IntegrationSha} is no longer in published {branch}; publication was rewritten. The acceptance must return to Human Review.";
    }

    /// <summary>Current acceptance evidence can settle a completed card without replaying its delivery ref.</summary>
    public static bool HasPublishedCurrentAcceptance(TaskInfo job, string branch, GitService? git)
    {
        if (job.State != TaskStates.Completed || git is null) return false;
        var delivery = CurrentDeliverySha(job);
        if (!ReviewSubjectStore.IsValidResultSha(delivery)) return false;
        var repo = git.ResolveRepoRootForWatchPath(job.WatchPath);
        if (repo is null) return false;
        var records = CurrentOperatorAcceptances(job, branch).ToArray();
        var external = ExternalPublishedVerificationStore.Read(job.WatchPath)
            .Where(record => IntegrationVerificationProjection.SameBranch(record.IntegrationBranch, branch)
                && ReviewSubjectStore.IsValidResultSha(record.CandidateSha)
                && git.IsAncestor(repo, delivery!, record.CandidateSha))
            .ToArray();
        if (records.Length == 0 && external.Length == 0) return false;
        var candidate = records.Select(record => record.IntegrationSha!)
            .Concat(external.Select(record => record.CandidateSha))
            .First();
        if (git.CheckPublishedDelivery(repo, branch, candidate) == PublishedDeliveryContainment.Unavailable)
            return false;
        var published = git.GetBranchTip(repo, $"refs/remotes/origin/{branch}");
        return published is not null && (records.Any(record => git.IsAncestor(repo, record.IntegrationSha!, published))
            || external.Any(record => git.IsAncestor(repo, record.CandidateSha, published)));
    }

    private static IEnumerable<TaskIntegrationRecord> CurrentOperatorAcceptances(TaskInfo job, string branch)
    {
        var delivery = CurrentDeliverySha(job);
        if (!ReviewSubjectStore.IsValidResultSha(delivery)) return [];
        return job.IntegrationRecords.Where(item =>
            item.Id.StartsWith("operator-acceptance-", StringComparison.Ordinal)
            && item.Classification == IntegrationRecordClasses.IntegratedVerified
            && ReviewSubjectStore.IsValidResultSha(item.IntegrationSha)
            && string.Equals(item.SourceSha, delivery, StringComparison.OrdinalIgnoreCase)
            && IntegrationVerificationProjection.SameBranch(item.IntegrationBranch, branch));
    }

    private static string? CurrentDeliverySha(TaskInfo job)
    {
        var resolved = DeliveryRefResolver.Resolve(job.Id, job.FolderPath).ExpectedResultSha;
        return ReviewSubjectStore.IsValidResultSha(resolved)
            ? resolved
            : ReviewSubjectStore.Read(job.FolderPath)?.ResultSha;
    }

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
