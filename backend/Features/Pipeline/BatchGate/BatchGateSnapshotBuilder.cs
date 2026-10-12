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
    private readonly ConcurrentDictionary<string, CachedDiff> _diffs = new(StringComparer.Ordinal);
    private int _diffReads;
    internal int DiffReadCount => Volatile.Read(ref _diffReads);

    private sealed record CachedDiff(IReadOnlyList<string> Paths, int Lines,
        DateTimeOffset ReadAtUtc);

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
            var diff = Diff(item.RepositoryPath, branch, item.Subject.ResultSha,
                item.Subject.ResultRef, now);
            var paths = diff.Paths;
            var lines = diff.Lines;
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
                var refName = review.Subject.ResultRef;
                var repo = _git.ResolveRepoRootForWatchPath(task.WatchPath);
                var diff = repo is null ? new CachedDiff([], 0, now)
                    : Diff(repo, branch, review.Subject.ExpectedResultSha, refName, now);
                var paths = diff.Paths;
                var lines = diff.Lines;
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
            options.LookAheadMinutes, options.EffectiveUrgentTags, options.RiskPolicy,
            options.Overlap, closeNow, idleGateHost);
    }

    private CachedDiff Diff(string repo, string branch, string sha,
        string? resultRef, DateTimeOffset now)
    {
        var key = string.Join('\n', repo, branch, sha);
        if (_diffs.TryGetValue(key, out var cached)
            && now - cached.ReadAtUtc < TimeSpan.FromMinutes(5))
            return cached;
        Interlocked.Increment(ref _diffReads);
        var fresh = new CachedDiff(Paths(repo, branch, resultRef),
            ChangedLines(repo, branch, resultRef), now);
        if (_diffs.Count > 1024) _diffs.Clear();
        _diffs[key] = fresh;
        return fresh;
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
