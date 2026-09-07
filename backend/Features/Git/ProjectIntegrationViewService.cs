using AgentStudio.Shared;

namespace AgentStudio.Git;

public static class IntegrationQueueStates
{
    public const string Merged = "merged";
    public const string Waiting = "waiting";
    public const string Conflict = "conflict";
    public const string Skipped = "skipped";
    public const string LegacyUnverifiable = "legacy-unverifiable";
    public const string Superseded = "superseded";
}

internal sealed record IntegrationQueueClassificationFacts(
    bool IsIntegrated,
    string? IntegrationProof,
    bool IsArchived,
    bool HasLegacyUnfencedReviewSubject,
    string? IntegrationStatus,
    string? IntegrationDetail,
    string IntegrationRef);

internal sealed record IntegrationQueueClassification(
    string Status,
    string? MergeSha,
    string? Reason);

/// <summary>
/// Pure lifecycle policy for the merge-queue projection. Archived deliveries
/// cannot regain attempt authority or be rebased in place, so their historical
/// outcomes are terminal without hiding the original diagnostic evidence.
/// </summary>
internal static class IntegrationQueueClassificationPolicy
{
    public static IntegrationQueueClassification Decide(IntegrationQueueClassificationFacts facts)
    {
        if (facts.IsIntegrated)
            return new(IntegrationQueueStates.Merged, facts.IntegrationProof, null);

        if (facts.IntegrationStatus == IntegrationStatuses.ConflictSkipped)
        {
            if (facts.IsArchived && facts.HasLegacyUnfencedReviewSubject)
            {
                return new(
                    IntegrationQueueStates.LegacyUnverifiable,
                    null,
                    WithOriginalOutcome(
                        "The archived review subject predates RunAttempt authority and cannot be verified retroactively.",
                        facts.IntegrationDetail));
            }

            if (facts.IsArchived)
            {
                return new(
                    IntegrationQueueStates.Superseded,
                    null,
                    WithOriginalOutcome(
                        "The conflicting delivery belongs to an archived card and is no longer an actionable merge subject. Recover still-required work through a new card.",
                        facts.IntegrationDetail));
            }

            return new(
                IntegrationQueueStates.Conflict,
                null,
                facts.IntegrationDetail ?? "Integration conflict recorded.");
        }

        if (facts.IntegrationStatus == IntegrationStatuses.NoBranch)
        {
            return new(
                IntegrationQueueStates.Skipped,
                null,
                facts.IntegrationDetail ?? "No integrable change set.");
        }

        // AGT-2720: a pending card can carry a real diagnosis of its own - a gate
        // that died in its bundler before the first test names that environment in
        // the detail. Prefer the recorded detail whenever one exists, so the queue
        // shows the cause instead of restating that the change is not there.
        var reason = facts.IntegrationStatus == IntegrationStatuses.Partial
                     || !string.IsNullOrWhiteSpace(facts.IntegrationDetail)
            ? facts.IntegrationDetail
            : $"Accepted change is not present in {facts.IntegrationRef}.";
        return new(IntegrationQueueStates.Waiting, null, reason);
    }

    private static string WithOriginalOutcome(string explanation, string? detail)
        => string.IsNullOrWhiteSpace(detail)
            ? explanation
            : $"{explanation} Original integration outcome: {detail}";
}

public sealed record IntegrationQueueItem(
    string TaskId,
    string TaskKey,
    string Title,
    string Lane,
    DateTime StateSince,
    string Status,
    string? MergeSha,
    string? Reason);

public sealed record PublisherMergeItem(
    string TaskKey,
    string? Title,
    string Sha,
    string ShortSha,
    DateTime IntegratedAt,
    string Publisher,
    string Subject);

public sealed record PromotionTaskItem(
    string TaskKey,
    string? Title,
    string Sha,
    string ShortSha,
    string Subject);

public sealed record PromotionDiffView(
    string FromRef,
    string ToRef,
    string? FromSha,
    string? ToSha,
    IReadOnlyList<PromotionTaskItem> Tasks,
    IReadOnlyList<GitFileChange> Files,
    int FilesChanged,
    int Added,
    int Removed);

public sealed record ProjectIntegrationView(
    string Project,
    bool IsRepo,
    string IntegrationRef,
    string ReleaseRef,
    string? IntegrationHeadSha,
    string? ReleaseHeadSha,
    DateTime CapturedAt,
    IReadOnlyList<IntegrationQueueItem> Queue,
    IReadOnlyList<PublisherMergeItem> PublisherMerges,
    PromotionDiffView Promotion,
    string? Error);

/// <summary>
/// First-class, read-only integration projection for a project. It deliberately
/// observes remote-tracking refs first: acceptance is task workflow state, while
/// integration is only true after the corresponding change is visible in the
/// <c>origin/develop</c> graph.
/// </summary>
public sealed class ProjectIntegrationViewService
{
    /// <summary>
    /// Queue membership includes completed history plus transactional acceptance
    /// cards that are integrating or returned to Human Review with the durable
    /// integration-pending marker.
    /// </summary>
    internal static readonly HashSet<string> AcceptedQueueLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Completed,
        TaskStates.Archive,
        TaskStates.HumanReview,
    };

    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly TaskIntegrationStatusService _taskIntegration;

    public ProjectIntegrationViewService(
        GitService git,
        TaskScannerService scanner,
        ProjectSettingsService settings,
        TaskIntegrationStatusService taskIntegration)
    {
        _git = git;
        _scanner = scanner;
        _settings = settings;
        _taskIntegration = taskIntegration;
    }

    public ProjectIntegrationView Build(string projectName)
    {
        var configuredIntegrationBranch = _settings.Get(projectName).IntegrationBranch;
        const string releaseBranch = BoardMergeStatusService.ReleaseBranch;

        var root = _git.ResolveProjectRepoRoot(projectName);
        if (root == null)
        {
            var unresolvedRef = string.IsNullOrWhiteSpace(configuredIntegrationBranch)
                ? "origin/HEAD"
                : _git.ResolveOriginReadRef(configuredIntegrationBranch);
            return Empty(projectName, unresolvedRef, releaseBranch, "Project has no readable git repository.");
        }

        var integrationBranch = _git.ResolveIntegrationBranch(root, configuredIntegrationBranch);
        var integrationRef = _git.ResolveOriginReadRef(integrationBranch);
        var releaseRef = _git.ResolveOriginReadRef(releaseBranch);
        var integrationHead = _git.GetBranchTip(root, integrationRef);
        var releaseHead = _git.GetBranchTip(root, releaseRef);
        if (integrationHead == null)
            return Empty(projectName, integrationRef, releaseRef, $"Integration ref '{integrationRef}' does not exist.");

        var accepted = _scanner.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .Where(task => AcceptedQueueLanes.Contains(task.State))
            .Where(IsAcceptedQueueTask)
            .Where(task => !task.Fixture)
            .ToList();
        var titles = accepted
            .GroupBy(TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);

        _git.TryGetAncestorShaSet(root, [integrationRef], out var integrationAncestors);
        var releaseAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (releaseHead != null)
            _git.TryGetAncestorShaSet(root, [releaseRef], out releaseAncestors);

        var publisherCommits = _git.GetIntegrationMergeCommits(root, integrationRef);
        var fallback = _taskIntegration.BuildLookup(accepted);
        var proofByTask = new Dictionary<string, string>(StringComparer.Ordinal);

        var queue = accepted.Select(task =>
        {
            string? proof = null;
            var attributed = TaskIntegrationStatusService.AttributedCommits(task);
            if (attributed.Count > 0
                && attributed.All(sha =>
                    TaskIntegrationStatusService.AncestorSetContains(integrationAncestors, sha)))
                proof = attributed[^1];

            fallback.TryGetValue(task.TaskKey, out var localStatus);
            var hasLegacyUnfencedReviewSubject = false;
            if (task.State == TaskStates.Archive
                && localStatus?.Status == IntegrationStatuses.ConflictSkipped
                && !string.IsNullOrWhiteSpace(task.FolderPath))
            {
                var reviewSubject = ReviewSubjectStore.Read(task.FolderPath);
                hasLegacyUnfencedReviewSubject = reviewSubject is not null
                    && string.IsNullOrWhiteSpace(reviewSubject.RunAttemptId);
            }
            var classification = IntegrationQueueClassificationPolicy.Decide(new(
                IsIntegrated: proof != null,
                IntegrationProof: proof,
                IsArchived: task.State == TaskStates.Archive,
                HasLegacyUnfencedReviewSubject: hasLegacyUnfencedReviewSubject,
                IntegrationStatus: localStatus?.Status,
                IntegrationDetail: localStatus?.Detail,
                IntegrationRef: integrationRef));

            if (classification.MergeSha != null)
                proofByTask[task.TaskKey] = classification.MergeSha;
            return Item(task, classification.Status, classification.MergeSha, classification.Reason);
        })
        .OrderBy(item => StatusOrder(item.Status))
        .ThenByDescending(item => item.StateSince)
        .ToList();

        var publisher = publisherCommits
            .Take(200)
            .Select(commit => new PublisherMergeItem(
                commit.TaskKey,
                titles.GetValueOrDefault(commit.TaskKey),
                commit.Sha,
                commit.ShortSha,
                commit.CommittedAtUtc,
                commit.Publisher,
                commit.Subject))
            .ToList();

        var promotionShas = new HashSet<string>(integrationAncestors, StringComparer.OrdinalIgnoreCase);
        promotionShas.ExceptWith(releaseAncestors);
        var promotionTasks = new List<PromotionTaskItem>();
        var seenPromotionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var merge in publisherCommits.Where(commit => promotionShas.Contains(commit.Sha)))
        {
            if (!seenPromotionKeys.Add(merge.TaskKey)) continue;
            promotionTasks.Add(new PromotionTaskItem(
                merge.TaskKey,
                titles.GetValueOrDefault(merge.TaskKey),
                merge.Sha,
                merge.ShortSha,
                merge.Subject));
        }
        foreach (var task in accepted)
        {
            var key = TaskKey(task);
            if (seenPromotionKeys.Contains(key)) continue;
            if (!proofByTask.TryGetValue(task.TaskKey, out var proof) || !promotionShas.Contains(proof)) continue;
            seenPromotionKeys.Add(key);
            promotionTasks.Add(new PromotionTaskItem(key, task.Title, proof, Short(proof), "Attributed task change set"));
        }

        var promotionBase = releaseHead == null
            ? null
            : _git.GetMergeBase(root, releaseHead, integrationHead);
        var files = promotionBase == null
            ? new List<GitFileChange>()
            : _git.GetFilesChangedInRangeAtRoot(root, promotionBase, integrationHead);
        var promotion = new PromotionDiffView(
            integrationRef,
            releaseRef,
            integrationHead,
            releaseHead,
            promotionTasks,
            files,
            files.Count,
            files.Sum(file => file.Added),
            files.Sum(file => file.Removed));

        return new ProjectIntegrationView(
            projectName,
            true,
            integrationRef,
            releaseRef,
            integrationHead,
            releaseHead,
            DateTime.UtcNow,
            queue,
            publisher,
            promotion,
            releaseHead == null
                ? $"Release ref '{releaseRef}' does not exist; promotion diff is unavailable."
                : promotionBase == null
                    ? $"Refs '{releaseRef}' and '{integrationRef}' have no common merge base; promotion diff is unavailable."
                    : null);
    }

    private static IntegrationQueueItem Item(TaskInfo task, string status, string? sha, string? reason)
        => new(task.Id, TaskKey(task), task.Title, task.State, task.EnteredLaneAt, status, sha, reason);

    private static bool IsAcceptedQueueTask(TaskInfo task)
    {
        if (task.State is TaskStates.Completed or TaskStates.Archive) return true;
        if (task.State != TaskStates.HumanReview) return false;
        return string.Equals(task.Phase, LifecyclePhases.Integrating, StringComparison.Ordinal)
               || (task.Tags ?? []).Any(IntegrationStatuses.IsPendingTag);
    }

    private static string TaskKey(TaskInfo task)
        => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key!;

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static int StatusOrder(string status) => status switch
    {
        IntegrationQueueStates.Conflict => 0,
        IntegrationQueueStates.Waiting => 1,
        IntegrationQueueStates.Skipped => 2,
        IntegrationQueueStates.LegacyUnverifiable => 3,
        IntegrationQueueStates.Superseded => 4,
        _ => 5,
    };

    private static ProjectIntegrationView Empty(string project, string integrationRef, string releaseRef, string error)
        => new(
            project,
            false,
            integrationRef,
            releaseRef,
            null,
            null,
            DateTime.UtcNow,
            [],
            [],
            new PromotionDiffView(integrationRef, releaseRef, null, null, [], [], 0, 0, 0),
            error);
}
