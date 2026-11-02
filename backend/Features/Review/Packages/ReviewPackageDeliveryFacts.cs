using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// The current delivery of one Dossier card as package formation sees it:
/// the run attempt and result, the per-card review round whose card check
/// passed (S2 tiers: tool commands and requirement fit), that round's frozen
/// plan, and the review material size. Null fields are unknown.
/// </summary>
public sealed record ReviewPackageDelivery(
    string RunAttemptId,
    string ResultSha,
    string? ResultRef,
    string? CardCheckAttemptId,
    DateTimeOffset? CardCheckPassedAtUtc,
    ReviewPlanDto? CardPlan,
    string? RepositoryId,
    string? RepositoryUrl,
    IReadOnlyList<string> Paths,
    int ChangedLines,
    bool SizeKnown);

public interface IReviewPackageDeliveryFacts
{
    /// <summary>
    /// The card's current delivery, or null when it has none or, outside Auto
    /// Review, recorded no review step since <paramref name="since"/>.
    /// </summary>
    ReviewPackageDelivery? Read(TaskInfo task, DateTimeOffset since);
}

/// <summary>
/// Reads delivery facts from the attempt authority, the task's pipeline record
/// and the published Git facts. It starts no Git process and writes nothing.
/// </summary>
public sealed class ReviewPackageDeliveryFacts(
    AttemptAuthorityService authority, PipelineExecutionLog pipeline, ITokenPricing pricing,
    GitCardFactReader gitFacts) : IReviewPackageDeliveryFacts
{
    public ReviewPackageDelivery? Read(TaskInfo task, DateTimeOffset since)
    {
        // A finished card whose pipeline record has not changed since the
        // lookback cannot hold a recent card check; skip its authority read.
        var pipelinePath = Path.Combine(task.FolderPath, PipelineExecutionLog.FileName);
        if (task.State != TaskStates.AutoReview
            && (!File.Exists(pipelinePath) || File.GetLastWriteTimeUtc(pipelinePath) < since.UtcDateTime))
            return null;
        var projection = authority.GetTaskProjection(string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key);
        if (projection.CurrentRunAttempt is not { ResultSha: { Length: > 0 } resultSha } run) return null;
        var samples = pipeline.Read(task.FolderPath) is { } record
            ? ReviewStepSampleReader.Read(task.Id, record, pricing) : [];
        var check = LatestCardCheck(samples, resultSha, round => projection.ReviewAttempts
            .FirstOrDefault(attempt => attempt.AttemptId == round)?.Subject.ExpectedResultSha);
        var review = check is null ? null
            : projection.ReviewAttempts.FirstOrDefault(attempt => attempt.AttemptId == check.Value.RoundId);
        var material = gitFacts.Read(task.WatchPath).Facts is { } facts
            ? ReviewBaselineService.MaterialPaths(task.Commits, facts.Catalog.Commit) : null;
        return new ReviewPackageDelivery(run.AttemptId, resultSha,
            review?.Subject.ResultRef ?? run.ResultEnvelope?.ImmutableRemoteRef,
            check?.RoundId, check?.PassedAt, review?.Subject.Plan,
            review?.Subject.RepositoryId ?? run.RepositoryId,
            review?.Subject.RepositoryUrl ?? run.ResultEnvelope?.RepositoryUrl,
            material?.Paths ?? [], material?.Lines ?? 0, material is not null);
    }

    /// <summary>
    /// The latest review round of <paramref name="resultSha"/> whose card-check
    /// samples all passed, and when the last of them finished. A round is of
    /// that SHA when a sample names it, or, for rows without one, when the
    /// authority's review attempt of that round reviewed it.
    /// </summary>
    internal static (string RoundId, DateTimeOffset PassedAt)? LatestCardCheck(
        IReadOnlyList<ReviewStepSample> samples, string resultSha, Func<string, string?> roundSha)
    {
        bool Same(string? sha) => string.Equals(sha, resultSha, StringComparison.OrdinalIgnoreCase);
        var round = samples.Where(sample => ReviewSteps.Tier(sample.Step) == ReviewSteps.CardCheckTier)
            .GroupBy(sample => sample.RoundId, StringComparer.Ordinal)
            .Where(group => group.All(sample => sample.Passed)
                && group.All(sample => sample.ReviewedResultSha is null || Same(sample.ReviewedResultSha))
                && (group.Any(sample => Same(sample.ReviewedResultSha)) || Same(roundSha(group.Key))))
            .MaxBy(group => group.Max(sample => sample.CompletedAt));
        return round is null ? null
            : (round.Key, new DateTimeOffset(DateTime.SpecifyKind(round.Max(sample => sample.CompletedAt), DateTimeKind.Utc)));
    }
}
