using Xunit;

namespace AgentStudio.Tests;

public sealed class StaleVerdictRecoveryPolicyTests
{
    [Theory]
    [InlineData(true, DeliveryChainAction.ReturnToAutoReview)]
    [InlineData(false, DeliveryChainAction.Leave)]
    public void Integrated_delivery_replaces_only_a_superseded_verdict(bool stale, DeliveryChainAction expected)
    {
        var facts = new DeliveryChainOperatorFacts(TaskStates.HumanReview, false, true,
            false, false, false, ReviewVerdictStale: stale);
        Assert.Equal(expected, DeliveryChainPolicy.Decide(facts).Action);
    }
    [Theory]
    [InlineData(true, false, false, false, false, StaleVerdictRecoveryAction.Review)]
    [InlineData(true, false, false, true, true, StaleVerdictRecoveryAction.Review)]
    [InlineData(false, true, false, false, false, StaleVerdictRecoveryAction.Review)]
    [InlineData(false, false, true, false, false, StaleVerdictRecoveryAction.MergeRound)]
    [InlineData(false, false, false, true, false, StaleVerdictRecoveryAction.RouteGateFailure)]
    [InlineData(false, false, false, false, true, StaleVerdictRecoveryAction.WaitCapacity)]
    [InlineData(false, false, false, false, false, StaleVerdictRecoveryAction.Escalate)]
    public void Every_integration_result_has_an_owner(bool verified, bool success, bool conflict,
        bool gate, bool capacity, StaleVerdictRecoveryAction expected)
        => Assert.Equal(expected, StaleVerdictRecoveryPolicy.AfterIntegration(verified, success, conflict, gate, capacity));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void Recall_requires_a_machine_stale_park_and_conclusive_verification(bool system,
        bool stale, bool verified, bool expected)
        => Assert.Equal(expected, StaleVerdictRecoveryPolicy.Recall(system, stale, verified));

    [Fact]
    public void Escalation_names_delivery_verdict_failure_and_operator_action()
    {
        var receipt = new StaleVerdictRecovery("run:result", "review:Pass:old-result", "integrating");
        var reason = receipt.Failure("delivery branch missing");
        Assert.Contains("run:result", reason);
        Assert.Contains("review:Pass:old-result", reason);
        Assert.Contains("delivery branch missing", reason);
        Assert.Contains("Continue", reason);
    }
    [Theory]
    [InlineData(true, false, 0, GateEnvironmentRetryAction.Retry)]
    [InlineData(true, false, 1, GateEnvironmentRetryAction.Park)]
    [InlineData(false, false, 0, GateEnvironmentRetryAction.Ignore)]
    [InlineData(true, true, 0, GateEnvironmentRetryAction.Ignore)]
    public void Pre_review_recovery_keeps_the_environment_budget_and_inflight_guards(bool enabled,
        bool inFlight, int spent, GateEnvironmentRetryAction expected)
    {
        var now = DateTimeOffset.UnixEpoch;
        var facts = new GateEnvironmentRetryFacts(enabled, true, true, inFlight, true, false,
            spent, [TimeSpan.Zero], null, now, now, IntegrationBeforeFreshReview: true);
        Assert.Equal(expected, GateEnvironmentRetryPolicy.Decide(facts).Action);
        Assert.False(facts.ReviewPassed);
    }
    [Theory]
    [InlineData(true, false, false, false, false, AiOperatorEffects.RequestFreshDeliveryReview)]
    [InlineData(false, true, false, false, false, AiOperatorEffects.RequestFreshDeliveryReview)]
    [InlineData(false, false, true, false, false, AiOperatorEffects.QueueMergeRound)]
    [InlineData(false, false, false, true, false, AiOperatorEffects.RouteDeliveryGateFailure)]
    [InlineData(false, false, false, false, true, AiOperatorEffects.WaitDeliveryCapacity)]
    [InlineData(false, false, false, false, false, AiOperatorEffects.Escalate)]
    public void Recovery_effects_use_the_delivery_chain_gateway(bool verified, bool success,
        bool conflict, bool gate, bool capacity, string expected)
    {
        var facts = new DeliveryChainOperatorFacts(TaskStates.AutoReview, true, verified,
            false, false, false, Recovery: new(verified, success, conflict, gate, capacity));
        var payload = new AiOperatorDecisionPayload(AiOperatorDeciders.DeliveryChain,
            "recovery-subject", DeliveryChain: facts);
        Assert.Equal(expected, AiOperatorDecisionPolicy.Decide(payload).Effect);
    }
}
