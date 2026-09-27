using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGatePilotReportTests
{
    [Fact]
    public void CorrectnessViolationStopsPilotEvenWhenThroughputTargetsPass()
    {
        var waves = Enumerable.Range(0, 5).Select(i => new BatchGatePilotWave(
            $"batch-{i}", 4, 1, 4, 0, true,
            1, 3, 2, 6, [10, 12, 14, 15], 1m,
            UntestedPublishShas: i == 0 ? 1 : 0)).ToArray();
        var report = BatchGatePilotReport.Calculate(waves);
        Assert.Equal(.25, report.FullSuiteRunsPerEligibleMember);
        Assert.Equal(4, report.MeanBatchSize);
        Assert.Equal(1, report.UntestedPublishShas);
        Assert.False(report.CorrectnessFloorMet);
        Assert.False(report.MeetsPilotTargets);
    }
}
