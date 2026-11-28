using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationPublicationGatePolicyTests
{
    [Theory]
    [InlineData("green", 0)]
    [InlineData("decision", 1)]
    [InlineData("key", 2)]
    public void ConsecutiveMetadataChangesInheritTheExactReceipt(string delivery, int length)
    {
        var inherited = IntegrationPublicationGatePolicy.InheritedReceipt(delivery, "green",
            sha => sha switch { "key" => "decision", "decision" => "green", _ => null });
        Assert.Equal(length, inherited?.Count);
    }

    [Fact]
    public void ACodeChangeBetweenTheReceiptAndTheDecisionStopsInheritance()
    {
        Assert.Null(IntegrationPublicationGatePolicy.InheritedReceipt("decision", "green",
            sha => sha == "decision" ? "code" : null));
    }
}
