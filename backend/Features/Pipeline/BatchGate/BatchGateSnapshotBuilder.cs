using System.Collections.Concurrent;
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
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CachedDiff> _diffs = new(StringComparer.Ordinal);
    private int _diffReads;
    internal int DiffReadCount => Volatile.Read(ref _diffReads);

    /// <summary>A null diff could not be computed and must not look like a small delivery.</summary>
    private sealed record CachedDiff(BatchGateDeliveryDiff? Diff, DateTimeOffset ReadAtUtc);

    public BatchGateSnapshotBuilder(BatchGateStore store, AttemptAuthorityService authority,
        TaskScannerService scanner, GitService git, ILogger? logger = null)
    {
        _store = store;
        _authority = authority;
        _scanner = scanner;
        _git = git;
        _logger = logger;
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
            var diff = Diff(item.RepositoryPath, branch, item.Subject.TaskKey,
                item.Subject.ResultSha, now);
            return new BatchPlanCard(item.Subject.TaskKey,
                item.Subject.ReviewCompletedAtUtc,
                Risk(diff, redKeys.Contains(item.Subject.TaskKey),
                    flakyKeys.Contains(item.Subject.TaskKey),
                    task?.Tags.Any(redAreaTags.Contains) == true),
                diff?.Paths ?? [], task?.Tags ?? []);
        }).ToArray();
        var medianReview = MedianReviewDuration(waiting);
        var waitingKeys = ready.Select(item => item.TaskKey).ToHashSet(StringComparer.Ordinal);
        var reviews = _authority.ListActiveReviewAttempts()
            .Concat(_authority.ListPendingReviewAttempts())
            .GroupBy(review => review.AttemptId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(review => !waitingKeys.Contains(review.TaskKey))
            .Select(review =>
            {
                var task = _scanner.FindJob(review.TaskKey);
                if (task is null || task.ProjectName != project) return null;
                // A full-plan review or a review of a superseded run can never
                // become a member; it is filtered before any git call.
                var currentRun = _authority.GetTaskProjection(review.TaskKey).CurrentRunAttempt;
                if (!BatchPlanUpcomingEligibility.IsCandidate(review.Subject.Plan,
                        string.Equals(currentRun?.AttemptId, review.SourceRunAttemptId,
                            StringComparison.Ordinal)))
                    return null;
                var repo = _git.ResolveRepoRootForWatchPath(task.WatchPath);
                var diff = Diff(repo, branch, review.TaskKey,
                    review.Subject.ExpectedResultSha, now);
                var started = new DateTimeOffset(DateTime.SpecifyKind(review.CreatedAt, DateTimeKind.Utc));
                return new BatchPlanUpcoming(review.TaskKey, "review",
                    started + medianReview, Risk(diff,
                        redKeys.Contains(review.TaskKey), flakyKeys.Contains(review.TaskKey),
                        task.Tags.Any(redAreaTags.Contains)), diff?.Paths ?? []);
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
            options.LookAheadMinutes, options.EffectiveUrgentTags, options.RiskPolicy,
            options.Overlap, closeNow, idleGateHost);
    }

    /// <summary>
    /// The card's own diff from the result SHA, never from a ref name. A
    /// missing repository counts as an unavailable diff.
    /// </summary>
    private BatchGateDeliveryDiff? Diff(string? repo, string branch, string taskKey,
        string sha, DateTimeOffset now)
    {
        var key = string.Join('\n', repo ?? string.Empty, branch, sha);
        if (_diffs.TryGetValue(key, out var cached)
            && now - cached.ReadAtUtc < TimeSpan.FromMinutes(5))
            return cached.Diff;
        Interlocked.Increment(ref _diffReads);
        BatchGateDeliveryDiff? diff = null;
        if (repo is not null)
        {
            try { diff = BatchGateDeliveryDiff.Read(_git, repo, branch, sha); }
            catch (Exception) { diff = null; }
        }
        if (diff is null)
            _logger?.LogWarning("batch-gate-diff-unavailable task={TaskKey} sha={Sha}",
                taskKey, sha);
        if (_diffs.Count > 1024) _diffs.Clear();
        _diffs[key] = new CachedDiff(diff, now);
        return diff;
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

    private static double Risk(BatchGateDeliveryDiff? diff, bool priorRed, bool flaky,
        bool areaRed)
        => diff is null ? BatchGateRiskModel.Unknown(priorRed, flaky, areaRed)
            : BatchGateRiskModel.Score(diff.Paths, diff.Lines, priorRed, flaky, areaRed);
}
