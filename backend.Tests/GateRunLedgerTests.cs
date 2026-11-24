using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateRunLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-ledger-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SelectionEfficiencyCountsRetryMinutesOncePerDistinctCardAndKeepsMissingCohortsUnknown()
    {
        var day = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var before = Row("before", "batch-gate", day) with { Cards = ["A", "B"], DurationMs = 1_200_000 };
        var after = Row("after", "batch-gate", day) with
        {
            Cards = ["C", "D"], DurationMs = 120_000, BatchId = "batch",
            TestSelector = AgentStudio.TaskServer.Contracts.ProjectTestSelectionPolicy.Selector,
        };
        var retry = after with { RunId = "retry", Type = "clean-diagnostic-rerun", DurationMs = 60_000 };
        var sample = GateRunLedger.SelectionEfficiency([before, after, retry]);
        Assert.Equal(10, sample.Before.MinutesPerCard);
        Assert.Equal(1.5, sample.After.MinutesPerCard);
        Assert.Equal(2, sample.After.Cards);
        Assert.Equal(2, sample.After.Runs);
        Assert.Null(GateRunLedger.SelectionEfficiency([after]).Before.MinutesPerCard);
    }

    [Fact]
    public void Rows_are_immutable_paged_and_filtered_by_day_and_type()
    {
        var day = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var ledger = new GateRunLedger(_root, new FakeTimeProvider(day));
        var first = Row("first", "batch-gate", day);
        ledger.Append(first);
        ledger.Append(Row("second", "batch-base", day.AddMinutes(1)) with
        { TriggerEvent = "red-candidate:first" });
        ledger.Append(Row("third", "batch-gate", day.AddDays(-1)));

        ledger.Append(first);
        ledger.Append(first with { Outcome = "Fail" });
        var page = ledger.Read("PROJ-002", DateOnly.FromDateTime(day.UtcDateTime), null, 1, 1);
        Assert.Equal(2, page.Total);
        Assert.Equal("second", Assert.Single(page.Rows).RunId);
        var basePage = ledger.Read("PROJ-002", DateOnly.FromDateTime(day.UtcDateTime), "batch-base");
        Assert.Equal("red-candidate:first", Assert.Single(basePage.Rows).TriggerEvent);
        Assert.Equal("Ok", Assert.Single(ledger.Read("PROJ-002", DateOnly.FromDateTime(day.UtcDateTime),
            "batch-gate").Rows).Outcome);
    }

    [Fact]
    public void Result_projection_keeps_process_and_shard_spans_and_rerun_cost()
    {
        var start = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var request = new BuildTestGateRequest("/tmp/repo", new string('a', 40), "batch-base")
        {
            Project = "PROJ-002", GateId = "batch-base", LedgerCards = ["AGT-3500"],
            LedgerTriggerEvent = "red-candidate:run-one",
        };
        var result = new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 2000, "", "red", false, false)
        {
            GateRunId = "run-two", GateQueueWaitMs = 500,
            GateHostLoadAtStart = 3.5, GateHostLoadAtEnd = 4.5,
            Processes = [new BuildTestGateProcessEvidence
            {
                Phase = "flaky-rerun", StartedAtUtc = start.AddSeconds(1),
                CompletedAtUtc = start.AddSeconds(2), ExitCode = 1,
            }],
        };
        var row = GateRunLedger.FromResult(request, result, start, start.AddSeconds(3),
            start.AddMilliseconds(500), 1.5, 2.5);
        Assert.Equal("batch-base", row.Type);
        Assert.Equal("red-candidate:run-one", row.TriggerEvent);
        Assert.Equal(1000, row.RerunDurationMs);
        Assert.Equal("flaky-test", row.RerunCause);
        Assert.Equal(start.AddSeconds(1), row.StartedAtUtc);
        Assert.Single(row.Spans);
        Assert.Equal(3.5, row.HostLoadStart);
        Assert.Equal(4.5, row.HostLoadEnd);
    }

    [Theory]
    [InlineData("flaky-suspect-regate:parent", "flaky-suspect-regate")]
    [InlineData("infrastructure-red", "infrastructure-retry")]
    public void Same_code_reruns_carry_their_minutes_by_purpose(string trigger, string purpose)
    {
        var start = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
        var request = new BuildTestGateRequest("/tmp/repo", new string('a', 40), "batch-gate")
        {
            Project = "PROJ-002", GateId = "batch-gate", LedgerCards = ["AGT-3858"],
            LedgerType = "clean-diagnostic-rerun", LedgerTriggerEvent = trigger,
        };
        var result = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 180_000, "", "ok", false, false)
        {
            GateRunId = "regate-run",
            Processes = [new BuildTestGateProcessEvidence
            {
                Phase = "flaky-rerun", StartedAtUtc = start.AddMinutes(2),
                CompletedAtUtc = start.AddMinutes(3), ExitCode = 0,
            }],
        };
        var row = GateRunLedger.FromResult(request, result, start, start.AddMinutes(3), start, null, null);

        Assert.Equal(120_000, row.SameCodeRerunMs![purpose]);
        Assert.Equal(60_000, row.SameCodeRerunMs[SameCodeRerunPurposes.FlakyTestRerun]);
        var ledger = new GateRunLedger(_root, new FakeTimeProvider(start));
        ledger.Append(row);
        ledger.Append(Row("first", "batch-gate", start));
        var summary = ledger.Summary(new BatchGateStore(Path.Combine(_root, "store")), "PROJ-002",
            DateOnly.FromDateTime(start.UtcDateTime));
        Assert.Equal(2, summary.SameCodeRerunMinutesByPurpose![purpose]);
        Assert.Equal(1, summary.SameCodeRerunMinutesByPurpose[SameCodeRerunPurposes.FlakyTestRerun]);
        Assert.Null(Assert.Single(ledger.ReadAll("PROJ-002"), item => item.RunId == "first").SameCodeRerunMs);
    }

    [Fact]
    public void A_first_gate_of_new_code_records_no_same_code_rerun()
    {
        var request = new BuildTestGateRequest("/tmp/repo", new string('a', 40), "batch-gate")
        {
            Project = "PROJ-002", GateId = "batch-gate", LedgerTriggerEvent = "red-candidate:parent",
            LedgerType = "clean-diagnostic-rerun",
        };
        var result = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 60_000, "", "ok", false, false);
        var at = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);

        Assert.Null(GateRunLedger.FromResult(request, result, at, at.AddMinutes(1), at, null, null).SameCodeRerunMs);
    }

    [Fact]
    public void Daily_minutes_reconcile_with_host_execution_facts()
    {
        var at = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var ledger = new GateRunLedger(_root, new FakeTimeProvider(at));
        var store = new BatchGateStore(Path.Combine(_root, "store"));
        ledger.Append(Row("batch", "batch-gate", at) with { DurationMs = 120_000 });
        ledger.Append(Row("base", "batch-base", at.AddMinutes(3)) with
        { DurationMs = 60_000, TriggerEvent = "red-candidate:batch" });
        store.RecordExecution(new BatchGateExecutionFact("PROJ-002", "batch-id", "batch",
            new string('a', 40), "Ok", "host", at, at.AddMinutes(2), 0, 0, "", 120_000));
        store.RecordExecution(new BatchGateExecutionFact("PROJ-002", "batch-id", "base",
            new string('a', 40), "Fail", "host", at.AddMinutes(3), at.AddMinutes(4), 0, 0, "", 60_000));
        var summary = ledger.Summary(store, "PROJ-002", DateOnly.FromDateTime(at.UtcDateTime));
        Assert.Equal(2, summary.GateMinutesByType["batch-gate"]);
        Assert.Equal(1, summary.GateMinutesByType["batch-base"]);
        Assert.Equal(0d, summary.ReconciliationPercent);
    }

    [Fact]
    public void Diagnostic_rerun_records_its_cause_and_full_duration()
    {
        var start = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var request = new BuildTestGateRequest("/tmp/repo", new string('a', 40), "batch-gate")
        {
            Project = "PROJ-002", GateId = "batch-gate",
            LedgerType = "clean-diagnostic-rerun", LedgerTriggerEvent = "red-candidate:original",
        };
        var result = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 75_000, "", "green", false, false);
        var row = GateRunLedger.FromResult(request, result, start, start.AddSeconds(75), start, null, null);
        Assert.Equal("red-candidate:original", row.RerunCause);
        Assert.Equal(75_000, row.RerunDurationMs);
    }

    [Fact]
    public async Task Repeated_run_id_does_not_change_gate_verdict_or_first_row()
    {
        var ledger = new GateRunLedger(_root);
        var runner = new LedgeredBuildTestGateRunner(new FixedGate(BuildTestGateVerdict.Ok), ledger);
        var request = Request("repeat");
        Assert.Equal(BuildTestGateVerdict.Ok, (await Run(runner, request)).Verdict);
        Assert.Equal(BuildTestGateVerdict.Ok, (await Run(runner, request)).Verdict);
        var page = ledger.Read("PROJ-002", null, null);
        Assert.Equal(1, page.Total);
        Assert.Equal("repeat", Assert.Single(page.Rows).RunId);
    }

    [Fact]
    public async Task Failing_ledger_store_does_not_change_gate_verdict()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "unwritable-root");
        File.WriteAllText(file, "occupied");
        var runner = new LedgeredBuildTestGateRunner(new FixedGate(BuildTestGateVerdict.Fail),
            new GateRunLedger(file));
        Assert.Equal(BuildTestGateVerdict.Fail, (await Run(runner, Request("failed-write"))).Verdict);
    }

    [Fact]
    public void Lease_timestamp_comes_from_coordinator_acquisition_and_differs_from_slot()
    {
        var acquired = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var scope = new BatchGateScope("PROJ-002", "repo", "develop", "full", "digest", "v1");
        var leases = new BatchGateLeaseService(Path.Combine(_root, "leases"), () => acquired);
        var lease = leases.TryAcquire(scope, "host")!;
        var slot = acquired.AddSeconds(12);
        var requested = acquired.AddSeconds(-4);
        var row = GateRunLedger.FromResult(Request("lease") with
        { LedgerRequestedAtUtc = requested, LedgerLeaseAcquiredAtUtc = lease.AcquiredAtUtc },
            new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 1000, "", "", false, false),
            requested, slot.AddSeconds(1), slot, null, null);
        Assert.Equal(TimeSpan.FromSeconds(4), row.LeaseAcquiredAtUtc!.Value - row.RequestedAtUtc);
        Assert.Equal(acquired, row.LeaseAcquiredAtUtc);
        Assert.Equal(slot, row.SlotAcquiredAtUtc);
        Assert.NotEqual(row.LeaseAcquiredAtUtc, row.SlotAcquiredAtUtc);
    }

    private static BuildTestGateRequest Request(string runId) => new("/tmp/repo", new string('a', 40), "batch-gate")
    { Project = "PROJ-002", GateId = "batch-gate", LedgerRunId = runId };

    private static Task<BuildTestGateResult> Run(IBuildTestGateRunner runner, BuildTestGateRequest request)
        => runner.RunAsync(request, null, null, PostStepMode.Fail, TimeSpan.FromMinutes(1),
            CancellationToken.None);

    private sealed class FixedGate(BuildTestGateVerdict verdict) : IBuildTestGateRunner
    {
        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
            TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new BuildTestGateResult(verdict, verdict == BuildTestGateVerdict.Ok ? 0 : 1,
                1000, "", "fixed", false, false));
    }

    private static GateRunLedgerRow Row(string id, string type, DateTimeOffset at)
        => new(id, type, "PROJ-002", ["AGT-3500"], new string('a', 40), "profile",
            at, at.AddSeconds(1), at.AddSeconds(1), at.AddSeconds(2), at.AddSeconds(4),
            "Ok", 2000, 1000, [], null, 0, 1, 2, null);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
