using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class PostMergeMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void RedDurationEndsAtTheNextGreenAndNightlyCountsOnlyExecutedRuns()
    {
        var red = Run("red", "window", Now.AddMinutes(-50));
        var green = Run("green", "nightly", Now.AddMinutes(-20));
        var skipped = Run("skipped", "nightly", Now.AddMinutes(-10));
        var state = new PostMergeProjectState("PROJ-002", "green", Sha, Sha, null,
            [red, green, skipped]);
        var result = PostMergeMetrics.Decorate(PipelineBaseline.Unknown, [state], Now.AddDays(-7), Now);
        Assert.Equal(30, result.RedDevelopMedianMinutes);
        Assert.Equal(1, result.FullSuiteRuns);
    }

    private static PostMergeRunRecord Run(string status, string kind, DateTimeOffset at)
        => new(Guid.NewGuid().ToString("N"), "Agent Studio", kind, Sha,
            null, null, null, 1, ["test"], status == "skipped" ? [] : [new("test", 0, false)],
            status, at.AddMinutes(-1), at);
}
