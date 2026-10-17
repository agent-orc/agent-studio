using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewLegacyDecisionPolicyTests
{
    [Theory]
    [InlineData("[[ORCHESTRATOR_DECISION: action=reissue; reason=try again]]")]
    [InlineData("[[ORCHESTRATOR_DECISION: reason=park; action=ESCALATE]]")]
    [InlineData("[[ORCHESTRATOR_DECISION: action=accept_as_done; reason=settled]]")]
    [InlineData("[[ORCHESTRATOR_DECISION: action=accept; reason=settled]]")]
    [InlineData("[[ORCHESTRATOR_DECISION: action=reissue; reason=old]]\n[[ORCHESTRATOR_DECISION: action=escalate; reason=new]]")]
    [InlineData("[[ORCHESTRATOR_DECISION: action=reissue; reason=old]]\n[[ORCHESTRATOR_DECISION: action=unknown; reason=new]]")]
    [InlineData("[[ORCHESTRATOR_DECISION: action=escalate; action=reissue; reason=duplicate]]")]
    [InlineData("no sentinel")]
    public void Shared_review_parser_matches_the_legacy_verdict(string output)
        => Assert.Equal(ReviewDecisionParsing.ParseDecision(output),
            MonolithTransitionAdapter.ReviewText(output));
}
