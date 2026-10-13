using AgentStudio.Pipeline;
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
            mergeLockWaitMs: 400);

        var lines = File.ReadAllLines(Path.Combine(_root, "post-steps", "pre-develop-build-gate-1.log"));
        Assert.StartsWith("verdict=", lines[0]);
        Assert.StartsWith("expectedSha=", lines[1]);
        Assert.StartsWith("reason=", lines[2]);
        Assert.Contains("gateRung=full", lines);
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
            [new BatchGateMetric("fixture", 2, batch)]);

        Assert.Equal(2, row.GatedMerges);
        Assert.Equal(2, row.GateMinutesMedian);
        Assert.Equal(1, row.FullSuiteShare);
        Assert.Equal(1200, row.QueueWaitP90Ms);
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
