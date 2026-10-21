using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewCommandFailureEvidenceTests
{
    [Fact]
    public void Tail_is_bounded_and_scrubs_credentials()
    {
        var secret = string.Concat("sk-", new string('x', 24));
        var output = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"line {i}"))
            + $"\nAuthorization: Bearer {secret}\nerror access_token={secret}";

        var tail = ReviewCommandFailureEvidence.Tail(new ProcessResult(1, output, string.Empty));

        Assert.DoesNotContain(secret, tail, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0", tail, StringComparison.Ordinal);
        Assert.True(tail.Length <= 1600);
    }
}
