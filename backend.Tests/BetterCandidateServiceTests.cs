using Xunit;

namespace AgentStudio.Tests;

public sealed class BetterCandidateServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Attach_ProjectsMatrixCandidateOnlyOntoReadyTask()
    {
        var service = new BetterCandidateService(
            TokenEconomy.ModelBenchmarkMatrix.Default,
            TokenEconomy.BenchmarkEvidenceCatalog.Default,
            new FixedTimeProvider(Now));
        var ready = new TaskInfo
        {
            Id = "AGT-2770",
            State = TaskStates.Ready,
            Model = "gpt-5.6-sol",
            ThinkingLevel = "max",
        };
        var settings = new ProjectSettings { BenchmarkCapabilityClass = "CodingAgent" };

        var projected = service.Attach(ready, settings);
        var candidate = Assert.Single(projected.BetterCandidates!.Candidates);
        Assert.Equal("gpt-6-astra", candidate.Model);
        Assert.Equal("deepswe-v1.1", candidate.BenchmarkType);
        Assert.True(candidate.ScoreDelta > 0);
        Assert.True(candidate.CostDeltaUsd < 0);
        Assert.True(candidate.EvidenceAgeDays >= 0);
        Assert.Equal(BetterCandidateService.MatrixUrl, projected.BetterCandidates.MatrixUrl);
        Assert.Equal("gpt-5.6-sol", projected.Model);
        Assert.Equal("max", projected.ThinkingLevel);

        Assert.Null(service.Attach(ready with { State = TaskStates.Progress }, settings).BetterCandidates);
    }

    [Fact]
    public void FindRoute_CachesPerRouteBenchmarkAndEvidenceSnapshot()
    {
        var service = new BetterCandidateService(
            TokenEconomy.ModelBenchmarkMatrix.Default,
            TokenEconomy.BenchmarkEvidenceCatalog.Default,
            new FixedTimeProvider(Now));

        var first = service.FindRoute("gpt-5.6-sol", "max", "CodingAgent");
        var countAfterFirst = service.CachedQueryCount;
        var second = service.FindRoute("gpt-5.6-sol", "max", "CodingAgent");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(countAfterFirst > 0);
        Assert.Equal(countAfterFirst, service.CachedQueryCount);
        Assert.Equal(first.EvidenceSnapshot, second.EvidenceSnapshot);
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
