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
    [InlineData(null, IntegrationTestModes.PostMerge)]
    [InlineData("fast", IntegrationTestModes.PostMerge)]
    [InlineData(" BALANCED ", IntegrationTestModes.PostMerge)]
    [InlineData("strict", IntegrationTestModes.PreMerge)]
    [InlineData("pre-merge", IntegrationTestModes.PostMerge)]
    public void ModeIsDerivedFromTheStrategyAndDefaultsToPostMerge(string? stored, string expected)
        => Assert.Equal(expected, IntegrationTestModes.Resolve(new ProjectSettings
        {
            Testing = new ProjectTestingSettings { Strategy = stored },
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

    [Theory]
    [InlineData(TestingStrategies.Fast, IntegrationTestModes.PostMerge)]
    [InlineData(TestingStrategies.Balanced, IntegrationTestModes.PostMerge)]
    [InlineData(TestingStrategies.Strict, IntegrationTestModes.PreMerge)]
    public void A_red_develop_never_changes_the_configured_placement(string strategy, string expected)
    {
        // RT-1 (AGT-3988): three hours of red and an escalated repair card
        // both used to switch the project to pre-merge and turn the batch
        // gate back on. The placement is now the configured one.
        var mode = new PostMergeModeService();
        var settings = new ProjectSettings { Testing = new ProjectTestingSettings { Strategy = strategy } };

        Assert.Equal(expected, mode.Resolve("PROJ-002", settings));
        Assert.Equal(expected, PostMergeModeService.Effective(mode, "PROJ-002", settings));
        Assert.Equal(expected, mode.Resolve("unknown-project", settings));
    }
}
