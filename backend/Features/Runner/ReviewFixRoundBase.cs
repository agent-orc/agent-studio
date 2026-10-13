using AgentStudio.Orchestration.Decisions;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>
/// The base a review fix round starts from (AGT-3056, AGT-W65 D17 A).
///
/// <para>
/// A fix round after a ProductFailure continues on the delivery the review
/// graded instead of on the integration branch: its prompt asks the agent to
/// fix only the findings, which is only possible when the graded work is in the
/// checkout. Every fix-round entry point (the review-plane finding round, the
/// operator sweep fix round and an operator continue) records the reviewed
/// fenced result ref and SHA as the card's <see cref="ContinuationBaseRecord"/>
/// before the card reaches Ready. The runner prepares the worktree on it and
/// keeps the integration branch as rebase target, exactly as the AGT-2870
/// continuation rounds do.
/// </para>
/// </summary>
public static class ReviewFixRoundBase
{
    /// <summary>The <see cref="ContinuationBaseRecord.Reason"/> of a fix-round base, also sent on the claim.</summary>
    public const string Reason = AgentStudio.TaskServer.Contracts.ContinuationBaseReasons.ReviewFixRound;

    public const string SourceReviewPlane = "review-plane-finding-round";
    public const string SourceOperatorSweep = "operator-sweep-fix-round";
    public const string SourceOperatorContinue = "operator-continue";

    public static bool IsFixRound(string? reason)
        => AgentStudio.TaskServer.Contracts.ContinuationBaseReasons.IsReviewFixRound(reason);

    /// <summary>
    /// The fix-round base for one reviewed delivery, or null when the review
    /// named no full result SHA or no ref to fetch it from. A null base keeps
    /// today's start on the integration branch, and the prompt says so.
    /// </summary>
    public static ContinuationBaseRecord? Decide(
        string reviewAttemptId,
        string? resultRef,
        string? resultSha,
        DateTime savedAtUtc)
    {
        var basis = ReviewedDeliveryBasePolicy.Decide(reviewAttemptId, resultRef, resultSha);
        return basis is null ? null : new ContinuationBaseRecord(
            basis.Ref, basis.Sha, Reason, basis.ReviewAttemptId, savedAtUtc);
    }

    /// <summary>
    /// The base for the delivery <paramref name="review"/> graded. The review
    /// subject names the fenced result ref; an older subject without one falls
    /// back to the card's <c>review-subject.json</c> when it describes the same
    /// SHA, preferring its immutable result ref.
    /// </summary>
    public static ContinuationBaseRecord? ForReview(
        ReviewAttemptDto review,
        ReviewSubjectRecord? folderSubject,
        DateTime savedAtUtc)
    {
        var sha = review.Subject.ExpectedResultSha;
        var reference = review.Subject.ResultRef;
        if (string.IsNullOrWhiteSpace(reference)
            && folderSubject is not null
            && string.Equals(folderSubject.ResultSha, sha, StringComparison.OrdinalIgnoreCase))
            reference = folderSubject.ImmutableResultRef ?? folderSubject.ResultRef;
        return Decide(review.AttemptId, reference, sha, savedAtUtc);
    }

    /// <summary>
    /// Pure decision for an operator continue: it is a fix round when the
    /// card's latest settled review graded its current delivery ProductFailure
    /// and the card does not already carry a salvage base from another round.
    /// An explicit continue refreshes an existing review base from the current
    /// reviewed subject, allowing recovery after its old ref was retired.
    /// </summary>
    public static ContinuationBaseRecord? ForOperatorContinue(
        ReviewAttemptDto? latestSettledReview,
        ReviewSubjectRecord? folderSubject,
        ContinuationBaseRecord? existingBase,
        DateTime savedAtUtc)
    {
        if (existingBase is not null && !IsFixRound(existingBase.Reason)) return null;
        if (latestSettledReview is not { Outcome: ReviewTerminalOutcome.ProductFailure }) return null;
        if (folderSubject is null
            || !string.Equals(folderSubject.ResultSha, latestSettledReview.Subject.ExpectedResultSha,
                StringComparison.OrdinalIgnoreCase))
            return null;
        return ForReview(latestSettledReview, folderSubject, savedAtUtc);
    }

    /// <summary>The settled review that decides a fix round, as the operator sweep reads it.</summary>
    public static ReviewAttemptDto? LatestSettledReview(AttemptAuthorityProjection projection)
    {
        if (projection.CurrentReviewAttempt is { Outcome: not null } current) return current;
        return projection.ReviewAttempts
            .Where(attempt => attempt.Outcome is not null)
            .OrderBy(attempt => attempt.TerminalAt ?? attempt.CreatedAt)
            .LastOrDefault();
    }

    /// <summary>
    /// The one sentence a fix-round prompt carries about its starting point. It
    /// names no SHA: the platform prepares the checkout, the agent never checks
    /// anything out.
    /// </summary>
    public static string PromptSentence(bool continuesReviewedDelivery)
        => continuesReviewedDelivery
            ? "This round starts from the delivery the review graded, so that work is already in your checkout; change only what the findings require."
            : "This round starts from the integration branch because the reviewed delivery could not be named, so rebuild the delivery with the findings as constraints.";

    /// <summary>
    /// The timeline row naming the chosen base of the round. Written once per
    /// reviewed attempt, so a retried entry point does not repeat it.
    /// </summary>
    public static void RecordOnTimeline(
        TimelineLog? timeline,
        string folderPath,
        ContinuationBaseRecord record,
        string source,
        DateTime atUtc)
    {
        if (timeline is null) return;
        if (timeline.ReadAll(folderPath).Any(row =>
                row.Kind == TimelineEventKinds.ContinuationBaseRecorded
                && row.Details?.GetValueOrDefault("reviewAttemptId") == record.AttemptId
                && row.Details?.GetValueOrDefault("reason") == record.Reason
                && row.Details?.GetValueOrDefault("ref") == record.Ref
                && row.Details?.GetValueOrDefault("sha") == record.CommitSha))
            return;
        timeline.Append(folderPath, new TimelineEvent
        {
            Ts = atUtc,
            Kind = TimelineEventKinds.ContinuationBaseRecorded,
            Actor = TimelineActors.System,
            Summary = $"The fix round starts from the reviewed delivery {record.Ref} at {ShortSha(record.CommitSha)}.",
            RunId = record.AttemptId,
            Details = new Dictionary<string, string>
            {
                ["ref"] = record.Ref,
                ["sha"] = record.CommitSha,
                ["reason"] = record.Reason,
                ["reviewAttemptId"] = record.AttemptId,
                ["source"] = source,
            },
        });
    }

    private static string ShortSha(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
