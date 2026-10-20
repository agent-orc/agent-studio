using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class FencedDeliveryRefusalPolicyTests
{
    [Theory]
    [InlineData(true, TransitionAction.ReturnToReady)]
    [InlineData(false, TransitionAction.Escalate)]
    public void Only_a_verified_result_with_an_unrelated_base_is_requeued(
        bool baseNotAncestor, TransitionAction expected)
        => Assert.Equal(expected, FencedDeliveryRefusalPolicy.Decide(baseNotAncestor));
}
