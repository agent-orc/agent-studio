using System.Globalization;
using System.Text;

namespace AgentStudio.Runner;

/// <summary>
/// One graded ReviewAttempt reduced to the facts an operator summary needs.
/// Deliberately independent of <see cref="ReviewAttemptDto"/> so the summary
/// policy is a direct matrix test without an attempt store, a clock, or a
/// filesystem.
/// </summary>
public sealed record ReviewAttemptChainEntry(
    string AttemptId,
    DateTime CreatedAt,
    DateTime? TerminalAt,
    ReviewTerminalOutcome? Outcome,
    string? FailureClassification,
    string? TerminalReason)
{
    /// <summary>When this attempt produced its evidence: the terminal stamp, or
    /// the creation stamp while the attempt is still open.</summary>
    public DateTime ObservedAt => TerminalAt ?? CreatedAt;

    /// <summary>True once the attempt carries a review verdict. A Superseded
    /// attempt was revoked by the authority and never graded anything, so it
    /// must not compete for "newest cause".</summary>
    public bool IsGraded => Outcome is not null and not ReviewTerminalOutcome.Superseded;

    /// <summary>The wire-shaped <c>Outcome/Classification</c> label the review
    /// report carries, e.g. <c>ReviewInfra/ShaMismatch</c>.</summary>
    public string Classification => ClassificationLabel(Outcome, FailureClassification);

    public static ReviewAttemptChainEntry From(ReviewAttemptDto review) => new(
        review.AttemptId,
        review.CreatedAt,
        review.TerminalAt,
        review.Outcome,
        review.FailureClassification,
        review.TerminalReason);

    /// <summary>Renders the operator-facing outcome label. The outcome uses the
    /// wire spelling the review report used (<c>ReviewInfra</c>, not the
    /// internal <c>InfrastructureFailure</c>) so a summary and a report can be
    /// read side by side.</summary>
    public static string ClassificationLabel(ReviewTerminalOutcome? outcome, string? failureClassification)
    {
        var wire = outcome switch
        {
            ReviewTerminalOutcome.InfrastructureFailure => "ReviewInfra",
            ReviewTerminalOutcome.ProductFailure => "ProductFailure",
            ReviewTerminalOutcome.Inconclusive => "Inconclusive",
            ReviewTerminalOutcome.Cancellation => "Cancellation",
            ReviewTerminalOutcome.Pass => "Pass",
            ReviewTerminalOutcome.PassWithConcerns => "PassWithConcerns",
            ReviewTerminalOutcome.Superseded => "Superseded",
            _ => "Ungraded",
        };
        var classification = (failureClassification ?? string.Empty).Trim();
        return classification.Length == 0 ? wire : $"{wire}/{classification}";
    }
}

/// <summary>
/// Every graded attempt that carried one distinct failure classification, plus
/// the window in which that classification was observed. The enumeration is
/// never truncated and never folded into a "dominant" class: a classification
/// seen once keeps its own row.
/// </summary>
public sealed record ReviewAttemptClassificationGroup(
    string Classification,
    int Count,
    DateTime FirstObservedAt,
    DateTime LastObservedAt,
    IReadOnlyList<string> AttemptIds);

/// <summary>
/// Pure operator summary of a ReviewAttempt chain, used by every park and
/// escalation that hands a review subject back to a human.
///
/// <para>AGT-2220 is the incident this policy exists for: the chain summary
/// counted attempts by frequency, reported the dominant class ("4 attempts, all
/// ReviewInfra/BaselineUnavailable"), and concluded "infrastructure problem,
/// no finding against the card content". A fifth, younger attempt classified
/// <c>ShaMismatch</c> ("Materialized HEAD does not match expected Result-SHA")
/// was the actual hard blocker and appeared in no summary, so the options the
/// operator was offered (a fresh merge) could not fix the problem at all.</para>
///
/// <para>The rules that follow from it: the newest graded attempt is always
/// named, every distinct classification is enumerated with its dates, attempts
/// that diverge from the newest classification are called out explicitly, and
/// the operator options are derived from the NEWEST cause rather than from the
/// most frequent one.</para>
/// </summary>
public sealed record ReviewAttemptChainSummary(
    IReadOnlyList<ReviewAttemptChainEntry> GradedAttempts,
    ReviewAttemptChainEntry? NewestAttempt,
    IReadOnlyList<ReviewAttemptClassificationGroup> Classifications,
    IReadOnlyList<ReviewAttemptChainEntry> DivergentAttempts,
    IReadOnlyList<string> Options,
    int UngradedCount)
{
    /// <summary>Per-attempt listing cap for the rendered detail block. The
    /// classification enumeration is never capped; when this cap bites, the
    /// omission is stated instead of silently truncating the chain.</summary>
    public const int MaxListedAttempts = 12;

    /// <summary>True when the chain holds more than one distinct classification,
    /// which is exactly the AGT-2220 shape a frequency summary hides.</summary>
    public bool HasDivergentClassifications => Classifications.Count > 1;

    public static ReviewAttemptChainSummary Build(IEnumerable<ReviewAttemptChainEntry>? attempts)
    {
        var all = (attempts ?? []).Where(attempt => attempt is not null).ToList();
        // Newest first by observed evidence, then by creation, then by id: the
        // ordering is total so two attempts settled in the same second cannot
        // swap the "newest cause" between two renders of the same chain.
        var graded = all
            .Where(attempt => attempt.IsGraded)
            .OrderByDescending(attempt => attempt.ObservedAt)
            .ThenByDescending(attempt => attempt.CreatedAt)
            .ThenBy(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .ToList();
        var newest = graded.FirstOrDefault();
        var classifications = graded
            .GroupBy(attempt => attempt.Classification, StringComparer.Ordinal)
            .Select(group => new ReviewAttemptClassificationGroup(
                group.Key,
                group.Count(),
                group.Min(attempt => attempt.ObservedAt),
                group.Max(attempt => attempt.ObservedAt),
                group.Select(attempt => attempt.AttemptId).ToList()))
            .OrderByDescending(group => group.LastObservedAt)
            .ThenBy(group => group.Classification, StringComparer.Ordinal)
            .ToList();
        var divergent = newest is null
            ? new List<ReviewAttemptChainEntry>()
            : graded
                .Where(attempt => !string.Equals(
                    attempt.Classification, newest.Classification, StringComparison.Ordinal))
                .ToList();
        return new ReviewAttemptChainSummary(
            graded,
            newest,
            classifications,
            divergent,
            OperatorOptions(newest),
            all.Count - graded.Count);
    }

    /// <summary>
    /// One-line situation report for the escalation reason and the decision
    /// journal. It always names the newest attempt and always states whether
    /// the chain is uniform or divergent, so no reader can infer "all attempts
    /// failed the same way" from a count.
    /// </summary>
    public string Headline
    {
        get
        {
            if (NewestAttempt is null)
            {
                return UngradedCount == 0
                    ? "No graded review attempt exists for this subject."
                    : $"No graded review attempt exists for this subject ({UngradedCount} attempt(s) still open or superseded).";
            }

            var builder = new StringBuilder();
            builder.Append(GradedAttempts.Count)
                .Append(GradedAttempts.Count == 1 ? " graded review attempt. Newest: " : " graded review attempts. Newest: ")
                .Append(NewestAttempt.AttemptId)
                .Append(" (")
                .Append(Stamp(NewestAttempt.ObservedAt))
                .Append(") classified ")
                .Append(NewestAttempt.Classification)
                .Append('.');
            var reason = FirstSentence(NewestAttempt.TerminalReason);
            if (reason.Length > 0) builder.Append(' ').Append(reason);
            if (DivergentAttempts.Count > 0)
            {
                builder.Append(" Divergent chain: ")
                    .Append(DivergentAttempts.Count)
                    .Append(" of ")
                    .Append(GradedAttempts.Count)
                    .Append(" attempts carry a different classification (")
                    .Append(string.Join(", ", Classifications
                        .Where(group => !string.Equals(
                            group.Classification, NewestAttempt.Classification, StringComparison.Ordinal))
                        .Select(group => group.Classification)))
                    .Append("), so decide on the newest cause, not on the most frequent one.");
            }
            else
            {
                builder.Append(" All ")
                    .Append(GradedAttempts.Count)
                    .Append(GradedAttempts.Count == 1 ? " attempt carries" : " attempts carry")
                    .Append(" this classification.");
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// Markdown block appended to the parked card's status.md: the newest
    /// attempt, the complete dated classification enumeration, the divergent
    /// attempts, the attempt chain, and the operator options for the newest
    /// cause.
    /// </summary>
    public string Detail
    {
        get
        {
            var newline = Environment.NewLine;
            var builder = new StringBuilder();
            // The headline belongs to the caller's one-line reason; repeating it
            // here would only push the facts below the fold.
            if (NewestAttempt is null)
                return builder.Append("- Review attempt chain: ").Append(Headline).Append(newline).ToString();

            builder.Append("- Newest attempt: ")
                .Append(Stamp(NewestAttempt.ObservedAt)).Append(' ')
                .Append(NewestAttempt.AttemptId).Append(' ')
                .Append(NewestAttempt.Classification);
            var newestReason = Flatten(NewestAttempt.TerminalReason);
            if (newestReason.Length > 0) builder.Append(": ").Append(newestReason);
            builder.Append(newline);

            builder.Append("- Failure classifications (")
                .Append(Classifications.Count)
                .Append(" distinct, complete, newest first):")
                .Append(newline);
            foreach (var group in Classifications)
            {
                builder.Append("  - ").Append(group.Classification).Append(": ")
                    .Append(group.Count)
                    .Append(group.Count == 1 ? " attempt, " : " attempts, ")
                    .Append(group.Count == 1 || group.FirstObservedAt == group.LastObservedAt
                        ? Stamp(group.LastObservedAt)
                        : $"{Stamp(group.FirstObservedAt)} to {Stamp(group.LastObservedAt)}")
                    .Append(" (").Append(string.Join(", ", group.AttemptIds)).Append(')')
                    .Append(newline);
            }

            if (DivergentAttempts.Count > 0)
            {
                builder.Append("- Divergent attempts: ")
                    .Append(DivergentAttempts.Count).Append(" of ").Append(GradedAttempts.Count)
                    .Append(" attempts are classified differently from the newest cause ")
                    .Append(NewestAttempt.Classification)
                    .Append("; decide on the newest cause, not on the most frequent one.")
                    .Append(newline);
                foreach (var attempt in DivergentAttempts)
                {
                    builder.Append("  - ").Append(Stamp(attempt.ObservedAt)).Append(' ')
                        .Append(attempt.AttemptId).Append(' ').Append(attempt.Classification)
                        .Append(newline);
                }
            }

            builder.Append("- Attempt chain (newest first):").Append(newline);
            var listed = Math.Min(GradedAttempts.Count, MaxListedAttempts);
            for (var index = 0; index < listed; index++)
            {
                var attempt = GradedAttempts[index];
                builder.Append("  - ").Append(Stamp(attempt.ObservedAt)).Append(' ')
                    .Append(attempt.AttemptId).Append(' ').Append(attempt.Classification);
                if (index == 0) builder.Append(" (newest cause)");
                var reason = Flatten(attempt.TerminalReason);
                if (reason.Length > 0) builder.Append(": ").Append(reason);
                builder.Append(newline);
            }
            if (GradedAttempts.Count > MaxListedAttempts)
            {
                builder.Append("  - (")
                    .Append(GradedAttempts.Count - MaxListedAttempts)
                    .Append(" older attempts omitted from this listing; the classification enumeration above stays complete.)")
                    .Append(newline);
            }

            if (UngradedCount > 0)
            {
                builder.Append("- Not graded: ").Append(UngradedCount)
                    .Append(" attempt(s) still open or superseded; they carry no review verdict.")
                    .Append(newline);
            }

            builder.Append("- Operator options for the newest cause ")
                .Append(NewestAttempt.Classification).Append(':').Append(newline);
            for (var index = 0; index < Options.Count; index++)
                builder.Append("  ").Append(index + 1).Append(". ").Append(Options[index]).Append(newline);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Remediation options for the NEWEST cause. Deriving them from the newest
    /// classification is the AGT-2220 counter-check: the frequency summary
    /// offered a fresh merge (the remedy for the four older baseline faults)
    /// while the actual blocker was an immutable Result-SHA mismatch that a
    /// fresh merge cannot touch.
    /// </summary>
    private static IReadOnlyList<string> OperatorOptions(ReviewAttemptChainEntry? newest)
    {
        if (newest is null)
        {
            return
            [
                "No graded attempt exists yet; requeue the card for auto review and let the review plane produce a verdict.",
            ];
        }

        var classification = (newest.FailureClassification ?? string.Empty).Trim();
        return classification.ToLowerInvariant() switch
        {
            "shamismatch" =>
            [
                "Establish which commit the delivery ref actually points at: the recorded Result-SHA was not the materialized HEAD, so the subject under review never existed as recorded.",
                "Re-run the source coding attempt so it publishes a reachable Result-Envelope, then let a new ReviewSubject be created from it.",
                "Do not requeue or re-merge this subject unchanged: its Result-SHA is immutable, so a fresh merge reproduces the same mismatch.",
            ],
            "snapshotunavailable" =>
            [
                "Check that this subject's result ref or source bundle is still present on the repository the review executor fetches from.",
                "Re-publish the source run's Result-Envelope, then requeue the card for auto review.",
                "Do not read this as a finding against the card content: no product check ran.",
            ],
            "baselineunavailable" =>
            [
                "Restore the baseline ref (merge base or default branch) in the review host's mirror, then requeue the card for auto review.",
                "Verify the project's repository URL and the review host's fetch credentials.",
                "Do not read this as a finding against the card content: no product check ran.",
            ],
            "repositorymismatch" =>
            [
                "Reconcile the project's configured repository with the repository identity recorded on the immutable ReviewSubject.",
                "Requeue the card for auto review only once both identities match.",
            ],
            "dirtybefore" or "mutatedafter" =>
            [
                "Clean the review host workspace: the tree was not clean around the review, so its evidence is not trustworthy.",
                "Requeue the card for auto review once the host is clean.",
            ],
            _ => newest.Outcome switch
            {
                ReviewTerminalOutcome.ProductFailure =>
                [
                    "Read the newest review report: it is a finding against the card content, not an infrastructure fault.",
                    "Reissue the card with that finding as the correction brief.",
                ],
                ReviewTerminalOutcome.Inconclusive =>
                [
                    "Read the newest review report and decide manually: the review plane reached no verdict.",
                    "Requeue the card for auto review only after the reason for the inconclusive run is removed.",
                ],
                ReviewTerminalOutcome.Cancellation =>
                [
                    "The newest attempt was cancelled, so no verdict exists; requeue the card for auto review.",
                ],
                _ =>
                [
                    "Read the newest attempt's terminal reason above and decide from it: this classification has no scripted remediation.",
                    "Requeue the card for auto review only if the newest cause is transient.",
                ],
            },
        };
    }

    private static string Stamp(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Flatten(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Bounded first sentence of a terminal reason, so the one-line
    /// headline stays a line even when the executor wrote a paragraph. The
    /// untruncated reason stays available in <see cref="Detail"/>.</summary>
    private static string FirstSentence(string? value)
    {
        const int maxLength = 240;
        var flat = Flatten(value);
        if (flat.Length == 0) return string.Empty;
        var stop = flat.IndexOf(". ", StringComparison.Ordinal);
        var sentence = stop < 0 ? flat : flat[..(stop + 1)];
        return sentence.Length <= maxLength ? sentence : sentence[..maxLength].TrimEnd() + " [...]";
    }
}
