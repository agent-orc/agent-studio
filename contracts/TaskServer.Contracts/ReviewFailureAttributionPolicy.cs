namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Who owns a failing review command: the delivery under review, the
/// integration branch it is measured against, or nobody.
/// </summary>
public enum ReviewFailureOwner
{
    /// <summary>The command did not fail.</summary>
    None,

    /// <summary>
    /// The failure is new against the merge base, so the delivery under review
    /// introduced it. Blocks the card.
    /// </summary>
    Delivery,

    /// <summary>
    /// The command was already failing on the merge base and the delivery added
    /// no failure of its own. The integration branch owns it: reported as an
    /// integration-branch defect with its own alert, never charged to the card.
    /// </summary>
    IntegrationBranch,

    /// <summary>
    /// The command failed but every failure it named is accounted for by the
    /// flaky-test retry, so it charges neither the card nor the branch.
    /// </summary>
    Tolerated,
}

/// <summary>
/// Pure attribution of one failing review command (AGT-2819).
/// <para>
/// The failure this settles: <c>npm --prefix frontend run lint</c> had been red
/// on <c>develop</c> since 2026-08-19 because the component-size baseline had
/// rotted. Every delivery ran the same lint as review step <c>verify-5</c>, it
/// exited 1 for all of them, and every remote review graded
/// <c>ProductFailure</c>. Weeks of accumulated debt on one branch presented as a
/// product failure on unrelated cards, the acceptance rail refused each one, and
/// deliveries were merged by hand instead.
/// </para>
/// <para>
/// A command's exit status alone therefore cannot attribute a failure. The merge
/// base has to be consulted first, and the answer is one of three things, not
/// two: green over there, new failure here, or already broken over there.
/// </para>
/// </summary>
public static class ReviewFailureAttributionPolicy
{
    /// <summary>
    /// Attributes one verification command from its frozen plan entry and its
    /// recorded evidence.
    /// </summary>
    /// <remarks>
    /// Fails closed to <see cref="ReviewFailureOwner.Delivery"/>: a failing
    /// command without complete baseline evidence is charged to the card, so a
    /// missing or malformed comparison can never launder a real regression into
    /// branch debt.
    /// </remarks>
    public static ReviewFailureOwner Attribute(
        ReviewCommandDto? planned,
        ReviewCommandEvidenceDto evidence)
    {
        if (!Failed(evidence)) return ReviewFailureOwner.None;
        if (planned?.CompareToBaseline != true) return ReviewFailureOwner.Delivery;
        return Attribute(
            commandFailed: true,
            planned.BaselineMode,
            evidence.BaselineSha,
            evidence.BaselineExitCode,
            evidence.NewFailures);
    }

    /// <summary>
    /// The decision table, in terms of the facts that carry it. Exposed
    /// separately so the matrix can be tested without building evidence
    /// records.
    /// </summary>
    public static ReviewFailureOwner Attribute(
        bool commandFailed,
        string? baselineMode,
        string? baselineSha,
        int? baselineExitCode,
        IReadOnlyList<string>? newFailures)
    {
        if (!commandFailed) return ReviewFailureOwner.None;

        // No baseline was measured: the card carries it.
        if (string.IsNullOrWhiteSpace(baselineSha) || baselineExitCode is null)
            return ReviewFailureOwner.Delivery;

        // Named failures the merge base did not have are the card's own, even
        // when the step was already red over there.
        if (newFailures is null || newFailures.Count > 0) return ReviewFailureOwner.Delivery;

        if (baselineExitCode != 0) return ReviewFailureOwner.IntegrationBranch;

        // Green on the merge base, red here, and no new failure name to point
        // at. Under exit-status comparison there are no names at all, so the
        // exit codes are the whole evidence and the card turned it red. Under
        // failure-name comparison the names are authoritative and an empty
        // difference means the retry accounted for every failure.
        return ReviewBaselineModes.IsExitStatus(baselineMode)
            ? ReviewFailureOwner.Delivery
            : ReviewFailureOwner.Tolerated;
    }

    /// <summary>
    /// True when the command evidence records a failure at all. A signal or a
    /// negative exit code is an abnormal termination and is classified upstream
    /// as review infrastructure, so it counts as a failure here too.
    /// </summary>
    public static bool Failed(ReviewCommandEvidenceDto evidence)
        => evidence.Signal is not null
           || evidence.ExitCode is null or < 0
           || evidence.ExitCode != 0;
}
