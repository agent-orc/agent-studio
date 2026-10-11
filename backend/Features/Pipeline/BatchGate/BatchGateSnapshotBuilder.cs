using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

/// <summary>Builds the live inputs for the pure look-ahead policy.</summary>
public sealed class BatchGateSnapshotBuilder
{
    private readonly BatchGateStore _store;
    private readonly AttemptAuthorityService _authority;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;

    public BatchGateSnapshotBuilder(BatchGateStore store, AttemptAuthorityService authority,
        TaskScannerService scanner, GitService git)
    {
        _store = store;
        _authority = authority;
        _scanner = scanner;
        _git = git;
    }

    public BatchPlanInput Build(string project, string branch,
        IReadOnlyList<BatchGatePendingRecord> waiting, BatchGateFormationOptions options,
        DateTimeOffset now, bool closeNow = false, bool idleGateHost = false)
    {
        var redKeys = _store.ListManifests()
            .Where(manifest => manifest.Scope.Project == project
                && _store.ListRuns(manifest.BatchId).Any(run =>
                    _store.ReadVerdict(manifest.BatchId, run.BatchRunId)?.Outcome == "fail"))
            .SelectMany(manifest => manifest.Members.Select(member => member.TaskKey))
            .ToHashSet(StringComparer.Ordinal);
        var flakyKeys = _store.ListManifests()
            .Where(manifest => manifest.Scope.Project == project
                && _store.ListRuns(manifest.BatchId).Any(run =>
                    _store.ReadVerdict(manifest.BatchId, run.BatchRunId)?.Classification
                        is "flaky-red" or "flaky-quarantined-pass"))
            .SelectMany(manifest => manifest.Members.Select(member => member.TaskKey))
            .ToHashSet(StringComparer.Ordinal);
        var redAreaTags = redKeys.Select(key => _scanner.FindJob(key))
            .Where(task => task is not null)
            .SelectMany(task => task!.Tags)
            .Where(tag => tag.StartsWith("area:", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ready = waiting.Select(item =>
        {
            var task = _scanner.FindJob(item.Subject.TaskKey, item.WatchPath);
            var paths = Paths(item.RepositoryPath, branch, item.Subject.ResultRef);
            var lines = ChangedLines(item.RepositoryPath, branch, item.Subject.ResultRef);
            return new BatchPlanCard(item.Subject.TaskKey,
                item.Subject.ReviewCompletedAtUtc,
                Risk(paths, lines, redKeys.Contains(item.Subject.TaskKey),
                    flakyKeys.Contains(item.Subject.TaskKey),
                    task?.Tags.Any(redAreaTags.Contains) == true),
                paths, task?.Tags ?? []);
        }).ToArray();
        var medianReview = MedianReviewDuration(waiting);
        var waitingKeys = ready.Select(item => item.TaskKey).ToHashSet(StringComparer.Ordinal);
        var reviews = _authority.ListActiveReviewAttempts()
            .Where(review => !waitingKeys.Contains(review.TaskKey))
            .Select(review =>
            {
                var task = _scanner.FindJob(review.TaskKey);
                if (task is null || task.ProjectName != project) return null;
                var refName = review.Subject.ResultRef;
                var repo = _git.ResolveRepoRootForWatchPath(task.WatchPath);
                var paths = repo is null ? [] : Paths(repo, branch, refName);
                var lines = repo is null ? 0 : ChangedLines(repo, branch, refName);
                var started = new DateTimeOffset(DateTime.SpecifyKind(review.CreatedAt, DateTimeKind.Utc));
                return new BatchPlanUpcoming(review.TaskKey, "review",
                    started + medianReview, Risk(paths, lines,
                        redKeys.Contains(review.TaskKey), flakyKeys.Contains(review.TaskKey),
                        task.Tags.Any(redAreaTags.Contains)), paths);
            }).Where(item => item is not null).Cast<BatchPlanUpcoming>();
        var coding = _scanner.ScanAllJobsWithArchive()
            .Where(task => task.ProjectName == project && task.State == TaskStates.Progress
                && !waitingKeys.Contains(task.Id))
            .Select(task => new BatchPlanUpcoming(task.Id, "coding", null,
                task.Tags.Any(redAreaTags.Contains) ? .25 : .15,
                task.Tags.Where(tag => tag.StartsWith("area:", StringComparison.OrdinalIgnoreCase))
                    .ToArray()));
        return new BatchPlanInput(now, ready, reviews.Concat(coding).ToArray(),
            options.MinimumSize, options.MaximumSize, options.DeadlineMinutes,
            options.LookAheadMinutes, options.UrgentTags ?? [], options.RiskPolicy,
            options.Overlap, closeNow, idleGateHost);
    }

    private TimeSpan MedianReviewDuration(IReadOnlyList<BatchGatePendingRecord> waiting)
    {
        var samples = waiting.Select(item =>
            {
                var review = _authority.GetTaskProjection(item.Subject.TaskKey).CurrentReviewAttempt;
                return review is null ? 0 : (item.Subject.ReviewCompletedAtUtc
                    - new DateTimeOffset(DateTime.SpecifyKind(review.CreatedAt, DateTimeKind.Utc))).TotalMinutes;
            })
            .Where(minutes => minutes > 0 && minutes < 240).Order().ToArray();
        return TimeSpan.FromMinutes(samples.Length == 0 ? 30 : samples[samples.Length / 2]);
    }

    private IReadOnlyList<string> Paths(string repo, string branch, string? resultRef)
    {
        if (string.IsNullOrWhiteSpace(resultRef)) return [];
        try { return _git.ChangedPathsAgainstMergeBase(repo, branch, resultRef) ?? []; }
        catch (Exception) { return []; }
    }

    private int ChangedLines(string repo, string branch, string? resultRef)
    {
        if (string.IsNullOrWhiteSpace(resultRef)) return 0;
        try
        {
            return _git.GetFilesChangedInRangeAtRoot(repo, branch, resultRef)
                .Sum(change => change.Added + change.Removed);
        }
        catch (Exception) { return 0; }
    }

    private static double Risk(IReadOnlyList<string> paths, int changedLines,
        bool priorRed, bool flaky, bool areaRed)
    {
        var infrastructure = paths.Any(path => path.StartsWith("backend/Features/Pipeline/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("backend/Features/Git/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("contracts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("deploy/", StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(.08 + Math.Min(.24, paths.Count * .015)
            + Math.Min(.18, changedLines / 2000d)
            + (infrastructure ? .22 : 0) + (priorRed ? .2 : 0)
            + (flaky ? .1 : 0) + (areaRed ? .1 : 0), 0, .95);
    }
}
