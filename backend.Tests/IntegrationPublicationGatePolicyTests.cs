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
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later, FirstParentLine,
        [
            new(Batch, Verified, "batch gate"),
            new(Later, Batch, "per-task gate"),
        ]);

        Assert.True(decision.Verified);
    }

    [Fact]
    public void LaterPublicationWithoutGate_IsRefused()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later, FirstParentLine,
            [new(Batch, Verified, "batch gate")]);

        Assert.False(decision.Verified);
        Assert.Equal(Later, decision.MissingTree);
    }

    [Fact]
    public void LaterFullSuiteGateCoversEarlierHoleOnItsFirstParentLine()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later, FirstParentLine,
            [new(Later, Batch, "full gate", CoversPriorPublications: true)]);

        Assert.True(decision.Verified);
        Assert.Equal(Later, decision.CoveringPublication?.TreeSha);
    }

    [Fact]
    public void PartialLaterGateDoesNotCoverEarlierHole()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later, FirstParentLine,
            [new(Later, Batch, "partial gate")]);

        Assert.False(decision.Verified);
        Assert.Equal(Batch, decision.MissingTree);
    }

    [Fact]
    public void FullSuiteGateOnRewrittenLineDoesNotCoverHole()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Outside, Later, FirstParentLine,
            [new(Later, Batch, "full gate", CoversPriorPublications: true)]);

        Assert.False(decision.Verified);
        Assert.True(decision.MissingAncestry);
    }

    [Fact]
    public void GateNeutralKeyCommitAfterBatchGate_DoesNotRequireAnotherGate()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later,
            [new(Later, Batch), new(Batch, Verified)],
            [new(Batch, Verified, "batch gate")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Later });

        Assert.True(decision.Verified);
    }

    [Fact]
    public void GateNeutralKeyCommitDoesNotCoverAnEarlierUngatedCommit()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Verified, Later,
            [new(Later, Batch), new(Batch, Verified)], [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Later });

        Assert.False(decision.Verified);
        Assert.Equal(Batch, decision.MissingTree);
    }

    [Fact]
    public void VerificationTreeOutsideFirstParentBranch_IsRefused()
    {
        var decision = IntegrationPublicationGatePolicy.Check(Outside, Later, FirstParentLine,
        [
            new(Batch, Outside, "batch gate"),
            new(Later, Batch, "per-task gate"),
        ]);

        Assert.False(decision.Verified);
        Assert.True(decision.MissingAncestry);
        Assert.Equal(Replay, decision.MissingTree);
    }

    private static readonly IntegrationPublicationGatePolicy.FirstParentCommit[] FirstParentLine =
    [
        new(Later, Batch),
        new(Batch, Replay),
        new(Replay, Verified),
    ];
}
