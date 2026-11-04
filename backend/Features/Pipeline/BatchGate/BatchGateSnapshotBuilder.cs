using System.Collections.Concurrent;
using AgentStudio.Git;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

/// <summary>Risk and changed paths of one waiting card, as of <paramref name="ReadAtUtc"/>.</summary>
public sealed record BatchGateCachedRisk(double Risk, IReadOnlyList<string> Paths,
    DateTimeOffset ReadAtUtc);

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
        DateTimeOffset now, bool closeNow = false, bool idleGateHost = false, bool gateBacklog = false)
    {
        var history = History(project);
        var ready = waiting.Where(item => PendingExclusion(item) is null).Select(item =>
        {
            var task = _scanner.FindJob(item.Subject.TaskKey, item.WatchPath);
            var diff = Diff(item.RepositoryPath, branch, item.Subject.TaskKey,
                item.Subject.ResultSha, now);
            return new BatchPlanCard(item.Subject.TaskKey,
                item.Subject.ReviewCompletedAtUtc, history.Risk(diff, item.Subject.TaskKey, task),
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
                            StringComparison.Ordinal), ContinuationPending(task),
                        review.Subject.ExpectedResultSha, currentRun?.ResultEnvelope?.ResultSha))
                    return null;
                var repo = _git.ResolveRepoRootForWatchPath(task.WatchPath);
                var diff = Diff(repo, branch, review.TaskKey,
                    review.Subject.ExpectedResultSha, now);
                var started = new DateTimeOffset(DateTime.SpecifyKind(review.CreatedAt, DateTimeKind.Utc));
                return new BatchPlanUpcoming(review.TaskKey, "review",
                    started + medianReview, history.Risk(diff, review.TaskKey, task),
                    diff?.Paths ?? []);
            }).Where(item => item is not null).Cast<BatchPlanUpcoming>();
        var coding = _scanner.ScanAllJobsWithArchive()
            .Where(task => task.ProjectName == project && task.State == TaskStates.Progress
                && task.PendingIntent is null && !waitingKeys.Contains(task.Id))
            .Select(task => new BatchPlanUpcoming(task.Id, "coding", null,
                history.AreaRed(task) ? .25 : .15,
                task.Tags.Where(tag => tag.StartsWith("area:", StringComparison.OrdinalIgnoreCase))
                    .ToArray()));
        return new BatchPlanInput(now, ready, reviews.Concat(coding).ToArray(),
            options.MinimumSize, options.MaximumSize, options.DeadlineMinutes,
            options.LookAheadMinutes, options.EffectiveUrgentTags, options.RiskPolicy,
            options.Overlap, closeNow, idleGateHost, gateBacklog);
    }

    /// <summary>
    /// Risk and paths of the waiting cards as the planner tick last read them,
    /// with the read time. Starts no Git process: a card the tick has not read
    /// yet is absent, a diff the tick could not compute keeps its unknown risk,
    /// and an entry past the tick's five-minute reuse window is still returned
    /// as the last known value (AGT-3413).
    /// </summary>
    public IReadOnlyDictionary<string, BatchGateCachedRisk> Cached(string project,
        IReadOnlyList<BatchGatePendingRecord> waiting)
    {
        var history = History(project);
        var cached = new Dictionary<string, BatchGateCachedRisk>(StringComparer.Ordinal);
        foreach (var item in waiting.Where(item => PendingExclusion(item) is null))
        {
            if (!_diffs.TryGetValue(DiffKey(item.RepositoryPath, item.Subject.IntegrationBranch,
                    item.Subject.ResultSha), out var entry)) continue;
            var task = _scanner.FindJob(item.Subject.TaskKey, item.WatchPath);
            cached[item.Subject.TaskKey] = new BatchGateCachedRisk(
                history.Risk(entry.Diff, item.Subject.TaskKey, task), entry.Diff?.Paths ?? [],
                entry.ReadAtUtc);
        }
        return cached;
    }

    /// <summary>
    /// Planner tick: reads the diffs of every waiting card of a scope, including
    /// those the look-ahead snapshot skips or a count planner never snapshots,
    /// so <see cref="Cached"/> finds them.
    /// </summary>
    public void Refresh(string branch, IEnumerable<BatchGatePendingRecord> waiting,
        DateTimeOffset now)
    {
        foreach (var item in waiting.Where(item => PendingExclusion(item) is null))
            Diff(item.RepositoryPath, branch, item.Subject.TaskKey, item.Subject.ResultSha, now);
    }

    /// <summary>Planner-owned changed paths, including the unknown-diff case.</summary>
    public IReadOnlyList<string>? Paths(BatchGatePendingRecord item, DateTimeOffset now)
        => Diff(item.RepositoryPath, item.Subject.IntegrationBranch,
            item.Subject.TaskKey, item.Subject.ResultSha, now)?.Paths;

    internal string? PendingExclusion(BatchGatePendingRecord pending)
    {
        var task = _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath);
        var projection = _authority.GetTaskProjection(pending.Subject.TaskKey);
        var deliverySha = projection.CurrentRunAttempt?.ResultEnvelope?.ResultSha;
        return BatchPlanUpcomingEligibility.DeliveryExclusion(
                task is not null && ContinuationPending(task),
                projection.CurrentReviewAttempt?.Subject.ExpectedResultSha, deliverySha)
            ?? BatchPlanUpcomingEligibility.DeliveryExclusion(false,
                pending.Subject.ResultSha, deliverySha);
    }

    // Progress covers a queued continuation after its persisted intent was consumed.
    private static bool ContinuationPending(TaskInfo task)
        => task.PendingIntent is not null || task.State == TaskStates.Progress;

    /// <summary>
    /// The card's own diff from the result SHA, never from a ref name. A
    /// missing repository counts as an unavailable diff.
    /// </summary>
    private BatchGateDeliveryDiff? Diff(string? repo, string branch, string taskKey,
        string sha, DateTimeOffset now)
    {
        var key = DiffKey(repo, branch, sha);
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

    private static string DiffKey(string? repo, string branch, string sha)
        => string.Join('\n', repo ?? string.Empty, branch, sha);

    /// <summary>Gate history of the project's cards; store and scanner reads only.</summary>
    private RiskHistory History(string project)
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
        return new RiskHistory(redKeys, flakyKeys, redAreaTags);
    }

    private sealed record RiskHistory(HashSet<string> RedKeys, HashSet<string> FlakyKeys,
        HashSet<string> RedAreaTags)
    {
        public bool AreaRed(TaskInfo? task) => task?.Tags.Any(RedAreaTags.Contains) == true;

        public double Risk(BatchGateDeliveryDiff? diff, string taskKey, TaskInfo? task)
            => BatchGateSnapshotBuilder.Risk(diff, RedKeys.Contains(taskKey),
                FlakyKeys.Contains(taskKey), AreaRed(task));
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
