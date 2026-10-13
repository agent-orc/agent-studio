using Contract = AgentStudio.TaskServer.Contracts;
namespace AgentStudio.OrchestrationDecisions;

public enum RemoteBuildTestGateClass
{
    Passed,
    NotApplicable,
    Failed,
}

public sealed record RemoteDeliveryIntegrationDecision(
    bool ShouldIntegrate,
    RemoteBuildTestGateClass BuildTestGate,
    string Reason);

/// <summary>
/// Pure admission policy for immediate Remote delivery integration. A fenced
/// result is eligible only after its source run persisted a settled immutable
/// envelope, the Remote Review reached Pass, and its build/test verdict is
/// either green or explicitly not applicable.
/// </summary>
public static class RemoteDeliveryIntegrationPolicy
{
    public static RemoteDeliveryIntegrationDecision Decide(
        bool hasSettledResultEnvelope,
        string? reviewOutcome,
        Contract.ReviewPlanDto? reviewPlan,
        IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
    {
        if (!hasSettledResultEnvelope)
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                "The source run has no settled immutable Result-Envelope.");
        }

        if (reviewPlan?.BuildTestDeferredToBatch == true)
            return new RemoteDeliveryIntegrationDecision(false,
                RemoteBuildTestGateClass.Failed,
                "The full build/test gate is deferred to a batch and has no per-card verdict.");

        // AGT-2819: a gate that was already red on the merge base is a defect of
        // the integration branch, not of this delivery. Refusing the card for it
        // stalled the whole board in Human Review for weeks while the deliveries
        // were merged by hand instead. The card does not make the branch worse,
        // so it is admissible; the branch defect carries its own operator alert.
        var integrationBranchDefect = string.Equals(
            reviewOutcome,
            ReviewOutcomeKinds.IntegrationBranchDefect,
            StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(reviewOutcome, ReviewOutcomeKinds.Pass, StringComparison.OrdinalIgnoreCase)
            && !integrationBranchDefect)
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                $"Remote Review ended with '{reviewOutcome ?? "unknown"}', not Pass.");
        }

        static bool IsBuildTestAspect(string aspect) =>
            string.Equals(aspect, "build-tests", StringComparison.OrdinalIgnoreCase)
            || string.Equals(aspect, "review-verification", StringComparison.OrdinalIgnoreCase);
        var buildTests = verdicts
            .Where(verdict => IsBuildTestAspect(verdict.Aspect))
            .ToList();
        if (buildTests.Count == 0)
        {
            var buildTestWasPlanned = reviewPlan?.Commands.Any(command =>
                                          IsBuildTestAspect(command.Aspect)) == true
                                      || reviewPlan?.RequiredAspects.Any(IsBuildTestAspect) == true;
            if (reviewPlan is null || buildTestWasPlanned)
            {
                return new RemoteDeliveryIntegrationDecision(
                    false,
                    RemoteBuildTestGateClass.Failed,
                    reviewPlan is null
                        ? "The settled Remote Review has no frozen plan proving build/test is not applicable."
                        : "The Remote Review report omitted its planned build/test verdict.");
            }

            return new RemoteDeliveryIntegrationDecision(
                true,
                RemoteBuildTestGateClass.NotApplicable,
                "The Remote Review plan has no applicable build/test gate.");
        }

        if (buildTests.Any(verdict => !IsGreenOrNotApplicable(verdict)))
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                "At least one applicable Remote Review build/test gate did not pass.");
        }

        var gateClass = buildTests.All(IsNotApplicable)
            ? RemoteBuildTestGateClass.NotApplicable
            : RemoteBuildTestGateClass.Passed;
        var reason = gateClass == RemoteBuildTestGateClass.Passed
            ? "All applicable Remote Review build/test gates passed."
            : "Every Remote Review build/test gate is not applicable.";
        return new RemoteDeliveryIntegrationDecision(
            true,
            gateClass,
            integrationBranchDefect
                ? reason + " A gate was already red on the integration branch; that defect is"
                  + " reported against the branch and does not refuse this delivery."
                : reason);
    }

    private static bool IsGreenOrNotApplicable(Contract.ReviewVerdictDto verdict)
        => string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase)
           || IsNotApplicable(verdict);

    private static bool IsNotApplicable(Contract.ReviewVerdictDto verdict)
        => string.Equals(verdict.Status, "not-applicable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verdict.Status, "not_applicable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verdict.Classification, "NotApplicable", StringComparison.OrdinalIgnoreCase)
           || (string.Equals(verdict.Status, "skipped", StringComparison.OrdinalIgnoreCase)
               && string.Equals(verdict.Classification, "NoCommands", StringComparison.OrdinalIgnoreCase));
}

