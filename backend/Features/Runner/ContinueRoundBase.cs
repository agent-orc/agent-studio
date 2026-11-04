using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// One result ref a card recorded: the immutable ref of a settled run's result
/// envelope, or the ref a review subject named for the delivery it graded.
/// </summary>
public sealed record CardResultRef(string Ref, string Sha, string AttemptId, DateTime RecordedAtUtc);

/// <summary>Why a continue round starts where it does; returned on the response and the timeline.</summary>
public static class ContinueRoundBaseSources
{
    /// <summary>The card's latest recorded result ref, chosen without the caller naming it.</summary>
    public const string LatestResult = "latest-result";
    /// <summary>The <c>baseRef</c> the caller named, validated against the card's refs.</summary>
    public const string Explicit = "explicit";
    /// <summary>The reviewed delivery of an AGT-3056 fix round.</summary>
    public const string ReviewFixRound = "review-fix-round";
    /// <summary>A base another round already saved on the card (salvage, older-brief starting point).</summary>
    public const string Existing = "existing";
}

public enum ContinueRoundBaseAction
{
    /// <summary>No base: the round starts as it did before AGT-3483.</summary>
    None,
    /// <summary>Save <see cref="ContinueRoundBaseDecision.Base"/> as the card's continuation base.</summary>
    Use,
    /// <summary>The card already carries a base of another round; keep it.</summary>
    Keep,
    /// <summary>The named ref is not one of the card's result refs (400).</summary>
    RejectUnknownRef,
    /// <summary>A ref was named for a round that no runner claim will start (409).</summary>
    RejectNotClaimed,
}

public sealed record ContinueRoundBaseDecision(
    ContinueRoundBaseAction Action,
    ContinuationBaseRecord? Base = null,
    string? Source = null,
    string? Code = null,
    string? Message = null);

/// <summary>
/// Pure decision for the base of an operator continue, steer or extend round
/// (AGT-3483, operator-agent-level Dossier guardrail "Continue rounds carry the
/// result ref"). A round on a card with a recorded result ref starts from the
/// latest one unless the caller names another ref of the same card; the
/// operator no longer types refs into the prompt.
///
/// <para>
/// The base only travels through a runner claim. A round that starts locally,
/// or that joins a run still live on the card, keeps today's checkout: an
/// explicit ref there is refused rather than silently ignored.
/// </para>
/// </summary>
public static class ContinueRoundBasePolicy
{
    public const string Reason = ContinuationBaseReasons.ContinueRoundResult;
    public const string UnknownRefCode = "base-ref-not-on-card";
    public const string NotClaimedCode = "base-ref-not-applicable";

    /// <param name="requestedRef">The caller's <c>baseRef</c>, or null for the default.</param>
    /// <param name="cardRefs">Every result ref the card recorded, see <see cref="Collect"/>.</param>
    /// <param name="existingBase">A base another round already saved on the card.</param>
    /// <param name="startsFromClaim">
    /// True when the round is queued for a runner claim and no run of the card
    /// is live, so the claim hands the saved base to the runner.
    /// </param>
    public static ContinueRoundBaseDecision Decide(
        string? requestedRef,
        IReadOnlyList<CardResultRef> cardRefs,
        ContinuationBaseRecord? existingBase,
        bool startsFromClaim,
        DateTime savedAtUtc)
    {
        var known = cardRefs
            .Select(item => NormalizeRef(item.Ref) is { } reference
                && ReviewSubjectStore.IsValidResultSha(item.Sha)
                    ? item with { Ref = reference, Sha = item.Sha.Trim().ToLowerInvariant() }
                    : null)
            .OfType<CardResultRef>()
            .OrderBy(item => item.RecordedAtUtc)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedRef))
        {
            var wanted = NormalizeRef(requestedRef);
            var match = wanted is null ? null : known.LastOrDefault(item => item.Ref == wanted);
            if (match is null)
                return new ContinueRoundBaseDecision(ContinueRoundBaseAction.RejectUnknownRef,
                    Code: UnknownRefCode,
                    Message: $"{UnknownRefCode}: '{requestedRef.Trim()}' is not a result ref of this card.");
            if (!startsFromClaim)
                return new ContinueRoundBaseDecision(ContinueRoundBaseAction.RejectNotClaimed,
                    Code: NotClaimedCode,
                    Message: $"{NotClaimedCode}: this round does not start from a runner claim "
                        + "(a run of the card is live, or the project runs locally), so it cannot start from a named ref.");
            return Use(match, ContinueRoundBaseSources.Explicit, savedAtUtc);
        }

        if (!startsFromClaim) return new ContinueRoundBaseDecision(ContinueRoundBaseAction.None);
        if (existingBase is not null)
            return new ContinueRoundBaseDecision(ContinueRoundBaseAction.Keep, existingBase,
                ReviewFixRoundBase.IsFixRound(existingBase.Reason)
                    ? ContinueRoundBaseSources.ReviewFixRound
                    : ContinueRoundBaseSources.Existing);
        var latest = known.LastOrDefault();
        return latest is null
            ? new ContinueRoundBaseDecision(ContinueRoundBaseAction.None)
            : Use(latest, ContinueRoundBaseSources.LatestResult, savedAtUtc);
    }

    /// <summary>
    /// The card's result refs: each settled run's immutable envelope ref, each
    /// review subject's ref, and the card's <c>review-subject.json</c> (its
    /// immutable ref, then a legacy mutable one).
    /// </summary>
    public static IReadOnlyList<CardResultRef> Collect(
        AttemptAuthorityProjection? projection,
        ReviewSubjectRecord? folderSubject)
    {
        var refs = new List<CardResultRef>();
        if (projection is not null)
        {
            foreach (var run in projection.RunAttempts)
                if (run.ResultEnvelope is { ImmutableRemoteRef: { } reference } envelope)
                    refs.Add(new CardResultRef(reference, envelope.ResultSha, run.AttemptId,
                        run.TerminalAt ?? run.CreatedAt));
            var subjects = projection.ReviewAttempts.Select(attempt => attempt.Subject);
            if (projection.CurrentReviewSubject is not null)
                subjects = subjects.Append(projection.CurrentReviewSubject);
            foreach (var subject in subjects)
                if (!string.IsNullOrWhiteSpace(subject.ResultRef))
                    refs.Add(new CardResultRef(subject.ResultRef, subject.ExpectedResultSha,
                        subject.SourceRunAttemptId, subject.CreatedAt));
        }
        if (folderSubject is not null)
        {
            var completed = folderSubject.CompletedAtUtc.UtcDateTime;
            if (!string.IsNullOrWhiteSpace(folderSubject.ResultRef))
                refs.Add(new CardResultRef(folderSubject.ResultRef, folderSubject.ResultSha,
                    folderSubject.RunAttemptId, completed));
            if (!string.IsNullOrWhiteSpace(folderSubject.ImmutableResultRef))
                refs.Add(new CardResultRef(folderSubject.ImmutableResultRef, folderSubject.ResultSha,
                    folderSubject.RunAttemptId, completed));
        }
        return refs;
    }

    /// <summary>
    /// The full branch ref a runner can fetch, or null for anything that is
    /// not a branch (tags, notes, other ref namespaces).
    /// </summary>
    public static string? NormalizeRef(string? value)
    {
        var reference = (value ?? string.Empty).Trim();
        if (reference.StartsWith("origin/", StringComparison.Ordinal))
            reference = reference["origin/".Length..];
        if (reference.Length == 0
            || (reference.StartsWith("refs/", StringComparison.Ordinal)
                && !reference.StartsWith("refs/heads/", StringComparison.Ordinal)))
            return null;
        return reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference
            : "refs/heads/" + reference;
    }

    /// <summary>
    /// The timeline row naming where the round starts. The card timeline is the
    /// operator's record of which ref a continue round was given.
    /// </summary>
    public static void RecordOnTimeline(
        TimelineLog? timeline,
        string folderPath,
        ContinuationBaseRecord record,
        string source,
        string mode,
        string? actor,
        DateTime atUtc)
    {
        if (timeline is null) return;
        var origin = source == ContinueRoundBaseSources.Explicit
            ? "the requested result"
            : "the card's latest result";
        timeline.Append(folderPath, new TimelineEvent
        {
            Ts = atUtc,
            Kind = TimelineEventKinds.ContinuationBaseRecorded,
            Actor = string.IsNullOrWhiteSpace(actor) ? TimelineActors.System : actor,
            Summary = $"The {mode} round starts from {origin} {record.Ref} at {ShortSha(record.CommitSha)}.",
            RunId = record.AttemptId,
            Details = new Dictionary<string, string>
            {
                ["ref"] = record.Ref,
                ["sha"] = record.CommitSha,
                ["reason"] = record.Reason,
                ["resultAttemptId"] = record.AttemptId,
                ["source"] = ReviewFixRoundBase.SourceOperatorContinue,
                ["base"] = source,
                ["mode"] = mode,
            },
        });
    }

    private static ContinueRoundBaseDecision Use(CardResultRef reference, string source, DateTime savedAtUtc)
        => new(ContinueRoundBaseAction.Use,
            new ContinuationBaseRecord(reference.Ref, reference.Sha, Reason, reference.AttemptId, savedAtUtc),
            source);

    private static string ShortSha(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
