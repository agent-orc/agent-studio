using AgentStudio.Git;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3309: the pipeline delivery-target baseline computed from fixture
/// receipts, Remote Review reports and integration-lane events, including
/// legacy evidence that predates the fields it reads.
/// </summary>
public sealed class PipelineBaselineMetricsTests : IDisposable
{
    // Injected clock for the economics window; every fixture is stamped against it.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pipeline-baseline-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Card_lead_time_and_gate_minutes_come_from_the_last_round_and_both_tiers()
    {
        var sha = new string('a', 40);
        // Round 1 passed review at -90 min and was ejected; round 2 passed at -40 min.
        Event("enqueued", 1, Now.AddMinutes(-89), deliveredAt: Now.AddMinutes(-90));
        Event("agent-round-re-entry", 2, Now.AddMinutes(-39), deliveredAt: Now.AddMinutes(-40));
        Event("published", 2, Now.AddMinutes(-10), receipt: sha);
        Review("attempt-1", Now.AddMinutes(-95),
            Row("verify-1", "60000"), Row("verify-2", "240000"), Row("aspect-code", "900000", phase: "review"));
        Review("attempt-2", Now.AddMinutes(-45),
            Row("verify-1", "60000"), Row("verify-2", "240000", result: "verify result reused from attempt attempt-1 (50m)"),
            Row("verify-2", "120000", workspace: "baseline-1", baseline: "baseline result reused from attempt attempt-1 (50m)"));
        Receipt(1, "Fail", 600000, Now.AddMinutes(-80), level: "full");
        Receipt(2, "Ok", 300000, Now.AddMinutes(-20), sha: sha, level: "work-package",
            reason: "Passed. Source bundle 52428800 bytes transferred in 12.5s.");
        Receipt(3, "Ok", 999999, Now.AddMinutes(-15), sha: sha, level: "full", source: "CacheHit",
            reason: "Passed. Source bundle 99999999 bytes transferred in 99.0s.");

        var baseline = Row(_root);

        Assert.Equal(30, baseline.LeadTimeMedianMinutes);
        Assert.Equal(30, baseline.LeadTimeP90Minutes);
        Assert.Equal(1, baseline.LeadTimeCards);
        Assert.Equal(0, baseline.LeadTimeUnknownCards);
        // T1: 1 + 4 (attempt 1) + 1 (attempt 2, reused rows skipped); T2: 10 + 5, the cache hit ran nothing.
        Assert.Equal(21, baseline.GateMinutesPerCardMedian);
        Assert.Equal(1, baseline.GateMinutesCards);
        Assert.Equal(1, baseline.FullSuiteRuns);
        Assert.Equal(1 / 7d, baseline.FullSuiteRunsPerDay);
        Assert.Equal(52428800, baseline.GateSourceBytesMedian);
        Assert.Equal(12.5, baseline.GateSourceSecondsMedian);
        Assert.Equal(1, baseline.GateSourceRuns);
        Assert.Null(baseline.RedDevelopMedianMinutes);
        Assert.Null(baseline.RedDevelopP90Minutes);
    }

    [Fact]
    public void Legacy_receipts_reports_and_events_without_new_fields_count_as_unknown()
    {
        var sha = new string('b', 40);
        // A legacy arrival has no delivered time; a legacy report has no budget column value.
        Event("enqueued", 1, Now.AddMinutes(-60));
        Event("published", 1, Now.AddMinutes(-5), receipt: sha);
        Review("attempt-1", Now.AddMinutes(-70), Row("verify-1", null));
        var postSteps = Directory.CreateDirectory(Path.Combine(_root, "post-steps")).FullName;
        var legacy = Path.Combine(postSteps, "pre-develop-build-gate-1.log");
        File.WriteAllText(legacy,
            $"verdict=Ok exit=0 durationMs=1200000 failureKind=None\nexpectedSha={sha} testedSha={sha}\nreason=legacy\n" +
            "--- test-selection.json ---\n{\"Level\":\"full\"}\n--- resource-evidence.json ---\n");
        File.SetLastWriteTimeUtc(legacy, Now.AddMinutes(-30).UtcDateTime);

        var baseline = Row(_root);

        Assert.Null(baseline.LeadTimeMedianMinutes);
        Assert.Equal(0, baseline.LeadTimeCards);
        Assert.Equal(1, baseline.LeadTimeUnknownCards);
        Assert.Null(baseline.GateMinutesPerCardMedian);
        Assert.Equal(0, baseline.GateMinutesCards);
        Assert.Equal(1, baseline.GateMinutesUnknownCards);
        // The legacy selection audit still proves one full-suite run.
        Assert.Equal(1, baseline.FullSuiteRuns);
        Assert.Null(baseline.GateSourceBytesMedian);
        Assert.Null(baseline.GateSourceSecondsMedian);
        Assert.Equal(0, baseline.GateSourceRuns);
    }

    [Fact]
    public void Legacy_receipt_with_a_null_selection_audit_reads_as_unknown_level()
    {
        // A gate that ended before test selection writes "null" here. Receipts from before the
        // testLevel header fall back to this audit, and the 30-day window failed with HTTP 500.
        var postSteps = Directory.CreateDirectory(Path.Combine(_root, "post-steps")).FullName;
        var path = Path.Combine(postSteps, "pre-develop-build-gate-1.log");
        File.WriteAllText(path,
            "verdict=Fail exit=1 durationMs=60000 failureKind=Code\nexpectedSha=n/a testedSha=n/a\nreason=legacy\n" +
            "--- test-selection.json ---\nnull\n--- resource-evidence.json ---\n");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-20).UtcDateTime);

        var receipt = GateReceiptMetric.Read(path, Now.AddDays(-20).UtcDateTime, Now.AddDays(-30), Now);
        var row = GateEconomicsEndpoints.ProjectRow("fixture", [_root], since: Now.AddDays(-30), now: Now, retest: null);

        Assert.Null(receipt.TestLevel);
        Assert.Equal(1, row.GateRed);
        Assert.Null(row.Baseline!.FullSuiteRuns);
    }

    [Fact]
    public void Missing_inputs_are_unknown_never_zero()
    {
        Directory.CreateDirectory(_root);

        var baseline = Row(_root);

        Assert.Equal(PipelineBaseline.Unknown, baseline);
        Assert.Null(baseline.FullSuiteRuns);
        Assert.Null(baseline.FullSuiteRunsPerDay);
    }

    [Fact]
    public void Settlement_journal_counts_executed_tool_verification_and_wins_over_the_report()
    {
        var sha = new string('f', 40);
        Event("enqueued", 1, Now.AddMinutes(-60), deliveredAt: Now.AddMinutes(-60));
        Event("published", 1, Now.AddMinutes(-5), receipt: sha);
        Receipt(1, "Ok", 600000, Now.AddMinutes(-20), sha: sha);
        var start = Now.AddMinutes(-80);
        Journal("attempt-1", Now.AddMinutes(-62),
            Command("prepare-1", start, 4, phase: "preparation"),
            Command("verify-1", start, 2),
            Command("verify-2", start, 1),
            Command("aspect-code-quality", start, 5, kind: "agent-aspect"),
            Command("verify-3", start, 10, extra: "\"verifyReusedFromAttemptId\":\"attempt-0\""),
            Command("verify-1", start, 10, extra: "\"workspaceRole\":\"baseline-1\",\"baselineReusedFromAttemptId\":\"attempt-0\""));
        // The Markdown projection of the same attempt is not counted twice.
        Review("attempt-1", Now.AddMinutes(-62), Row("verify-1", "6000000"));

        var baseline = Row(_root);

        Assert.Equal(13, baseline.GateMinutesPerCardMedian);
        Assert.Equal(1, baseline.GateMinutesCards);
    }

    [Fact]
    public void Legacy_journal_command_without_times_is_unknown()
    {
        var review = PipelineBaselineMetrics.ParseSettlement(
            $"{{\"attemptId\":\"a-1\",\"receivedAtUtc\":\"{Now:O}\",\"report\":{{\"commands\":[{{\"stepId\":\"verify-1\"}}]}}}}");
        Assert.NotNull(review);
        Assert.Null(review.Minutes);
        Assert.Null(PipelineBaselineMetrics.ParseSettlement("{\"attemptId\":\"a-2\"}"));
    }

    [Fact]
    public void Report_without_command_evidence_leaves_the_card_unknown()
    {
        var review = PipelineBaselineMetrics.ParseReview(
            "---\nattemptId: \"a-1\"\n---\n\n## Command evidence\n\n_No command evidence was supplied._\n",
            "fallback", Now);
        Assert.Equal("a-1", review.AttemptId);
        Assert.Equal(Now, review.AtUtc);
        Assert.Null(review.Minutes);
    }

    [Fact]
    public void Batch_member_receives_its_share_of_the_published_batch_run()
    {
        Event("enqueued", 1, Now.AddMinutes(-50), deliveredAt: Now.AddMinutes(-50));
        Event("published", 1, Now.AddMinutes(-5), receipt: new string('c', 40));
        Review("attempt-1", Now.AddMinutes(-55), Row("verify-1", "120000"));
        var run = new BatchGateExecutionFact("fixture", "batch-1", "run-1", new string('c', 40), "Ok", "host",
            Now.AddMinutes(-30), Now.AddMinutes(-10), 0, 0, "evidence.jsonl", 1200000, ["card-1", "card-2"]);
        var red = run with { BatchRunId = "run-0", Outcome = "Fail", CompletedAtUtc = Now.AddMinutes(-35) };

        var baseline = GateEconomicsEndpoints.ProjectRow("fixture", [_root], since: Now.AddDays(-7), now: Now,
            retest: null, batchMetrics: [new BatchGateMetric("fixture", 2, red), new BatchGateMetric("fixture", 2, run, Published: true)])
            .Baseline!;

        Assert.Equal(12, baseline.GateMinutesPerCardMedian);
        Assert.Equal(45, baseline.LeadTimeMedianMinutes);
        // Every batch run is one full-suite execution, red or green.
        Assert.Equal(2, baseline.FullSuiteRuns);
    }

    [Fact]
    public void Aggregate_counts_equal_the_sum_of_the_project_rows()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        foreach (var (folder, sha) in new[] { (first, new string('d', 40)), (second, new string('e', 40)) })
        {
            Event("enqueued", 1, Now.AddMinutes(-30), deliveredAt: Now.AddMinutes(-30), folder: folder);
            Event("published", 1, Now.AddMinutes(-5), receipt: sha, folder: folder);
            ReviewIn(folder, "attempt-1", Now.AddMinutes(-35), Row("verify-1", "60000"));
            Receipt(1, "Ok", 120000, Now.AddMinutes(-10), sha: sha, level: "full", folder: folder,
                reason: "Passed. Source bundle 1048576 bytes transferred in 2.0s.");
        }

        var a = GateEconomicsEndpoints.ProjectRow("A", [first], since: Now.AddDays(-7), now: Now, retest: null).Baseline!;
        var b = GateEconomicsEndpoints.ProjectRow("B", [second], since: Now.AddDays(-7), now: Now, retest: null).Baseline!;
        var all = GateEconomicsEndpoints.ProjectRow(GateEconomicsEndpoints.AllProjects, [first, second],
            since: Now.AddDays(-7), now: Now, retest: null).Baseline!;

        Assert.Equal(a.FullSuiteRuns + b.FullSuiteRuns, all.FullSuiteRuns);
        Assert.Equal(a.FullSuiteRunsPerDay + b.FullSuiteRunsPerDay, all.FullSuiteRunsPerDay);
        Assert.Equal(a.LeadTimeCards + b.LeadTimeCards, all.LeadTimeCards);
        Assert.Equal(a.GateMinutesCards + b.GateMinutesCards, all.GateMinutesCards);
        Assert.Equal(a.GateSourceRuns + b.GateSourceRuns, all.GateSourceRuns);
        Assert.Equal(3, all.GateMinutesPerCardMedian);
    }

    [Fact]
    public void Board_read_reports_each_handler_and_unknown_without_requests()
    {
        var read = PipelineBaselineMetrics.BoardRead([
            new EndpointStats("tasks/grouped", 158, 3139.5, 5598.4, 0, 0, 158),
            new EndpointStats("board/integration-status", 523, 43.5, 5471.5, 826, 13.8, 522),
            new EndpointStats("wiki/tree", 234, 10, 20, 5, 0.1, 234),
        ]);

        Assert.Equal(["tasks/grouped", "tasks/list", "board/integration-status", "board/merge-status"],
            read.Select(handler => handler.Label));
        var board = read[0];
        Assert.Equal((158, 3139.5, 5598.4, 0d), (board.Requests, board.P50Ms!.Value, board.P95Ms!.Value,
            board.GitSpawnsPerRequest!.Value));
        Assert.Equal(826d / 523, read[2].GitSpawnsPerRequest);
        Assert.Equal(new BoardReadMetric("tasks/list", "Card list", 0, null, null, null), read[1]);
    }

    [Theory]
    [InlineData("Passed. Source bundle 2048 bytes transferred in 3.5s.", 2048L, 3.5)]
    [InlineData("Passed. Source bundle 2048 bytes transferred in 3,5s.", 2048L, 3.5)]
    [InlineData("Passed. Source bundle 0 bytes transferred in 0.0s. Source ready 12.4s.", 0L, 12.4)]
    public void Source_transfer_reads_the_receipt_reason_in_any_culture(string reason, long bytes, double seconds)
        => Assert.Equal((bytes, seconds), PipelineBaselineMetrics.ParseSourceTransfer(reason));

    [Fact]
    public void Local_gate_reason_has_no_source_transfer()
        => Assert.Null(PipelineBaselineMetrics.ParseSourceTransfer("Passed."));

    [Fact]
    public void Gate_without_a_ready_checkout_has_no_ready_metric()
        => Assert.Null(PipelineBaselineMetrics.ParseSourceTransfer(
            "Failed. Source bundle 0 bytes transferred in 0.0s. Source ready unavailable."));

    private PipelineBaseline Row(string folder)
        => GateEconomicsEndpoints.ProjectRow("fixture", [folder], since: Now.AddDays(-7), now: Now, retest: null).Baseline!;

    private void Event(string kind, long sequence, DateTimeOffset at, DateTimeOffset? deliveredAt = null,
        string? receipt = null, string? folder = null)
    {
        Directory.CreateDirectory(folder ?? _root);
        IntegrationLaneEvents.Append(folder ?? _root, "fixture", "card-1", kind, sequence: sequence,
            deliveredAtUtc: deliveredAt, receipt: receipt, atUtc: at);
    }

    private void Receipt(int index, string verdict, long durationMs, DateTimeOffset at, string? sha = null,
        string level = "full", string source = "Executed", string reason = "Passed.", string? folder = null)
    {
        var postSteps = Directory.CreateDirectory(Path.Combine(folder ?? _root, "post-steps")).FullName;
        var path = Path.Combine(postSteps, $"pre-develop-build-gate-{index}.log");
        File.WriteAllText(path,
            $"verdict={verdict} exit=0 durationMs={durationMs} failureKind=None\n" +
            $"expectedSha={sha ?? "n/a"} testedSha={sha ?? "n/a"}\nreason={reason}\n" +
            $"verdictSource={source} originalRunId=r{index} originalCompletedAtUtc={at:O}\n" +
            $"gateRung=full\ntestLevel={level}\nrecordedAtUtc={at:O}\n" +
            "gateQueueWaitMs=900000 mergeLockWaitMs=0 gateCollisionDetected=false\n--- last-300-lines ---\n");
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
    }

    private void Review(string attemptId, DateTimeOffset receivedAt, params string[] rows)
        => ReviewIn(_root, attemptId, receivedAt, rows);

    private static void ReviewIn(string folder, string attemptId, DateTimeOffset receivedAt, params string[] rows)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"remote-review-grade-{attemptId}.md");
        File.WriteAllText(path,
            $"---\ntype: remote-review-grade\nattemptId: \"{attemptId}\"\nreceivedAt: {receivedAt:O}\noutcome: \"Passed\"\n---\n\n" +
            "## Command evidence\n\n" +
            "| Phase | Workspace | Step | Location | Host / executor | Command | Exit | Budget | Output | Errors | Baseline | Result |\n" +
            "| --- | --- | --- | --- | --- | --- | ---: | --- | --- | --- | --- | --- |\n" +
            string.Join("\n", rows) + "\n\n## Immutable subject proof\n");
        File.SetLastWriteTimeUtc(path, receivedAt.UtcDateTime);
    }

    private void Journal(string attemptId, DateTimeOffset receivedAt, params string[] commands)
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "logs")).FullName;
        var path = Path.Combine(logs, $"remote-review-settlement-{attemptId}.json");
        File.WriteAllText(path,
            $"{{\"version\":1,\"attemptId\":\"{attemptId}\",\"receivedAtUtc\":\"{receivedAt:O}\"," +
            $"\"report\":{{\"commands\":[{string.Join(",", commands)}]}}}}");
        File.SetLastWriteTimeUtc(path, receivedAt.UtcDateTime);
    }

    private static string Command(string step, DateTimeOffset started, int minutes,
        string phase = "verification", string kind = "tool", string? extra = null)
        => $"{{\"stepId\":\"{step}\",\"phase\":\"{phase}\",\"executionKind\":\"{kind}\"," +
           $"\"startedAt\":\"{started:O}\",\"finishedAt\":\"{started.AddMinutes(minutes):O}\"" +
           (extra is null ? "" : "," + extra) + "}";

    private static string Row(string step, string? consumedMs, string phase = "verification",
        string workspace = "candidate", string baseline = "n/a", string result = "executed in this attempt")
        => $"| {phase} | {workspace} | {step} | remote | host / executor | `dotnet test --filter A\\|B` | 0 | " +
           (consumedMs is null ? "not reported" : $"review-command: {consumedMs}/7200000 ms") +
           $" | stdout | stderr | {baseline} | {result} |";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
