using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class IntegrationGateReceiptsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "integration-gate-receipt-" + Guid.NewGuid().ToString("N"));

    // Injected clock for the economics window; receipts are stamped against it.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadExact_recovers_legacy_receipt_without_cache_provenance()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var postSteps = Path.Combine(_root, "post-steps");
        Directory.CreateDirectory(postSteps);
        File.WriteAllText(Path.Combine(postSteps, "pre-main-test-gate-1.log"),
            "verdict=Ok exit=0 durationMs=321 failureKind=None\n" +
            $"expectedSha={sha} testedSha={sha}\n" +
            "reason=Legacy gate completed.\n" +
            "testSelectionAuditDigest=legacy-selection\n" +
            "reviewReuse=not-evaluated\n" +
            "--- last-300-lines ---\n");

        var recovered = IntegrationGateReceipts.ReadExact(_root, "pre-main-test-gate", sha);

        Assert.NotNull(recovered);
        Assert.Equal(BuildTestGateVerdict.Ok, recovered.Verdict);
        Assert.Equal(GateVerdictSource.Executed, recovered.VerdictSource);
        Assert.Equal(0, recovered.ExitCode);
        Assert.Equal(321, recovered.DurationMs);
        Assert.Equal("Legacy gate completed.", recovered.Reason);
        Assert.Null(recovered.GateRunId);
        Assert.Null(recovered.GateCompletedAtUtc);
        Assert.Null(recovered.OriginEvidencePath);
        Assert.Null(recovered.GateProfileDigest);
        Assert.Null(recovered.PipelineDefinitionVersion);
        Assert.Null(recovered.ToolchainIdentity);
    }

    [Fact]
    public void ReadExact_recovers_receipt_with_economics_headers()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var postSteps = Path.Combine(_root, "post-steps");
        Directory.CreateDirectory(postSteps);
        File.WriteAllText(Path.Combine(postSteps, "pre-develop-build-gate-1.log"),
            "verdict=Ok exit=0 durationMs=60000 failureKind=None\n" +
            $"expectedSha={sha} testedSha={sha}\n" +
            "reason=Passed.\n" +
            "verdictSource=Executed\n" +
            "gateRung=full\n" +
            "gateQueueWaitMs=500 mergeLockWaitMs=250\n");

        var recovered = IntegrationGateReceipts.ReadExact(_root, "pre-develop-build-gate", sha);

        Assert.NotNull(recovered);
        Assert.Equal(BuildTestGateVerdict.Ok, recovered.Verdict);
        Assert.Equal(60000, recovered.DurationMs);
    }

    [Fact]
    public void Record_writes_wait_and_rung_after_durable_header()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var result = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 60000,
            "", "Passed.", false, false)
        {
            ExpectedSha = sha,
            TestedSha = sha,
            GateQueueWaitMs = 800,
        };

        IntegrationGateReceipts.Record(_root, "pre-develop-build-gate", result,
            mergeLockWaitMs: 400, time: new FakeTimeProvider(Now));

        var lines = File.ReadAllLines(Path.Combine(_root, "post-steps", "pre-develop-build-gate-1.log"));
        Assert.StartsWith("verdict=", lines[0]);
        Assert.StartsWith("expectedSha=", lines[1]);
        Assert.StartsWith("reason=", lines[2]);
        Assert.Contains("gateRung=full", lines);
        Assert.Contains($"recordedAtUtc={Now:O}", lines);
        Assert.Contains(lines, line => line.StartsWith("gateQueueWaitMs=800 mergeLockWaitMs=400 gateCollisionDetected=false"));
        Assert.NotNull(IntegrationGateReceipts.ReadExact(_root, "pre-develop-build-gate", sha));
    }

    [Fact]
    public void Economics_counts_legacy_fields_as_unknown_and_uses_known_denominators()
    {
        var postSteps = Path.Combine(_root, "post-steps");
        Directory.CreateDirectory(postSteps);
        File.WriteAllText(Path.Combine(postSteps, "pre-develop-build-gate-1.log"),
            "verdict=Ok exit=0 durationMs=60000 failureKind=None\nexpectedSha=n/a testedSha=n/a\nreason=legacy\n" +
            "--- test-selection.json ---\n{\"Level\":\"work-package\"}\n--- resource-evidence.json ---\n");
        File.WriteAllText(Path.Combine(postSteps, "pre-develop-build-gate-2.log"),
            "verdict=Ok exit=0 durationMs=180000 failureKind=None\nexpectedSha=n/a testedSha=n/a\nreason=new\n" +
            "gateRung=exact\ntestLevel=full\ngateQueueWaitMs=1000 mergeLockWaitMs=200\n--- last-300-lines ---\n");
        StampReceipts(postSteps, Now.AddHours(-1));

        var row = GateEconomicsEndpoints.ProjectRow("fixture", [_root],
            since: Now.AddDays(-1), now: Now, retest: null);

        Assert.Equal(2, row.GatedMerges);
        Assert.Equal(2, row.GateMinutesMedian);
        Assert.Equal(1, row.ExactReuseRate);
        Assert.Equal(0, row.DisjointReuseRate);
        Assert.Equal(.5, row.FullSuiteShare);
        Assert.Equal(1200, row.QueueWaitP90Ms);
        Assert.Null(row.SameShaRetestRate);
        var withRetests = GateEconomicsEndpoints.ProjectRow("fixture", [_root],
            since: Now.AddDays(-1), now: Now,
            retest: new GateRetestReport(4, 1, .25, 0, GateResultCache.RetestWindowSize));
        Assert.Equal(.25, withRetests.SameShaRetestRate);
    }

    [Fact]
    public void Economics_amortizes_one_batch_gate_across_its_visible_members()
    {
        var start = Now.AddMinutes(-5);
        var batch = new BatchGateExecutionFact("fixture", "batch-1", "run-1",
            new string('a', 40), "Ok", "host", start, start.AddMinutes(4),
            1200, 0, "evidence.jsonl", 240000);

        var row = GateEconomicsEndpoints.ProjectRow("fixture", [],
            since: start.AddDays(-1), now: Now, retest: null, batchMetrics:
            [new BatchGateMetric("fixture", 2, batch, Published: true)]);

        Assert.Equal(2, row.GatedMerges);
        Assert.Equal(2, row.GateMinutesMedian);
        Assert.Equal(1, row.FullSuiteShare);
        Assert.Equal(1200, row.QueueWaitP90Ms);
        Assert.Equal(1, row.GateGreen);
    }

    [Fact]
    public void Economics_counts_batch_reruns_as_gate_runs_but_merges_only_the_published_run()
    {
        var start = Now.AddHours(-3);
        BatchGateExecutionFact Run(string runId, string outcome, int offsetMinutes) => new(
            "fixture", "batch-" + runId, runId, new string('b', 40), outcome, "host",
            start.AddMinutes(offsetMinutes), start.AddMinutes(offsetMinutes + 40),
            0, 0, "evidence.jsonl", 2_400_000);

        // A red three-member batch, one red isolation probe of two members and
        // the published survivor of one member.
        var row = GateEconomicsEndpoints.ProjectRow("fixture", [],
            since: Now.AddDays(-1), now: Now, retest: null, batchMetrics:
            [
                new BatchGateMetric("fixture", 3, Run("red", "Fail", 0)),
                new BatchGateMetric("fixture", 2, Run("probe", "Fail", 45)),
                new BatchGateMetric("fixture", 1, Run("survivor", "Ok", 90), Published: true),
            ]);

        Assert.Equal(1, row.GatedMerges);
        Assert.Equal(1, row.GateGreen);
        Assert.Equal(2, row.GateRed);
        Assert.Equal(1, row.FullSuiteShare);
    }

    [Fact]
    public void Economics_counts_published_batch_members_once_after_infrastructure_retry()
    {
        var start = Now.AddHours(-2);
        BatchGateExecutionFact Attempt(string outcome, int offsetMinutes, long durationMs) => new(
            "fixture", "batch-1", "run-1", new string('b', 40), outcome, "host",
            start.AddMinutes(offsetMinutes), start.AddMinutes(offsetMinutes + 40),
            0, 0, "evidence.jsonl", durationMs);

        var row = GateEconomicsEndpoints.ProjectRow("fixture", [],
            since: Now.AddDays(-1), now: Now, retest: null, batchMetrics:
            [
                new BatchGateMetric("fixture", 3, Attempt("Fail", 0, 2_400_000), Published: true),
                new BatchGateMetric("fixture", 3, Attempt("Ok", 45, 2_700_000), Published: true),
            ]);

        Assert.Equal(3, row.GatedMerges);
        Assert.Equal(1, row.GateRed);
        Assert.Equal(1, row.GateGreen);
        Assert.NotNull(row.GateMinutesMedian);
        Assert.Equal(85d / 6, row.GateMinutesMedian.Value, precision: 6);
    }

    [Fact]
    public void Old_receipt_header_does_not_read_legacy_selection_outside_window()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "old-receipt.log");
        File.WriteAllText(path,
            "verdict=Fail durationMs=60000\nexpectedSha=n/a\nreason=old\n" +
            $"originalCompletedAtUtc={Now.AddDays(-40):O}\n" +
            "--- test-selection.json ---\n{\"Level\":\"full\"}\n");
        var old = GateReceiptMetric.Read(path, Now.UtcDateTime, Now.AddDays(-30), Now);
        Assert.Null(old.TestLevel);
        var inWindow = GateReceiptMetric.Read(path, Now.UtcDateTime, Now.AddDays(-60), Now);
        Assert.Equal("full", inWindow.TestLevel);
    }

    [Fact]
    public void Economics_places_receipts_by_recorded_time_not_by_file_time()
    {
        var postSteps = Path.Combine(_root, "post-steps");
        Directory.CreateDirectory(postSteps);
        // A clone restamps every file; the receipt's own time still places it.
        File.WriteAllText(Path.Combine(postSteps, "pre-develop-build-gate-1.log"),
            "verdict=Ok exit=0 durationMs=60000 failureKind=None\nexpectedSha=n/a testedSha=n/a\nreason=old\n" +
            $"verdictSource=Executed originalRunId=r1 originalCompletedAtUtc={Now.AddDays(-40):O}\n");
        File.WriteAllText(Path.Combine(postSteps, "pre-develop-build-gate-2.log"),
            "verdict=Ok exit=0 durationMs=120000 failureKind=None\nexpectedSha=n/a testedSha=n/a\nreason=new\n" +
            $"verdictSource=Executed originalRunId=r2 originalCompletedAtUtc={Now.AddDays(-40):O}\n" +
            $"gateRung=exact\ntestLevel=full\nrecordedAtUtc={Now.AddDays(-2):O}\n");
        StampReceipts(postSteps, Now.AddMinutes(-1));

        var row = GateEconomicsEndpoints.ProjectRow("fixture", [_root],
            since: Now.AddDays(-30), now: Now, retest: null);

        Assert.Equal(1, row.GatedMerges);
        Assert.Equal(2, row.GateMinutesMedian);
    }

    [Fact]
    public void Missing_source_gate_records_no_rung_and_counts_as_unknown()
    {
        var missing = new BuildTestGateResult(BuildTestGateVerdict.Fail, null, 0,
            "", "No pre-merge tip.", false, false)
        {
            FailureKind = BuildTestGateFailureKind.MissingSource,
        };
        var executed = new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 120000,
            "", "Passed.", false, false);
        var time = new FakeTimeProvider(Now.AddHours(-1));
        IntegrationGateReceipts.Record(_root, "pre-develop-build-gate", missing, time: time);
        IntegrationGateReceipts.Record(_root, "pre-develop-build-gate", executed, time: time);

        var row = GateEconomicsEndpoints.ProjectRow("fixture", [_root],
            since: Now.AddDays(-1), now: Now, retest: null);

        Assert.Contains("gateRung=none",
            File.ReadAllLines(Path.Combine(_root, "post-steps", "pre-develop-build-gate-1.log")));
        Assert.Equal(0, row.ExactReuseRate);
        Assert.Equal(2, row.GateMinutesMedian);
        Assert.Equal(0, row.GateRed);
        Assert.Equal(1, row.GatedMerges);
    }

    [Fact]
    public void Economics_aggregate_count_equals_sum_of_project_counts()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(Path.Combine(first, "post-steps"));
        Directory.CreateDirectory(Path.Combine(second, "post-steps"));
        File.WriteAllText(Path.Combine(first, "post-steps", "pre-develop-build-gate-1.log"),
            "verdict=Ok durationMs=60000\nexpectedSha=n/a testedSha=n/a\nreason=old\n");
        File.WriteAllText(Path.Combine(second, "post-steps", "pre-develop-build-gate-1.log"),
            "verdict=Fail durationMs=120000\nexpectedSha=n/a testedSha=n/a\nreason=red\n");
        StampReceipts(Path.Combine(first, "post-steps"), Now.AddDays(-2));
        StampReceipts(Path.Combine(second, "post-steps"), Now.AddDays(-2));
        var a = GateEconomicsEndpoints.ProjectRow("A", [first], since: Now.AddDays(-7), now: Now, retest: null);
        var b = GateEconomicsEndpoints.ProjectRow("B", [second], since: Now.AddDays(-7), now: Now, retest: null);
        var all = GateEconomicsEndpoints.ProjectRow("All projects", [first, second],
            since: Now.AddDays(-7), now: Now, retest: null);

        Assert.Equal(a.GatedMerges + b.GatedMerges, all.GatedMerges);
        Assert.Equal(1.5, all.GateMinutesMedian);
        Assert.Null(all.QueueWaitP90Ms);
    }

    private static void StampReceipts(string postSteps, DateTimeOffset writtenAt)
    {
        foreach (var path in Directory.EnumerateFiles(postSteps, "pre-develop-build-gate-*.log"))
            File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
