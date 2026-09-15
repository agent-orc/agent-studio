using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2832: the pure placement and lifecycle policy of the Studio-owned
/// integration worktree. The matrix is asserted directly - preparation must
/// never have to guess what to do with a half-registered slot - and the path
/// derivation is asserted to be stable, because it is the only thing that lets
/// an existing project find the worktree a previous process created for it.
/// </summary>
public sealed class IntegrationWorktreePolicyTests
{
    [Theory]
    // A slot that is registered, present and linked is the normal steady state.
    [InlineData(false, true, false, true, true, IntegrationWorktreeAction.Reuse)]
    // Registered but the directory was removed (a temp sweeper, a manual delete).
    [InlineData(false, false, false, true, false, IntegrationWorktreeAction.Recreate)]
    // Present and registered but the .git link is gone: not a live worktree.
    [InlineData(false, true, false, true, false, IntegrationWorktreeAction.Recreate)]
    // Leftover content occupies the slot without any registration.
    [InlineData(false, true, false, false, false, IntegrationWorktreeAction.Recreate)]
    // Nothing there yet: the first integration of a project that predates AGT-2832.
    [InlineData(false, false, false, false, false, IntegrationWorktreeAction.Create)]
    // An empty directory is a usable target for git worktree add.
    [InlineData(false, true, true, false, false, IntegrationWorktreeAction.Create)]
    // The developer checkout is refused no matter how the facts line up.
    [InlineData(true, true, false, true, true, IntegrationWorktreeAction.Blocked)]
    [InlineData(true, false, false, false, false, IntegrationWorktreeAction.Blocked)]
    public void Decide_MapsObservedSlotToAction(
        bool isDeveloperCheckout,
        bool directoryExists,
        bool directoryIsEmpty,
        bool registered,
        bool hasGitAdminLink,
        IntegrationWorktreeAction expected)
    {
        var decision = IntegrationWorktreePolicy.Decide(new IntegrationWorktreeState(
            isDeveloperCheckout,
            directoryExists,
            directoryIsEmpty,
            registered,
            hasGitAdminLink));

        Assert.Equal(expected, decision.Action);
        if (expected != IntegrationWorktreeAction.Reuse && expected != IntegrationWorktreeAction.Create)
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void CandidatePaths_PrefersASiblingContainerAndFallsBackToTemp()
    {
        var repo = Path.Combine(Path.GetTempPath(), "projects", "quality-studio");

        var candidates = IntegrationWorktreePolicy.CandidatePaths(repo, Path.Combine(Path.GetTempPath(), "fallback"));

        Assert.Equal(2, candidates.Count);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "projects", IntegrationWorktreePolicy.ContainerName),
            Path.GetDirectoryName(candidates[0]));
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "fallback"), candidates[1]);
        Assert.All(candidates, candidate => Assert.False(
            candidate.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the integration worktree must never live inside the project checkout"));
    }

    [Fact]
    public void CandidatePaths_PreferTempWhenTheParentLiesInsideAnotherRepository()
    {
        var repo = Path.Combine(Path.GetTempPath(), "outer", "inner");

        var candidates = IntegrationWorktreePolicy.CandidatePaths(
            repo, Path.Combine(Path.GetTempPath(), "fallback"), containerParentIsInsideRepository: true);

        Assert.Equal(2, candidates.Count);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "fallback"), candidates[0]);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "outer", IntegrationWorktreePolicy.ContainerName),
            Path.GetDirectoryName(candidates[1]));
    }

    [Fact]
    public void CandidatePaths_AreStableForTheSameRepository()
    {
        var repo = Path.Combine(Path.GetTempPath(), "projects", "quality-studio");

        Assert.Equal(
            IntegrationWorktreePolicy.CandidatePaths(repo),
            IntegrationWorktreePolicy.CandidatePaths(repo + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void SlotName_KeepsTheRepositoryNameAndSeparatesEqualNames()
    {
        var first = IntegrationWorktreePolicy.SlotName(
            Path.Combine(Path.GetTempPath(), "a", "quality-studio"));
        var second = IntegrationWorktreePolicy.SlotName(
            Path.Combine(Path.GetTempPath(), "b", "quality-studio"));

        Assert.StartsWith("quality-studio-", first);
        Assert.StartsWith("quality-studio-", second);
        Assert.NotEqual(first, second);
    }
}
