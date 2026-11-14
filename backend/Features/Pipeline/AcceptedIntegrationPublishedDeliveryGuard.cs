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

    internal static string? CurrentDeliverySha(TaskInfo job)
    {
        var resolved = DeliveryRefResolver.Resolve(job.Id, job.FolderPath).ExpectedResultSha;
        if (ReviewSubjectStore.IsValidResultSha(resolved)) return resolved;
        var subject = ReviewSubjectStore.Read(job.FolderPath)?.ResultSha;
        return ReviewSubjectStore.IsValidResultSha(subject)
            ? subject
            : job.CompletionClaim?.ResultSha;
    }

    /// <summary>
    /// AGT-3758: the card's verified record names a tree the published branch
    /// still carries and that tree is the delivery's net-change publication
    /// (recorded delivery SHA or <c>Agent-Studio-Net-Change-Of</c> trailer).
    /// The result commit itself is not an ancestor, yet the delivery is integrated.
    /// </summary>
    public static bool CarriesNetChangePublication(TaskInfo job, string branch, GitService? git)
        => PublishedDelivery(job, branch, git) is { } context
           && CarriesNetChangePublication(job, branch, git!, context);

    private static bool CarriesNetChangePublication(TaskInfo job, string branch, GitService git,
        (string Repo, string Delivery, string Published) context)
    {
        var record = IntegrationVerificationStore.Read(job.FolderPath);
        return record is not null && IntegrationVerificationProjection.CarriesVerifiedDelivery(
            record, branch, context.Delivery, sha => git.IsAncestor(context.Repo, sha, context.Published),
            sha => git.GetNetChangeSource(context.Repo, sha));
    }

    /// <summary>State, delivery, repository and published tip shared by the carried-delivery checks.</summary>
    private static (string Repo, string Delivery, string Published)? PublishedDelivery(
        TaskInfo job, string branch, GitService? git)
    {
        if (job.State != TaskStates.Completed || git is null) return null;
        var delivery = CurrentDeliverySha(job);
        if (!ReviewSubjectStore.IsValidResultSha(delivery)) return null;
        var repo = git.ResolveRepoRootForWatchPath(job.WatchPath);
        if (repo is null) return null;
        var published = git.GetBranchTip(repo, $"refs/remotes/origin/{branch}");
        return published is null ? null : (repo, delivery!, published);
    }

    /// <summary>
    /// AGT-3758: a failed recovery merge never reopens a completed card whose
    /// delivery the published branch carries, by ancestry or as a net-change
    /// publication. The case is logged; the card stays Completed.
    /// </summary>
    private static bool KeepCompletedAfterRecoveryMerge(TaskInfo job, string branch, string? failureCode,
        GitService? git, ILogger logger)
    {
        if (PublishedDelivery(job, branch, git) is not { } context) return false;
        var carriedBy = git!.IsAncestor(context.Repo, context.Delivery, context.Published) ? "ancestry"
            : CarriesNetChangePublication(job, branch, git, context) ? "net-change publication" : null;
        if (carriedBy is null) return false;
        logger.LogWarning(
            "Recovery merge for completed {JobId} failed ({FailureCode}), but {Branch} carries delivery {DeliverySha} by {CarriedBy}; the card stays Completed",
            job.Id, failureCode, branch, context.Delivery, carriedBy);
        return true;
    }

    /// <summary>
    /// The single keep-Completed decision after a failed acceptance integration.
    /// <paramref name="recoveryMerge"/> marks a backstop recovery merge, which
    /// additionally keeps a card whose delivery the published branch carries.
    /// </summary>
    public static bool KeepCompleted(TaskInfo job, string branch, string? failureCode, GitService? git,
        AgentMessageBusBridge? bus, ILogger logger, bool recoveryMerge = false)
    {
        if (recoveryMerge && KeepCompletedAfterRecoveryMerge(job, branch, failureCode, git, logger)) return true;
        if (job.State != TaskStates.Completed || git is null ||
            failureCode is not (AcceptedIntegrationFailureCodes.BranchSyncFailed or
                AcceptedIntegrationFailureCodes.GitCredentialUnavailable or
                AcceptedIntegrationFailureCodes.WorktreeUnavailable or
                AcceptedIntegrationFailureCodes.ServerCheckoutMissing or
                AcceptedIntegrationFailureCodes.NoTaskBranch)) return false;
        var repo = git.ResolveRepoRootForWatchPath(job.WatchPath);
        var delivery = DeliveryRefResolver.Resolve(job.Id, job.FolderPath);
        var deliverySha = delivery.ExpectedResultSha;
        if (!ReviewSubjectStore.IsValidResultSha(deliverySha))
            deliverySha = job.CompletionClaim?.ResultSha;
        if (!ReviewSubjectStore.IsValidResultSha(deliverySha) && !delivery.IsRemote && repo is not null)
            deliverySha = git.GetBranchTip(repo, delivery.Ref);
        if (!ReviewSubjectStore.IsValidResultSha(deliverySha)) return false;
        if (failureCode == AcceptedIntegrationFailureCodes.NoTaskBranch &&
            !job.IntegrationRecords.Any(item =>
                item.Id.StartsWith("operator-acceptance-", StringComparison.Ordinal)
                && item.Classification == IntegrationRecordClasses.IntegratedVerified
                && AgentStudio.Pipeline.IntegrationVerificationProjection.SameBranch(item.IntegrationBranch, branch)
                && (string.Equals(item.SourceSha, deliverySha, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.IntegrationSha, deliverySha, StringComparison.OrdinalIgnoreCase)))) return false;
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
