namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// What a settled review says about the change, before it is projected onto the
/// narrower wire outcome vocabulary.
/// </summary>
public enum ReviewGrade
{
    /// <summary>Every aspect passed and every command produced a clean result.</summary>
    Pass,

    /// <summary>At least one aspect raised concerns and none blocked. The change
    /// is acceptable; the concerns ride along on the card.</summary>
    PassWithConcerns,

    /// <summary>A reviewer blocked the diff, or a command produced parsed test
    /// failures or compiler errors.</summary>
    ProductFailure,

    /// <summary>The review could not produce a trustworthy verdict because the
    /// host, network, or toolchain failed.</summary>
    InfrastructureFailure,
}

/// <summary>One graded review, ready to be reported on the wire.</summary>
/// <param name="Grade">The grade the table produced.</param>
/// <param name="WireOutcome">One of the five accepted wire outcomes.</param>
/// <param name="Classification">Wire classification, or null when there is
/// nothing to classify.</param>
public sealed record ReviewOutcomeDecision(
    ReviewGrade Grade,
    string WireOutcome,
    string? Classification);

/// <summary>
/// The single table that turns aspect verdicts and command evidence into a
/// review outcome.
/// <para>
/// This mapping used to exist twice, spelled differently each time: once on the
/// review runner (<c>RemoteReviewWorkspace</c>) and once as the authority on the
/// task server (<c>TaskServerReviewStore</c>). Both wrote
/// <c>ProductFailure</c> for a <c>concerns</c> verdict, which is how AGT-2706
/// was parked as broken product with every aspect passing and a single
/// <c>documentation-impact: concerns</c>. Concerns are a note on an acceptable
/// change; the repository already calls that <c>accept-with-concerns</c> in the
/// decision vocabulary, and this policy keeps the two consistent.
/// </para>
/// <para>
/// Precedence is deliberate: infrastructure outranks everything. When the host
/// failed, the product verdicts in the same report were not produced under
/// conditions worth trusting, so the card is retried rather than judged.
/// </para>
/// </summary>
public static class ReviewOutcomeGradingPolicy
{
    /// <summary>Wire outcome for a pass, with or without concerns.</summary>
    public const string PassOutcome = "Pass";

    /// <summary>Wire outcome for a change that is genuinely at fault.</summary>
    public const string ProductFailureOutcome = "ProductFailure";

    /// <summary>Wire outcome for a host, network, or toolchain failure.</summary>
    public const string InfrastructureOutcome = "ReviewInfra";

    /// <summary>
    /// Classification carried alongside <see cref="PassOutcome"/> when at least
    /// one aspect raised concerns. Matches the existing
    /// <c>accept-with-concerns</c> decision verdict so the card, the timeline
    /// detail, and the review report all name the same thing.
    /// </summary>
    public const string AcceptWithConcernsClassification = "AcceptWithConcerns";

    /// <summary>Classification for a review that reported an unreadable verdict.</summary>
    public const string InvalidAspectVerdictClassification = "InvalidAspectVerdict";

    /// <summary>Classification for a blocked or failing aspect.</summary>
    public const string ReviewFindingClassification = "ReviewFinding";

    /// <summary>
    /// Grades one aspect verdict. An unrecognised status is graded
    /// <see cref="ReviewGrade.InfrastructureFailure"/> rather than passed: a
    /// reviewer that answered with something the contract does not define did
    /// not answer at all.
    /// </summary>
    public static ReviewGrade GradeAspect(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "pass" => ReviewGrade.Pass,
            "concerns" => ReviewGrade.PassWithConcerns,
            "block" or "fail" => ReviewGrade.ProductFailure,
            _ => ReviewGrade.InfrastructureFailure,
        };

    /// <summary>
    /// Grades a whole review from its aspect verdicts and its command evidence.
    /// </summary>
    /// <param name="aspectStatuses">Reported aspect verdict statuses.</param>
    /// <param name="hasProductCommandFailure">Whether a verification command
    /// produced parsed test failures or compiler errors. Callers derive this
    /// with <see cref="GateFailureClassifier.ClassifyVerificationCommand"/> so
    /// an unreadable result never lands here.</param>
    /// <param name="commandFailure">Classification of the failing command, when
    /// one failed. An infrastructure or quota class outranks every verdict.</param>
    /// <param name="reportedClassification">Classification the reporter already
    /// chose, preserved when this policy has nothing more specific.</param>
    public static ReviewOutcomeDecision Decide(
        IEnumerable<string?> aspectStatuses,
        bool hasProductCommandFailure = false,
        GateFailureClassification? commandFailure = null,
        string? reportedClassification = null)
    {
        var grades = aspectStatuses.Select(GradeAspect).ToArray();

        if (commandFailure is not null && commandFailure.IsRetryable)
        {
            return new ReviewOutcomeDecision(
                ReviewGrade.InfrastructureFailure,
                InfrastructureOutcome,
                commandFailure.Signature);
        }

        if (grades.Contains(ReviewGrade.InfrastructureFailure))
        {
            return new ReviewOutcomeDecision(
                ReviewGrade.InfrastructureFailure,
                InfrastructureOutcome,
                InvalidAspectVerdictClassification);
        }

        if (hasProductCommandFailure || grades.Contains(ReviewGrade.ProductFailure))
        {
            return new ReviewOutcomeDecision(
                ReviewGrade.ProductFailure,
                ProductFailureOutcome,
                string.IsNullOrWhiteSpace(reportedClassification)
                    ? ReviewFindingClassification
                    : reportedClassification);
        }

        return grades.Contains(ReviewGrade.PassWithConcerns)
            ? new ReviewOutcomeDecision(
                ReviewGrade.PassWithConcerns,
                PassOutcome,
                AcceptWithConcernsClassification)
            : new ReviewOutcomeDecision(ReviewGrade.Pass, PassOutcome, reportedClassification);
    }
}
