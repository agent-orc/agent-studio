using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGatePilotReportTests
{
    [Fact]
    public void LiveSnapshotReportsObservedCountsAndLeavesMissingBaselinesNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-metrics-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            var now = DateTimeOffset.UtcNow;
            var sha = new string('a', 40);
            var scope = new BatchGateScope("project", "repo", "develop", "full", "digest", "v1");
            var members = Enumerable.Range(0, 4).Select(index => new BatchGateSubject(
                $"task-{index}", "project", "repo", "develop", "full", "digest", "v1",
                $"refs/heads/agent-studio/results/task-{index}", sha, $"run-{index}",
                1, 1, sha, true, true, true, true, false, null, true,
                now.AddMinutes(-20 + index), index + 1)).ToArray();
            var manifest = BatchGatePolicy.Form(members, scope, sha,
                new BatchGateFormationOptions(Enabled: true), now).Manifest!;
            store.CloseManifest(manifest);
            store.RecordExecution(new BatchGateExecutionFact(
                "project", manifest.BatchId, "gate-run", sha, "Ok", "host", now,
                now.AddMinutes(10), 0, 1, "evidence"));
            var report = BatchGatePilotSnapshotReader.Read(store, "project");
            Assert.Equal(4, report.EligibleDeliveredMembers);
            Assert.Equal(.25, report.FullSuiteRunsPerEligibleMember);
            Assert.Equal(4, report.MeanBatchSize);
            Assert.Null(report.HostOverloadReduction);
            Assert.Null(report.GateCostUsd);
            Assert.True(report.CorrectnessFloorMet);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorrectnessViolationFailsTheFloorWhateverTheThroughput()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-metrics-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            var report = BatchGatePilotSnapshotReader.Read(store, "project",
                falseCompletedCards: 1);
            Assert.Equal(1, report.FalseCompletedCards);
            Assert.False(report.CorrectnessFloorMet);
            report = BatchGatePilotSnapshotReader.Read(store, "project",
                staleReleasedCards: 1);
            Assert.Equal(1, report.StaleAttemptPasses);
            Assert.False(report.CorrectnessFloorMet);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
