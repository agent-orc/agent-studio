using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3858: a red cohort that no isolation child reproduces is gated once more as a cohort.</summary>
public sealed class BatchGatePilotFlakySuspectTests : BatchGatePilotServiceTests
{
    private const string UnstableTest = "Product.Tests.Unstable_timing";

    [Fact]
    public async Task A_red_no_child_reproduces_is_regated_once_as_a_cohort_without_per_card_suites()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var a = SeedMember(factory, "CODE-FLAKY-A", "src/flaky-a.cs");
        var b = SeedMember(factory, "CODE-FLAKY-B", "src/flaky-b.cs");
        var first = true;
        _gate.Batch = request =>
        {
            var red = first;
            first = false;
            return red ? ReportRed(request.ExpectedSha) : Green(request.ExpectedSha);
        };
        _gate.Base = request => Green(request.ExpectedSha);
        _gate.Fallback = _ => throw new InvalidOperationException("A flaky suspect member ran a per-card suite.");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(a.Key, TaskStates.HumanReview);
        AssertLane(b.Key, TaskStates.HumanReview);
        Assert.True(TreeHas(RemoteTip(), "src/flaky-a.cs"));
        Assert.True(TreeHas(RemoteTip(), "src/flaky-b.cs"));
        var parent = store.ListManifests().Single(manifest => manifest.ParentBatchId is null);
        var suspect = store.ReadFlakySuspect(ProjectName, parent.BatchId);
        Assert.NotNull(suspect);
        Assert.True(suspect.Active);
        Assert.Equal([UnstableTest], suspect.Mark.FailedTests);
        Assert.Equal("passed", suspect.Regate!.Outcome);
        Assert.Equal([b.Key], suspect.Regate.MemberKeys);
        // One re-gate on the cohort's code: the deduced member, on the tip that already holds A.
        var regate = Assert.Single(store.ListManifests(), manifest => manifest.SameCodeRerunOfRunId is not null);
        Assert.Equal(suspect.Mark.RedRunId, regate.SameCodeRerunOfRunId);
        Assert.Equal(regate.BatchId, suspect.Regate.RegateBatchId);
        Assert.Equal(BatchPhase.Published, store.LatestState(regate.BatchId).Phase);
        Assert.Contains(_gate.Requests, request => request.LedgerTriggerEvent
            == BatchGatePilotService.FlakySuspectRegateCause + parent.BatchId);

        using var state = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{ProjectName}/batch-gate/state"));
        var row = state.RootElement.GetProperty("batches").EnumerateArray()
            .Single(batch => batch.GetProperty("batchId").GetString() == parent.BatchId);
        var mark = row.GetProperty("flakySuspect");
        Assert.True(mark.GetProperty("active").GetBoolean());
        Assert.Equal(UnstableTest, Assert.Single(mark.GetProperty("failedTests").EnumerateArray()).GetString());
        Assert.Equal("passed", mark.GetProperty("regate").GetProperty("outcome").GetString());
        using var ranking = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{ProjectName}/batch-gate/failed-tests"));
        var test = Assert.Single(ranking.RootElement.GetProperty("tests").EnumerateArray());
        Assert.Equal(UnstableTest, test.GetProperty("testName").GetString());
        Assert.True(test.GetProperty("flakySuspect").GetBoolean());
        Assert.Equal(1, test.GetProperty("flakySuspectRuns").GetInt32());
    }

    [Fact]
    public async Task A_second_red_on_the_same_code_takes_the_per_card_path()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var a = SeedMember(factory, "CODE-REPRO-A", "src/repro-a.cs");
        var b = SeedMember(factory, "CODE-REPRO-B", "src/repro-b.cs");
        _gate.Batch = request => TreeHas(request.ExpectedSha!, "src/repro-b.cs")
            ? ReportRed(request.ExpectedSha) : Green(request.ExpectedSha);
        _gate.Base = request => Green(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        var parent = store.ListManifests().Single(manifest => manifest.ParentBatchId is null);
        var suspect = store.ReadFlakySuspect(ProjectName, parent.BatchId);
        Assert.NotNull(suspect);
        Assert.Equal("reproduced", suspect.Regate!.Outcome);
        Assert.False(suspect.Active);
        Assert.Single(store.ListManifests(), manifest => manifest.SameCodeRerunOfRunId is not null);
        // Today's path: the red re-gate proves nothing itself; the per-card suite decides.
        Assert.DoesNotContain(store.ListIsolationFindings(ProjectName), finding =>
            finding.TaskKey == b.Key && finding.Status == "proven-red");
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(a.Key, TaskStates.HumanReview);
        AssertLane(b.Key, TaskStates.Escalated);
        Assert.False(TreeHas(RemoteTip(), "src/repro-b.cs"));
        Assert.False(Assert.Single(store.FailedTestRanking(ProjectName, _clock).Tests
            .Where(test => test.TestName == UnstableTest)).FlakySuspect);
    }

    [Fact]
    public async Task A_red_child_names_the_culprit_and_marks_no_flaky_suspect()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4, scope: "code");
        var members = new[] { "a", "b", "c", "d" }
            .Select(name => SeedMember(factory, $"CODE-CULPRIT-{name.ToUpperInvariant()}", $"src/culprit-{name}.cs"))
            .ToArray();
        _gate.Batch = request => TreeHas(request.ExpectedSha!, "src/culprit-a.cs")
            ? ReportRed(request.ExpectedSha) : Green(request.ExpectedSha);
        _gate.Base = request => Green(request.ExpectedSha);
        _gate.Fallback = _ => throw new InvalidOperationException("A proven culprit ran a per-card suite.");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        var parent = store.ListManifests().Single(manifest => manifest.ParentBatchId is null);
        Assert.Null(store.ReadFlakySuspect(ProjectName, parent.BatchId));
        Assert.DoesNotContain(store.ListManifests(), manifest => manifest.SameCodeRerunOfRunId is not null);
        AssertLane(members[0].Key, TaskStates.Escalated);
    }

    [Fact]
    public async Task A_regate_the_gate_host_holds_is_recorded_requeued_and_returns_the_cohort_to_the_pool()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var a = SeedMember(factory, "CODE-HELD-A", "src/held-a.cs");
        var b = SeedMember(factory, "CODE-HELD-B", "src/held-b.cs");
        var first = true;
        _gate.Batch = request =>
        {
            if (request.LedgerTriggerEvent?.StartsWith(BatchGatePilotService.FlakySuspectRegateCause,
                    StringComparison.Ordinal) == true)
                return HostRefused();
            var red = first;
            first = false;
            return red ? ReportRed(request.ExpectedSha) : Green(request.ExpectedSha);
        };
        _gate.Base = request => Green(request.ExpectedSha);
        _gate.Fallback = _ => throw new InvalidOperationException("A host-held re-gate member ran a per-card suite.");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        var parent = store.ListManifests().Single(manifest => manifest.ParentBatchId is null);
        var suspect = store.ReadFlakySuspect(ProjectName, parent.BatchId);
        Assert.NotNull(suspect);
        Assert.Equal(BatchFlakySuspectRegate.Requeued, suspect.Regate!.Outcome);
        Assert.Equal([b.Key], suspect.Regate.MemberKeys);
        Assert.True(suspect.Active);
        Assert.Contains(store.ListPending(), item => item.Subject.TaskKey == b.Key);
        Assert.Contains("flaky suspect re-gate could not reach the gate host",
            store.LatestState(parent.BatchId).Reason, StringComparison.Ordinal);
        AssertLane(a.Key, TaskStates.HumanReview);

        using var state = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{ProjectName}/batch-gate/state"));
        var row = state.RootElement.GetProperty("batches").EnumerateArray()
            .Single(batch => batch.GetProperty("batchId").GetString() == parent.BatchId);
        Assert.Equal(BatchFlakySuspectRegate.Requeued,
            row.GetProperty("flakySuspect").GetProperty("regate").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_green_regate_whose_publication_does_not_land_is_recorded_unpublished_with_its_reason()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        SeedMember(factory, "CODE-UNPUB-A", "src/unpub-a.cs");
        SeedMember(factory, "CODE-UNPUB-B", "src/unpub-b.cs");
        var first = true;
        _gate.Batch = request =>
        {
            if (request.LedgerTriggerEvent?.StartsWith(BatchGatePilotService.FlakySuspectRegateCause,
                    StringComparison.Ordinal) == true)
            {
                // The integration branch moves under the green re-gate, so it cannot publish.
                AdvanceOriginDevelop("src/unpub-drift.cs");
                return Green(request.ExpectedSha);
            }
            var red = first;
            first = false;
            return red ? ReportRed(request.ExpectedSha) : Green(request.ExpectedSha);
        };
        _gate.Base = request => Green(request.ExpectedSha);
        _gate.Fallback = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.WaitForFlightsAsync(includeFallbacks: true, CancellationToken.None);

        var parent = store.ListManifests().Single(manifest => manifest.ParentBatchId is null);
        var suspect = store.ReadFlakySuspect(ProjectName, parent.BatchId);
        Assert.NotNull(suspect);
        Assert.Equal(BatchFlakySuspectRegate.Unpublished, suspect.Regate!.Outcome);
        Assert.Contains(nameof(BatchPublishDecision.StaleBase), suspect.Regate.Reason, StringComparison.Ordinal);
        Assert.True(suspect.Active);
        Assert.NotEqual(BatchPhase.Published, store.LatestState(suspect.Regate.RegateBatchId).Phase);
        Assert.Equal(1, _gate.FallbackRuns);
    }

    [Fact]
    public async Task A_mark_whose_parent_left_isolation_without_a_regate_ends_interrupted()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        SeedMember(factory, "CODE-INTERRUPT-A", "src/interrupt-a.cs");
        SeedMember(factory, "CODE-INTERRUPT-B", "src/interrupt-b.cs");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var parent = ClosePendingBatch(factory);
        _gate.Batch = request => Green(request.ExpectedSha);
        BatchGateStates.Reach(store, new BatchGateState(parent.BatchId, parent.MembershipDigest,
            BatchPhase.Red, null, 1, _clock, "deterministic suite red"));
        // The mark was written and the process died before its re-gate recorded a verdict.
        Assert.True(store.RecordFlakySuspect(new BatchFlakySuspect(ProjectName, parent.BatchId, "red-run",
            parent.BaseSha, parent.Members.Select(member => member.TaskKey).ToArray(), [UnstableTest], _clock)));
        foreach (var item in store.ListPending())
            BatchGateOwnershipStore.Write(item.JobFolderPath,
                new BatchGateOwnership(item.ReviewAttemptId, item.Subject));

        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 2, scope: "code");
        await restarted.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        Assert.Equal("isolation interrupted", store.LatestState(parent.BatchId).Reason);
        var suspect = store.ReadFlakySuspect(ProjectName, parent.BatchId);
        Assert.NotNull(suspect);
        Assert.Equal(BatchFlakySuspectRegate.Interrupted, suspect.Regate!.Outcome);
        Assert.False(suspect.Active);
        Assert.DoesNotContain(store.ListFlakySuspects(ProjectName), view => view.Active);
    }

    private static BuildTestGateResult HostRefused()
    {
        const string refused = "Remote gate transport failure after bounded retries: ssh exited 255: "
            + "ssh: connect to host 172.30.83.1 port 12222: Connection refused";
        return new(BuildTestGateVerdict.Fail, null, 1, refused, refused, false, false)
        {
            FailureKind = BuildTestGateFailureKind.Environment,
        };
    }

    private BuildTestGateResult ReportRed(string? sha)
        => new(BuildTestGateVerdict.Fail, 1, 1, "Failed: 1",
            "dotnet test exit 1; output: Total tests: 2 Passed: 1 Failed: 1", false, false)
        {
            TestedSha = sha,
            GateRunId = Guid.NewGuid().ToString("N"),
            GateCompletedAtUtc = _clock,
            FailureKind = BuildTestGateFailureKind.Code,
            Processes =
            [
                new BuildTestGateProcessEvidence
                {
                    Command = "dotnet test product.Tests",
                    ExitCode = 1,
                    TestReport = new GateTestReport(GateTestCounts.None, [UnstableTest]),
                },
            ],
        };
}
