using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationPublicationGatePolicyTests
{
    private const string Verified = "1111111111111111111111111111111111111111";
    private const string Replay = "2222222222222222222222222222222222222222";
    private const string Batch = "3333333333333333333333333333333333333333";
    private const string Later = "4444444444444444444444444444444444444444";
    private const string Outside = "5555555555555555555555555555555555555555";

    [Fact]
    public void FirstParentAncestor_WithPassedBatchAndPerTaskPublications_IsAccepted()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later, Parent,
        [
            new(Batch, Verified, "batch gate"),
            new(Later, Batch, "per-task gate"),
        ]);

        Assert.True(decision.Verified);
    }

    [Fact]
    public void LaterPublicationWithoutGate_IsRefused()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later, Parent,
            [new(Batch, Verified, "batch gate")]);

        Assert.False(decision.Verified);
        Assert.Equal(Later, decision.MissingTree);
    }

    [Fact]
    public void VerificationTreeOutsideFirstParentBranch_IsRefused()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Outside, Later, Parent,
        [
            new(Batch, Verified, "batch gate"),
            new(Later, Batch, "per-task gate"),
        ]);

        Assert.False(decision.Verified);
        Assert.Equal(Verified, decision.MissingTree);
    }

    private static string? Parent(string sha) => sha switch
    {
        Later => Batch,
        Batch => Replay,
        Replay => Verified,
        _ => null,
    };
}
