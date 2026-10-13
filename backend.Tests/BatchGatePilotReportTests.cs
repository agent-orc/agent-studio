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
            var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
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
    public void ReviewVerificationMinutesAddToTheGateExecutionMinutes()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-metrics-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
            var sha = new string('a', 40);
            store.RecordExecution(new BatchGateExecutionFact(
                "project", "batch", "gate-run", sha, "Ok", "host", now,
                now.AddMinutes(40), 0, 0, "evidence"));
            Assert.True(store.RecordReviewVerification(new BatchGateReviewVerificationFact(
                "project", "task-1", "review-1", sha, "Pass", 10, 0, 1, now)));
            Assert.True(store.RecordReviewVerification(new BatchGateReviewVerificationFact(
                "project", "task-2", "review-2", sha, "ProductFailure", 20, 5, 2, now)));
            // A replay of the same review attempt never adds a second fact.
            Assert.False(store.RecordReviewVerification(new BatchGateReviewVerificationFact(
                "project", "task-2", "review-2", sha, "ProductFailure", 20, 5, 2, now)));
            store.RecordReviewVerification(new BatchGateReviewVerificationFact(
                "other", "task-3", "review-3", sha, "Pass", 99, 99, 1, now));

            var report = BatchGatePilotSnapshotReader.Read(store, "project");

            Assert.Equal(1, report.FullSuiteRuns);
            Assert.Equal(40, report.FullSuiteHostMinutes, 6);
            Assert.Equal(2, report.ReviewVerificationRuns);
            Assert.Equal(35, report.ReviewVerificationHostMinutes, 6);
            Assert.Equal(5, report.ReviewVerificationBaselineMinutes, 6);
            Assert.Equal(75, report.TotalVerificationHostMinutes, 6);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WithoutReviewVerificationFactsTheGateFieldsAreUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-metrics-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            var now = DateTimeOffset.UnixEpoch.AddDays(20_000);
            store.RecordExecution(new BatchGateExecutionFact(
                "project", null, null, new string('a', 40), "Ok", "host", now,
                now.AddMinutes(40), 0, 0, "evidence"));

            var report = BatchGatePilotSnapshotReader.Read(store, "project");

            Assert.Equal(1, report.FullSuiteRuns);
            Assert.Equal(40, report.FullSuiteHostMinutes, 6);
            Assert.Equal(0, report.ReviewVerificationRuns);
            Assert.Equal(0, report.ReviewVerificationHostMinutes);
            Assert.Equal(0, report.ReviewVerificationBaselineMinutes);
            Assert.Equal(40, report.TotalVerificationHostMinutes, 6);
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
