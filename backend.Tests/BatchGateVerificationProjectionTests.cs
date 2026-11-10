using System.Security.Cryptography;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateVerificationProjectionTests
{
    private static string Sha() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private static BatchGateBaseVerdict Verdict(string tree, DateTimeOffset at) => new(
        "fixture", tree, "profile", "pass", null,
        "batch fixture verified and published this tip", "fixture-gate.log", at,
        BatchGateBaseCheckSources.PublishedBatch, 0, tree);

    [Theory]
    [InlineData("red")]
    [InlineData("provisional")]
    [InlineData("rewritten")]
    [InlineData("missing-delivery")]
    [InlineData("newer-delivery")]
    public void PublishedBatchVerdict_RefusesUnprovenDelivery(string shape)
    {
        var tree = Sha();
        var verdict = Verdict(tree, DateTimeOffset.UnixEpoch.AddDays(1)) with
        {
            Outcome = shape == "red" ? "fail" : "pass",
            Provisional = shape == "provisional",
        };
        var carries = shape != "rewritten";
        var contains = shape is not ("missing-delivery" or "newer-delivery");

        var found = IntegrationVerificationProjection.FindPublishedBatchBaseVerdict(
            [verdict], _ => carries, _ => contains);

        Assert.Null(found);
    }

    [Fact]
    public void PublishedBatchVerdict_SelectsNewestQualifyingTree()
    {
        var older = Verdict(Sha(), DateTimeOffset.UnixEpoch.AddDays(1));
        var newer = Verdict(Sha(), DateTimeOffset.UnixEpoch.AddDays(2));

        var found = IntegrationVerificationProjection.FindPublishedBatchBaseVerdict(
            [older, newer], _ => true, _ => true);

        Assert.Equal(newer, found);
        var projected = IntegrationVerificationProjection.FromPublishedBatchBaseVerdict(
            found!, "develop", newer.BaseSha);
        Assert.Equal(IntegrationVerificationStates.Verified, projected.State);
        Assert.Contains(newer.BaseSha, projected.Reason);
        Assert.Contains("batch fixture", projected.Reason);
    }
}
