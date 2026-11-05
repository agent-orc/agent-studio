using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchStackingPolicyTests
{
    private static readonly string Base = new('a', 40);
    private static readonly string Candidate = new('b', 40);
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "profile", "digest", "v1");
    private static readonly BatchGateManifest Predecessor = new("first", Scope, Base, "digest",
        At, [], [], []);
    private static readonly BatchGateManifest Successor = new("second", Scope, Candidate, "digest",
        At, [], [], [], StackedOnBatchId: "first");

    [Theory]
    [InlineData(BatchPhase.Formed, false)]
    [InlineData(BatchPhase.Assembling, false)]
    [InlineData(BatchPhase.Running, true)]
    [InlineData(BatchPhase.Green, true)]
    [InlineData(BatchPhase.Publishing, true)]
    [InlineData(BatchPhase.Red, false)]
    [InlineData(BatchPhase.Paused, false)]
    [InlineData(BatchPhase.Abandoned, false)]
    public void Only_a_live_predecessor_can_keep_a_speculative_gate(BatchPhase phase, bool expected)
    {
        var state = new BatchGateState("first", "digest", phase, Candidate, 1, At);
        Assert.Equal(expected, BatchStackingPolicy.CanLead(Predecessor, state, null, Successor));
        Assert.False(BatchStackingPolicy.CanPublish(Predecessor, state, null, Successor));
    }

    [Fact]
    public void Publication_requires_the_exact_predecessor_receipt_and_scope()
    {
        var state = new BatchGateState("first", "digest", BatchPhase.Published,
            Candidate, 1, At);
        var publication = new BatchGatePublication("first", "digest", "run", Base,
            Candidate, Candidate, 1, 1, At);
        Assert.True(BatchStackingPolicy.CanPublish(Predecessor, state, publication, Successor));
        Assert.False(BatchStackingPolicy.CanPublish(Predecessor, state,
            publication with { VerifiedRemoteSha = Base }, Successor));
        Assert.False(BatchStackingPolicy.CanPublish(Predecessor, state, publication,
            Successor with { Scope = Scope with { IntegrationBranch = "other" } }));
        Assert.False(BatchStackingPolicy.CanPublish(Predecessor, state, publication,
            Successor with { StackedOnBatchId = "other" }));
    }
}
