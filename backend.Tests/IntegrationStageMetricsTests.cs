using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationStageMetricsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Records_each_stage_and_second_cycle_without_turning_missing_legacy_data_into_zero()
    {
        IntegrationLaneEvent Row(string kind, int seconds, IReadOnlyDictionary<string, long>? timings = null)
            => new("project", "card", 1, Start.AddSeconds(seconds), kind, StageDurationsMs: timings);
        var records = IntegrationStageMetrics.Recent([
            Row("enqueued", 0) with { ReviewPassedAtUtc = Start }, Row("dequeued", 10), Row("gate-requested", 11),
            Row("gate-finished", 30, new Dictionary<string, long> { ["build"] = 15000 }),
            Row("integration-cycle", 31) with { Reason = "origin advanced" }, Row("gate-requested", 32),
            Row("gate-finished", 40, new Dictionary<string, long> { ["build"] = 5000 }),
            Row("push-finished", 42, new Dictionary<string, long> { ["push"] = 2000 }), Row("published", 42),
        ]);
        var record = Assert.Single(records);
        Assert.Equal(10000, record.StagesMs["queueWait"]);
        Assert.Equal(20000, record.StagesMs["build"]);
        Assert.Equal(42000, record.StagesMs["reviewToPushed"]);
        Assert.Null(record.StagesMs["restore"]);
        Assert.Equal(2, record.GateCycles);
        Assert.Equal(["origin advanced"], record.SecondCycleReasons);
    }

    [Fact]
    public void Last_fifty_are_ordered_and_percentiles_use_only_measured_attempts()
    {
        var events = Enumerable.Range(1, 60).Select(index => new IntegrationLaneEvent("p", "j" + index,
            index, Start.AddSeconds(index), "published", StageDurationsMs:
                new Dictionary<string, long> { ["push"] = index }));
        var records = IntegrationStageMetrics.Recent(events);
        Assert.Equal(50, records.Count);
        Assert.Equal("j60", records[0].JobId);
        var summary = IntegrationStageMetrics.Summarize(records);
        var push = summary.Single(row => row.Stage == "push");
        Assert.Equal(35.5, push.MedianMs);
        Assert.Equal(55.1, push.P90Ms!.Value, 4);
        Assert.Equal(50, push.Samples);
        Assert.Equal(new IntegrationStageSummary("restore", null, null, 0), summary.Single(row => row.Stage == "restore"));
    }

    [Fact]
    public void Queue_wait_includes_held_reentries_and_incomplete_transport_attempts_stay_unknown()
    {
        IntegrationLaneEvent Row(string kind, int seconds) => new("p", "j", 1, Start.AddSeconds(seconds), kind);
        var record = Assert.Single(IntegrationStageMetrics.Recent([
            Row("enqueued", 0), Row("dequeued", 10), Row("held", 12), Row("dequeued", 20),
            Row("gate-requested", 21), Row("gate-finished", 25) with
            {
                GateAttempts = 2, GateRetryReasons = ["transport response required a retry"], StageTimingsIncomplete = true,
                StageDurationsMs = new Dictionary<string, long> { ["build"] = 1000 },
            }, Row("published", 30),
        ]));
        Assert.Equal(18000, record.StagesMs["queueWait"]);
        Assert.Equal(2, record.GateCycles);
        Assert.True(record.HasIncompleteGateTimings);
        Assert.Null(record.StagesMs["build"]);
        Assert.Equal("transport response required a retry", Assert.Single(record.SecondCycleReasons));
    }

    [Fact]
    public void Measured_processes_classify_restore_and_each_build_without_double_counting()
    {
        BuildTestGateProcessEvidence Process(string command, string phase, int seconds) => new()
        { Command = command, Phase = phase, StartedAtUtc = Start, CompletedAtUtc = Start.AddSeconds(seconds) };
        var result = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 1, "", "", true, true)
        { GateQueueWaitMs = 7, Processes = [Process("dotnet restore", "preparation", 2),
            Process("dotnet build", "verification", 3), Process("bash scripts/check-release-build.sh", "verification", 4),
            Process("bash scripts/npm-frontend.sh run build", "verification", 5)] };
        var timings = GateStageTimings.Measure(result, 11);
        Assert.Equal(2000, timings["restore"]);
        Assert.Equal(3000, timings["build"]);
        Assert.Equal(4000, timings["releaseBuildCheck"]);
        Assert.Equal(5000, timings["frontendBuild"]);
        Assert.Equal(11, timings["sourceCheckout"]);
        Assert.Equal(7, timings["gateQueueWait"]);
        var custom = Process("make compile", "verification", 6) with { CommandKind = VerifyCommandKind.Build };
        Assert.Equal(6000, GateStageTimings.Measure(result with { Processes = [custom] }, null)["build"]);
        Assert.All(GateStageTimings.Measure(result with { VerdictSource = GateVerdictSource.CacheHit }, 0)
            .Where(row => GateStageTimings.BuildStages.Contains(row.Key)), row => Assert.Equal(0, row.Value));
    }
}
