using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PostMergeModeTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(null, IntegrationTestModes.PreMerge)]
    [InlineData("pre-merge", IntegrationTestModes.PreMerge)]
    [InlineData(" POST-MERGE ", IntegrationTestModes.PostMerge)]
    public void ModeDefaultsToPreMerge(string? stored, string expected)
        => Assert.Equal(expected, IntegrationTestModes.Resolve(new ProjectSettings
        {
            Testing = new ProjectTestingSettings { Mode = stored },
        }));

    [Theory]
    [InlineData(false, false, false, TestExecutionLevels.CompileOnly)]
    [InlineData(true, false, false, TestExecutionLevels.CompileOnly)]
    [InlineData(true, true, false, TestExecutionLevels.WorkPackage)]
    [InlineData(true, false, true, TestExecutionLevels.WorkPackage)]
    public void T2RerunsT1OnlyForSharedMovementOrUncertainMerge(
        bool moved, bool shared, bool replayed, string expected)
    {
        var review = new ReviewVerificationRecord
        {
            Outcome = "Pass", BuildTestGate = ReviewBuildTestGateClasses.Passed,
            IntegrationTipSha = Sha,
        };
        Assert.Equal(expected, PostMergeIntegrationPolicy.Level(review,
            moved ? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" : Sha,
            ["backend/Foo.cs"],
            moved ? [shared ? "backend/Foo.cs" : "frontend/Bar.ts"] : null,
            replayed));
    }

    [Fact]
    public void MissingReviewProofRunsAffectedTests()
        => Assert.Equal(TestExecutionLevels.WorkPackage,
            PostMergeIntegrationPolicy.Level(null, Sha, null, null, false));

    [Fact]
    public void EscalationFallsBackUntilGreen()
    {
        var clock = new FakeTimeProvider();
        var now = clock.GetUtcNow();
        var red = new PostMergeRunRecord("run", "PROJ-002", "window", Sha, null,
            null, null, 1, ["test"], [new("test", 1, false)], "red",
            now.AddHours(-3), now.AddHours(-2).AddMinutes(-1));
        var state = PostMergeProjectState.Empty("PROJ-002") with
        {
            DevelopState = "red", Runs = [red],
        };
        Assert.True(PostMergeModeService.EscalationActive(state, now));
        Assert.False(PostMergeModeService.EscalationActive(
            state with { DevelopState = "green" }, now));
        Assert.True(PostMergeModeService.EscalationActive(
            state with { DevelopState = "green" }, now, repairEscalated: true));
        Assert.False(PostMergeModeService.EscalationActive(
            state with { DevelopState = "green" }, now, repairEscalated: false));
    }
}
