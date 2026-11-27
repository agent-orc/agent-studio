using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationBlockerPolicyTests
{
    [Theory]
    [InlineData("agt-3973-implementation-not-integrated", "AGT-3973")]
    [InlineData("The remote agent reported a blocker: missing-AGT-4015-implementation", "AGT-4015")]
    [InlineData("The implementation of card AGT-4011 is not integrated into develop.", "AGT-4011")]
    [InlineData("AGT-4011 implementation is not integrated", "AGT-4011")]
    [InlineData("missing credentials for AGT-4011", null)]
    [InlineData("agt-3973-implementation-not-integrated missing-AGT-4015-implementation", null)]
    public void Recognizes_only_unambiguous_integration_prerequisites(string reason, string? expected)
        => Assert.Equal(expected, IntegrationBlockerPolicy.Dependency("agent-blocked", reason));

    [Fact]
    public void Does_not_reinterpret_an_operator_question()
        => Assert.Null(IntegrationBlockerPolicy.Dependency("needs-human-input",
            "Should we change scope while agt-3973-implementation-not-integrated?"));
}
