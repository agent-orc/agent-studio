using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: ranking receives an explicit now and evidence dates are parsed without reading the wall clock.
public sealed class BatchGateFailureRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evidence_retry_is_dropped_after_ninety_days()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reads = 0;
        try
        {
            var store = new BatchGateStore(root, _ =>
            {
                reads++;
                throw new IOException("Evidence missing.");
            });
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, "missing", MemberKeys: ["AGT-1"], EvidenceGateRunId: "gate-id"));
            Assert.Equal(1, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);
            Assert.Equal(0, store.FailedTestRanking("Project", Now.AddDays(91)).PendingEvidenceRuns);
            store.FailedTestRanking("Project", Now.AddDays(92));
            Assert.Equal(1, reads);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BatchTestFailure Failure(string batch, string member, int daysAgo = 0,
        string test = "Product.Tests.Unstable", bool rerunPassed = false) =>
        new(test, batch, batch + "-run", [member], Now.AddDays(-daysAgo),
            "Error Message: timed out", rerunPassed);

    [Fact]
    public void Three_disjoint_batches_are_suspects_and_a_shared_member_is_not()
    {
        var disjoint = BatchGateFailureRanking.Rank([
            Failure("one", "AGT-1"), Failure("two", "AGT-2"), Failure("three", "AGT-3")], Now);
        Assert.True(Assert.Single(disjoint).FlakySuspect);
        Assert.Equal(3, disjoint[0].DistinctMemberSets);
        Assert.Equal(3, disjoint[0].Failures.Count);

        var shared = BatchGateFailureRanking.Rank([
            Failure("one", "AGT-1"), Failure("two", "AGT-1"), Failure("three", "AGT-2")], Now);
        Assert.False(Assert.Single(shared).FlakySuspect);
    }

    [Fact]
    public void Window_drops_old_fault_and_rerun_pass_is_recorded()
    {
        var observations = new[] {
            Failure("one", "AGT-1", 8), Failure("two", "AGT-2", 1, rerunPassed: true),
            Failure("three", "AGT-3", 0),
        };
        var rank = Assert.Single(BatchGateFailureRanking.Rank(observations, Now));
        Assert.Equal(2, rank.Batches);
        Assert.True(rank.FlakyRerunPassed);
        Assert.False(rank.FlakySuspect);
        // After the member fault is fixed and no further reds occur, the window clears it.
        Assert.Empty(BatchGateFailureRanking.Rank(observations, Now.AddDays(9)));
    }

    [Fact]
    public void Member_fault_that_stops_after_a_fix_never_becomes_a_flaky_suspect()
    {
        var observations = new[] {
            Failure("before-fix-1", "AGT-42", 6),
            Failure("before-fix-2", "AGT-42", 5),
            Failure("before-fix-3", "AGT-42", 4),
        };
        Assert.False(Assert.Single(BatchGateFailureRanking.Rank(observations, Now)).FlakySuspect);
        Assert.Empty(BatchGateFailureRanking.Rank(observations, Now.AddDays(4)));
    }

    [Fact]
    public void Repeated_runs_in_one_batch_count_once_as_a_batch()
    {
        var first = Failure("one", "AGT-1");
        var rank = Assert.Single(BatchGateFailureRanking.Rank([
            first, first with { RunId = "one-rerun" }, Failure("two", "AGT-2")], Now));
        Assert.Equal(2, rank.Batches);
        Assert.Equal(3, rank.Runs);
    }

    [Fact]
    public void Evidence_reads_failure_and_flaky_classification()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            File.WriteAllText(path, """{"GateRunId":"run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","Output":"Product.Tests.Unstable\nError Message: timed out\nline two","RecordedFailedTests":["Product.Tests.Unstable"],"FlakyQuarantinedFailures":["Product.Tests.Unstable"]}""" + "\n");
            var fact = new BatchGateExecutionFact("Project", "batch", "run", "sha", "pass", "host",
                Now, Now, 0, 0, path, MemberKeys: ["AGT-1"], EvidenceGateRunId: "run");
            var failure = Assert.Single(BatchGateFailureRanking.ReadEvidence(fact));
            Assert.Equal("Product.Tests.Unstable", failure.TestName);
            Assert.True(failure.FlakyRerunPassed);
            Assert.Contains("Error Message: timed out", failure.ErrorExcerpt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_projection_adds_new_execution_after_first_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            var store = new BatchGateStore(root);
            File.WriteAllText(evidence, """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T11:00:00Z","RecordedFailedTests":["Product.Tests.Unstable"]}""" + "\n");
            var first = new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now.AddHours(-1), Now.AddHours(-1), 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run");
            store.RecordExecution(first);
            Assert.Equal(1, Assert.Single(store.FailedTestRanking("Project", Now).Tests).Batches);

            File.AppendAllText(evidence, """{"GateRunId":"two-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Product.Tests.Unstable"]}""" + "\n");
            store.RecordExecution(first with { BatchId = "two", BatchRunId = "two-run",
                StartedAtUtc = Now, CompletedAtUtc = Now, MemberKeys = ["AGT-2"],
                EvidenceGateRunId = "two-run" });
            var rank = Assert.Single(store.FailedTestRanking("Project", Now).Tests);
            Assert.Equal(2, rank.Batches);
            Assert.Equal(2, rank.Runs);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Concurrent_initialization_and_recording_project_each_execution_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var initialReadStarted = new ManualResetEventSlim();
        using var allowInitialRead = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        try
        {
            var store = new BatchGateStore(root, fact =>
            {
                if (fact.BatchId == "one")
                {
                    initialReadStarted.Set();
                    if (!allowInitialRead.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Initial evidence read was not released.");
                }
                return [new BatchTestFailure("Product.Tests.Unstable", fact.BatchId!, fact.BatchRunId!,
                    fact.MemberKeys ?? [], fact.CompletedAtUtc, "", false)];
            });
            var first = new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, "evidence", MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run");
            store.RecordExecution(first);

            var initialization = Task.Run(() => store.FailedTestRanking("Project", Now));
            Assert.True(initialReadStarted.Wait(TimeSpan.FromSeconds(10)));
            var recording = Task.Run(() =>
            {
                writerStarted.Set();
                store.RecordExecution(first with { BatchId = "two", BatchRunId = "two-run",
                    MemberKeys = ["AGT-2"], EvidenceGateRunId = "two-run" });
            });
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(10)));
            allowInitialRead.Set();

            Assert.Single(Assert.Single((await initialization).Tests).Failures);
            await recording;
            var failures = Assert.Single(store.FailedTestRanking("Project", Now).Tests).Failures;
            Assert.Equal(2, failures.Count);
            Assert.Equal(["one-run", "two-run"], failures.Select(failure => failure.RunId).Order());
        }
        finally
        {
            allowInitialRead.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Evidence_read_failure_after_persistence_does_not_fail_recording_and_retries_only_that_fact()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failNextRead = false;
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var store = new BatchGateStore(root, fact =>
            {
                reads[fact.BatchId!] = reads.GetValueOrDefault(fact.BatchId!) + 1;
                if (failNextRead)
                {
                    failNextRead = false;
                    throw new IOException("Evidence is temporarily unavailable.");
                }
                return [new BatchTestFailure("Product.Tests.Unstable", fact.BatchId!, fact.BatchRunId!,
                    fact.MemberKeys ?? [], fact.CompletedAtUtc, "Error Message: timed out", false)];
            });
            var first = new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now.AddHours(-1), Now.AddHours(-1), 0, 0, "evidence", MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run");
            store.RecordExecution(first);
            Assert.Equal(1, Assert.Single(store.FailedTestRanking("Project", Now).Tests).Batches);

            failNextRead = true;
            store.RecordExecution(first with { BatchId = "two", BatchRunId = "two-run",
                StartedAtUtc = Now, CompletedAtUtc = Now, MemberKeys = ["AGT-2"],
                EvidenceGateRunId = "two-run" });

            Assert.Equal(2, store.ListExecutions("Project").Count);
            Assert.Equal(2, Assert.Single(store.FailedTestRanking("Project", Now).Tests).Batches);
            Assert.Equal(1, reads["one"]);
            Assert.Equal(2, reads["two"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Unreadable_evidence_during_initial_projection_is_retried()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var attempts = 0;
        try
        {
            var store = new BatchGateStore(root, fact =>
            {
                if (++attempts == 1) throw new IOException("Evidence is temporarily unavailable.");
                return [new BatchTestFailure("Product.Tests.Unstable", fact.BatchId!, fact.BatchRunId!,
                    fact.MemberKeys ?? [], fact.CompletedAtUtc, "", false)];
            });
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, "evidence", MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run"));

            var pending = store.FailedTestRanking("Project", Now);
            Assert.Empty(pending.Tests);
            Assert.Equal(1, pending.PendingEvidenceRuns);
            var recovered = store.FailedTestRanking("Project", Now);
            Assert.Single(recovered.Tests);
            Assert.Equal(0, recovered.PendingEvidenceRuns);
            Assert.Equal(2, attempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Missing_evidence_remains_visible_until_it_arrives_without_rescanning_other_facts()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            var store = new BatchGateStore(root);
            var fact = new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run");
            store.RecordExecution(fact);
            var missing = store.FailedTestRanking("Project", Now);
            Assert.Empty(missing.Tests);
            Assert.Equal(1, missing.PendingEvidenceRuns);
            Assert.Equal(1, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);

            File.WriteAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Product.Tests.Unstable"]}""" + "\n");
            var recovered = store.FailedTestRanking("Project", Now);
            Assert.Equal(0, recovered.PendingEvidenceRuns);
            Assert.Equal(1, Assert.Single(recovered.Tests).Batches);
            Assert.Equal(0, store.FailedTestRanking("Project", Now.AddDays(8)).PendingEvidenceRuns);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Incomplete_evidence_is_retried_until_the_failure_inventory_arrives()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            var store = new BatchGateStore(root);
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run"));

            foreach (var incomplete in new[] { "", "{invalid}\n", """{"GateRunId":"one-run"}""" + "\n" })
            {
                File.WriteAllText(evidence, incomplete);
                var pending = store.FailedTestRanking("Project", Now);
                Assert.Empty(pending.Tests);
                Assert.Equal(1, pending.PendingEvidenceRuns);
            }

            File.WriteAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Product.Tests.Unstable"]}""" + "\n");
            var recovered = store.FailedTestRanking("Project", Now);
            Assert.Equal(0, recovered.PendingEvidenceRuns);
            Assert.Equal("Product.Tests.Unstable", Assert.Single(recovered.Tests).TestName);
            Assert.Equal(0, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Completed_record_for_another_gate_keeps_execution_pending_until_its_own_record_arrives()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            File.WriteAllText(evidence,
                """{"GateRunId":"other-gate","TestedSha":"sha","RecordedFailedTests":[]}""" + "\n");
            var store = new BatchGateStore(root);
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "batch-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "own-gate"));

            var pending = store.FailedTestRanking("Project", Now);
            Assert.Empty(pending.Tests);
            Assert.Equal(1, pending.PendingEvidenceRuns);

            File.AppendAllText(evidence,
                """{"GateRunId":"own-gate","TestedSha":"other-sha","RecordedFailedTests":["Wrong.Test"]}""" + "\n");
            Assert.Equal(1, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);

            File.AppendAllText(evidence,
                """{"GateRunId":"own-gate","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Product.Tests.Unstable"]}""" + "\n");
            var recovered = store.FailedTestRanking("Project", Now);
            Assert.Equal(0, recovered.PendingEvidenceRuns);
            var failure = Assert.Single(Assert.Single(recovered.Tests).Failures);
            Assert.Equal("batch-run", failure.RunId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Legacy_execution_without_gate_id_is_permanently_unattributed()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            var store = new BatchGateStore(root);
            var fact = new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"]);
            File.WriteAllText(evidence,
                """{"GateRunId":"other-run","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Wrong.Test"]}""" + "\n");
            store.RecordExecution(fact);
            Assert.Equal(0, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);

            File.AppendAllText(evidence,
                """{"GateCompletedAtUtc":"2026-11-05T11:00:00Z","RecordedFailedTests":["Stale.Test"]}""" + "\n");
            Assert.Equal(0, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);

            File.AppendAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Right.Test"]}""" + "\n");
            var recovered = store.FailedTestRanking("Project", Now);
            Assert.Equal(0, recovered.PendingEvidenceRuns);
            Assert.Empty(recovered.Tests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Complete_evidence_with_no_failures_is_not_left_pending()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            File.WriteAllText(evidence, """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":[]}""" + "\n");
            var store = new BatchGateStore(root);
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "one-run", "sha", "pass", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run"));

            var report = store.FailedTestRanking("Project", Now);
            Assert.Empty(report.Tests);
            Assert.Equal(0, report.PendingEvidenceRuns);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Partial_inventory_does_not_hide_a_later_completed_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            File.WriteAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","RecordedFailedTests":[]}""" + "\n");
            var store = new BatchGateStore(root);
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run"));
            Assert.Equal(1, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);

            File.AppendAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Right.Test"]}""" + "\n");
            var recovered = store.FailedTestRanking("Project", Now);
            Assert.Equal(0, recovered.PendingEvidenceRuns);
            Assert.Equal("Right.Test", Assert.Single(recovered.Tests).TestName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Later_partial_records_do_not_hide_completed_evidence_for_the_same_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            File.WriteAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Right.Test"]}""" + "\n" +
                """{"GateRunId":"one-run","TestedSha":"sha","RecordedFailedTests":[]}""" + "\n" +
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z"}""" + "\n");
            var store = new BatchGateStore(root);
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run"));

            var report = store.FailedTestRanking("Project", Now);
            Assert.Equal(0, report.PendingEvidenceRuns);
            Assert.Equal("Right.Test", Assert.Single(report.Tests).TestName);
            Assert.Equal("one-run", Assert.Single(Assert.Single(report.Tests).Failures).RunId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Unidentified_nearby_run_cannot_fill_a_missing_execution()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            File.WriteAllText(evidence,
                """{"TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Wrong.Test"]}""" + "\n");
            var store = new BatchGateStore(root);
            store.RecordExecution(new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run"));
            Assert.Equal(1, store.FailedTestRanking("Project", Now).PendingEvidenceRuns);
            Assert.Empty(store.FailedTestRanking("Project", Now).Tests);

            File.AppendAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Right.Test"]}""" + "\n");
            Assert.Equal("Right.Test", Assert.Single(store.FailedTestRanking("Project", Now).Tests).TestName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Error_excerpt_is_only_taken_from_the_named_test()
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "gate-evidence.jsonl");
        try
        {
            File.WriteAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Tests.First","Tests.Second"],"Output":"Failed Tests.First [1 ms]\nError Message: first error\nFailed Tests.Second [1 ms]\nError Message: second error"}""" + "\n");
            var fact = new BatchGateExecutionFact("Project", "one", "one-run", "sha", "fail", "host",
                Now, Now, 0, 0, evidence, MemberKeys: ["AGT-1"], EvidenceGateRunId: "one-run");
            var failures = BatchGateFailureRanking.ReadEvidence(fact);
            var first = Assert.Single(failures, failure => failure.TestName == "Tests.First").ErrorExcerpt;
            Assert.Contains("first error", first);
            Assert.DoesNotContain("second error", first);
            Assert.Contains("second error", Assert.Single(failures, failure => failure.TestName == "Tests.Second").ErrorExcerpt);

            File.WriteAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Tests.First","Tests.Second"],"Output":"Failed Tests.First [1 ms]\nFailed Tests.Second [1 ms]\nError Message: second error"}""" + "\n");
            failures = BatchGateFailureRanking.ReadEvidence(fact);
            Assert.Equal("", Assert.Single(failures, failure => failure.TestName == "Tests.First").ErrorExcerpt);
            Assert.Contains("second error", Assert.Single(failures, failure => failure.TestName == "Tests.Second").ErrorExcerpt);

            File.WriteAllText(evidence,
                """{"GateRunId":"one-run","TestedSha":"sha","GateCompletedAtUtc":"2026-11-05T12:00:00Z","RecordedFailedTests":["Tests.First","Tests.Second"],"Processes":[{"StandardOutput":"Failed Tests.First [1 ms]"},{"StandardOutput":"Failed Tests.Second [1 ms]\nError Message: second process error"}]}""" + "\n");
            failures = BatchGateFailureRanking.ReadEvidence(fact);
            Assert.Equal("", Assert.Single(failures, failure => failure.TestName == "Tests.First").ErrorExcerpt);
            Assert.Contains("second process error", Assert.Single(failures, failure => failure.TestName == "Tests.Second").ErrorExcerpt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
