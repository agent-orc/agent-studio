using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewReportPayloadPolicyTests
{
    [Fact]
    public void Inline_review_evidence_stays_within_the_body_budget_and_keeps_hashes()
    {
        var content = Convert.ToBase64String(new byte[1024]);
        var artifacts = new[]
        {
            new ReviewArtifactEvidenceDto("first", "text/plain", new string('a', 64), 1024, content),
            new ReviewArtifactEvidenceDto("second", "text/plain", new string('b', 64), 1024, content),
        };

        var bounded = ReviewReportPayloadPolicy.Bound(artifacts, maxWireBytes: 1500);

        Assert.Equal(1, bounded.Omitted);
        Assert.Equal(content, bounded.Artifacts[0].ContentBase64);
        Assert.Null(bounded.Artifacts[1].ContentBase64);
        Assert.Equal(artifacts[1].Sha256, bounded.Artifacts[1].Sha256);

        var prioritizeSecond = ReviewReportPayloadPolicy.Bound(artifacts, 1500,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { artifacts[1].Sha256 });
        Assert.Null(prioritizeSecond.Artifacts[0].ContentBase64);
        Assert.Equal(content, prioritizeSecond.Artifacts[1].ContentBase64);
    }
}
