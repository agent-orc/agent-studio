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
    /// The green baseline, red uncached repeat, and card-specific fingerprint
    /// confirm a delivery regression. Blocks the card.
    /// </summary>
    Delivery,

    /// <summary>
    /// The command was already failing on the merge base. Reported as an
    /// integration-branch defect with its own alert, never charged to the card.
    /// </summary>
    IntegrationBranch,

    /// <summary>The uncached repeat cleared the failure or another card shares it.</summary>
    Environment,

    /// <summary>
    /// An intermittent or reviewer-concern result charges neither the card nor
    /// the branch.
    /// </summary>
    Tolerated,

    /// <summary>A required diagnosis measurement was absent; no card charge.</summary>
    Undiagnosed,
}

/// <summary>
/// Pure projection of a completed diagnosis into review ownership.
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
/// A command's exit status alone cannot attribute a failure. The required
/// baseline, clean repeat, and fingerprint evidence is classified first.
/// </para>
/// </summary>
public static class ReviewFailureAttributionPolicy
{
    /// <summary>
    /// Attributes one verification command from its frozen plan entry and its
    /// recorded evidence.
    /// </summary>
    /// <remarks>
    /// Missing diagnosis proof is undiagnosed and cannot charge a card.
    /// </remarks>
    public static ReviewFailureOwner Attribute(
        ReviewCommandDto? planned,
        ReviewCommandEvidenceDto evidence)
    {
        if (!Failed(evidence)) return ReviewFailureOwner.None;
        if (planned?.CompareToBaseline != true) return ReviewFailureOwner.Undiagnosed;
        if (Enum.TryParse<DeliveryFailureClass>(evidence.DiagnosisClass, true, out var diagnosis))
            return diagnosis switch
            {
                DeliveryFailureClass.Product when !string.IsNullOrWhiteSpace(evidence.BaselineSha)
                    && evidence.BaselineExitCode == 0
                    && evidence.CleanRepeatExitCode is not null and not 0
                    && evidence.CleanRepeatSameFingerprint
                    && !evidence.FingerprintSeenOnOtherCardWithin24Hours
                    => ReviewFailureOwner.Delivery,
                DeliveryFailureClass.Environment => evidence.BaselineExitCode != 0
                    ? ReviewFailureOwner.IntegrationBranch
                    : ReviewFailureOwner.Environment,
                DeliveryFailureClass.Flaky or DeliveryFailureClass.Concern => ReviewFailureOwner.Tolerated,
                _ => ReviewFailureOwner.Undiagnosed,
            };
        return ReviewFailureOwner.Undiagnosed;
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
