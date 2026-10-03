namespace AgentStudio.Pipeline;

/// <summary>What the integration lane does next with a delivery the branch already contains.</summary>
public enum IntegrationVerificationAction
{
    /// <summary>The outcome is not "already contained"; the merge path owns its own gate.</summary>
    NotApplicable,

    /// <summary>A gate passed on the exact merged tree; the card may complete.</summary>
    CompleteVerified,

    /// <summary>No evidence exists; run the gate once on the current branch tip.</summary>
    RunGate,

    /// <summary>
    /// A gate reached a product verdict on the exact merged tree and it
    /// failed. The card returns to Human Review with the verdict and a cause
    /// card is opened for the branch.
    /// </summary>
    FailUnverified,

    /// <summary>
    /// No verdict about the tree was reached: the SHA could not be named, or
    /// the gate stopped on its host, budget, or source. The card does not
    /// complete, but nothing is known about the branch, so no cause card is
    /// opened. An environment failure is replayed by the gate-environment
    /// retry ladder.
    /// </summary>
    GateUnresolved,
}

/// <summary>Where the verdict on a contained delivery came from.</summary>
public static class IntegrationVerificationEvidence
{
    /// <summary>A durable gate receipt on the card for the exact merged tree.</summary>
    public const string GateReceipt = "gate-receipt";

    /// <summary>An <c>integrated-verified</c> integration record naming the exact SHA.</summary>
    public const string IntegrationRecord = "integration-record";

    /// <summary>The gate this lane ran once on the current branch tip.</summary>
    public const string GateRun = "gate-run";

    /// <summary>The gate that guarded a fresh merge created by this lane.</summary>
    public const string MergeGate = "merge-gate";

    /// <summary>A fresh merge whose diff no gate applies to.</summary>
    public const string GateNotApplicable = "gate-not-applicable";

    /// <summary>Nothing proves the tree.</summary>
    public const string None = "none";
}

/// <summary>Facts for one contained delivery. Pure input; the runner gathers them.</summary>
/// <param name="Outcome">The merge outcome the runner reached before verification.</param>
/// <param name="Sha">Exact integration-branch SHA the card would claim.</param>
/// <param name="ExactReceipt">Newest gate receipt on the card whose tested tree is exactly that SHA's tree.</param>
/// <param name="HasVerifiedIntegrationRecord">An <c>integrated-verified</c> record on the card names that SHA.</param>
/// <param name="GateRun">The single gate run on the current tip, once it ran.</param>
public sealed record IntegrationVerificationFacts(
    MergeIntoIntegrationOutcome Outcome,
    string? Sha,
    BuildTestGateResult? ExactReceipt,
    bool HasVerifiedIntegrationRecord,
    BuildTestGateResult? GateRun);

public sealed record IntegrationVerificationDecision(
    IntegrationVerificationAction Action,
    string State,
    string Evidence,
    string Reason)
{
    /// <summary>True when a gate reached a product verdict on the tree and it failed.</summary>
    public bool GateFailed => Action == IntegrationVerificationAction.FailUnverified;
}

/// <summary>
/// AGT-3002 - pure rule for completing a card whose delivery the integration
/// branch already contains. Containment proves the delivery is on the branch;
/// it does not prove that anything verified the branch with it. An operator
/// script can push a tree whose only gate failed, and the lane then finds the
/// delivery "already merged". Such a card completes only on gate evidence for
/// the exact tree it would claim, and the lane produces that evidence at most
/// once per call by running the gate on the current tip.
/// </summary>
public static class IntegrationVerificationPolicy
{
    public static IntegrationVerificationDecision Decide(IntegrationVerificationFacts facts)
    {
        if (!facts.Outcome.IsAlreadyContained())
        {
            return new(
                IntegrationVerificationAction.NotApplicable,
                IntegrationVerificationStates.Verified,
                IntegrationVerificationEvidence.None,
                "The merge path gated its own result.");
        }

        if (!ReviewSubjectStore.IsValidResultSha(facts.Sha))
        {
            return Unverified(
                IntegrationVerificationAction.GateUnresolved,
                IntegrationVerificationEvidence.None,
                "The exact integration-branch SHA could not be resolved, so no gate evidence can be matched to it.");
        }

        var sha = Short(facts.Sha!);
        if (facts.ExactReceipt is { } receipt)
        {
            if (PreDevelopBuildGate.IsGreen(receipt))
            {
                return Verified(
                    IntegrationVerificationEvidence.GateReceipt,
                    $"A {receipt.Verdict} gate receipt exists for the exact merged tree {sha}.");
            }

            // A host, budget, or source failure is not a verdict about the
            // tree; only a product verdict settles it.
            if (IsTreeVerdict(receipt))
            {
                return Unverified(
                    IntegrationVerificationAction.FailUnverified,
                    IntegrationVerificationEvidence.GateReceipt,
                    $"The gate already ran on the exact merged tree {sha} and returned {receipt.Verdict}: {receipt.Reason}");
            }
        }

        if (facts.HasVerifiedIntegrationRecord)
        {
            return Verified(
                IntegrationVerificationEvidence.IntegrationRecord,
                $"An integrated-verified integration record names the exact merged tree {sha}.");
        }

        if (facts.GateRun is not { } gate)
        {
            return Unverified(
                IntegrationVerificationAction.RunGate,
                IntegrationVerificationEvidence.None,
                $"No gate receipt and no integrated-verified record exists for {sha}; "
                + "the delivery reached the branch without a gate this card can name. The gate runs once on the current branch tip.");
        }

        if (PreDevelopBuildGate.IsGreen(gate))
        {
            return Verified(
                IntegrationVerificationEvidence.GateRun,
                $"The gate ran once on the current branch tip {sha} and returned {gate.Verdict}.");
        }

        return IsTreeVerdict(gate)
            ? Unverified(
                IntegrationVerificationAction.FailUnverified,
                IntegrationVerificationEvidence.GateRun,
                $"The gate ran once on the current branch tip {sha} and returned {gate.Verdict}: {gate.Reason}")
            : Unverified(
                IntegrationVerificationAction.GateUnresolved,
                IntegrationVerificationEvidence.GateRun,
                $"The gate on the current branch tip {sha} stopped before it reached a verdict ({gate.FailureKind}): {gate.Reason}");
    }

    /// <summary>
    /// A failed gate says something about the tree only when it failed on
    /// the code itself; the same split the per-SHA gate cache uses.
    /// </summary>
    public static bool IsTreeVerdict(BuildTestGateResult gate)
        => gate.FailureKind is BuildTestGateFailureKind.None or BuildTestGateFailureKind.Code;

    private static IntegrationVerificationDecision Verified(string evidence, string reason)
        => new(
            IntegrationVerificationAction.CompleteVerified,
            IntegrationVerificationStates.Verified,
            evidence,
            reason);

    private static IntegrationVerificationDecision Unverified(
        IntegrationVerificationAction action,
        string evidence,
        string reason)
        => new(action, IntegrationVerificationStates.Unverified, evidence, reason);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
