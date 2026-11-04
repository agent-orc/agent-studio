using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.Tests.Architecture;
using Microsoft.Extensions.Logging.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixture names and ReceivedAtUtc are stored metadata, never compared with the clock.

/// <summary>
/// AGT-3467: after Deploy 32 every card whose settlement journal a 58a576eab
/// backend wrote was refused with "no content verdict (requirement-fit); only
/// no aspects ran". The journal hashed the full serialization of the report
/// record, AGT-3430 added <c>outputTail</c> to every command, and the current
/// record serialized the old entry with <c>"outputTail":null</c>, so the
/// report hash no longer matched and the journal read as repair.
/// </summary>
public sealed class RemoteReviewSettlementJournalFormatTests : IDisposable
{
    /// <summary>Written by the 58a576eab backend for a report from a 58a576eab review host.</summary>
    private const string ReleaseFixture = "format-1-58a576eab-review_5a8a576e0b3d4c1fa9e2d7c6b5a40397.json";
    private const string ReleaseAttempt = "review_5a8a576e0b3d4c1fa9e2d7c6b5a40397";

    /// <summary>Recorded from a production card (AGT-3279) on 20 October 2026.</summary>
    private const string RecordedFixture = "recorded-2026-10-20-review_ac90d6599ee9427880fbce7283f4ddc8.json";
    private const string RecordedAttempt = "review_ac90d6599ee9427880fbce7283f4ddc8";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "settlement-format-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Current_record_no_longer_serializes_the_report_a_58a576eab_backend_hashed()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        var stored = JsonNode.Parse(File.ReadAllText(RemoteReviewSettlementJournal.PathFor(folder, ReleaseAttempt)))!;
        var report = stored["report"]!.Deserialize<Contract.ReviewReportRequest>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // The cause: the typed record carries a member the writing release did not know.
        Assert.DoesNotContain("outputTail", stored["report"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"outputTail\":null", JsonSerializer.Serialize(report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);
        Assert.Equal(RemoteReviewSettlementJournal.LegacyVersion, stored["version"]!.GetValue<int>());
    }

    [Fact]
    public void Format_1_entry_written_by_58a576eab_reads_ready_with_its_requirement_fit_pass()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);

        var read = RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt);

        Assert.Equal(RemoteReviewSettlementReadStatus.Ready, read.Status);
        Assert.Null(read.Reason);
        var content = Assert.Single(read.Entry!.Report.Verdicts, verdict => verdict.Aspect == "requirement-fit");
        Assert.Equal("pass", content.Status);
        Assert.NotNull(read.Entry.Delivery);
    }

    [Fact]
    public void Recorded_production_format_1_entry_reads_ready()
    {
        var folder = Seed(RecordedFixture, RecordedAttempt);

        var read = RemoteReviewSettlementJournal.Read(folder, RecordedAttempt);

        Assert.Equal(RemoteReviewSettlementReadStatus.Ready, read.Status);
        Assert.Contains(read.Entry!.Report.Verdicts, verdict => verdict.Aspect == "requirement-fit");
    }

    [Fact]
    public void Format_1_entry_with_a_changed_verdict_is_still_refused()
    {
        var folder = Seed(RecordedFixture, RecordedAttempt);
        Edit(folder, RecordedAttempt, stored =>
        {
            var verdict = stored["report"]!["verdicts"]!.AsArray()
                .First(item => (string?)item!["aspect"] == "requirement-fit")!;
            Assert.Equal("concerns", (string?)verdict["status"]);
            verdict["status"] = "pass";
        });

        var read = RemoteReviewSettlementJournal.Read(folder, RecordedAttempt);

        Assert.Equal(RemoteReviewSettlementReadStatus.Repair, read.Status);
        Assert.Equal("corrupt-review-settlement-journal:report-sha256-mismatch", read.Reason);
        Assert.Null(read.Entry);
    }

    [Fact]
    public void Format_1_entry_with_a_changed_delivery_is_still_refused()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        Edit(folder, ReleaseAttempt, stored => stored["delivery"]!["shouldIntegrate"] = false);

        var read = RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt);

        Assert.Equal(RemoteReviewSettlementReadStatus.Repair, read.Status);
        Assert.Equal("corrupt-review-settlement-journal:delivery-sha256-mismatch", read.Reason);
    }

    [Fact]
    public void Truncated_journal_is_unreadable()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        var path = RemoteReviewSettlementJournal.PathFor(folder, ReleaseAttempt);
        File.WriteAllText(path, File.ReadAllText(path)[..2000]);

        var read = RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt);

        Assert.Equal(RemoteReviewSettlementReadStatus.Repair, read.Status);
        Assert.Equal("unreadable-review-settlement-journal", read.Reason);
    }

    [Fact]
    public void Rewritten_format_1_entry_is_upgraded_and_stays_ready()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        var legacy = RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Entry!;
        Assert.Null(RemoteReviewSettlementJournal.SubmittedReportHash(legacy));
        Assert.Equal(RemoteReviewSettlementJournal.Hash(legacy.Report), RemoteReviewSettlementJournal.ReportHash(legacy));

        RemoteReviewSettlementJournal.Write(folder, legacy with { EvidenceFailures = 1 });
        var read = RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt);

        Assert.Equal(RemoteReviewSettlementReadStatus.Ready, read.Status);
        Assert.Equal(RemoteReviewSettlementJournal.CurrentVersion, read.Entry!.Version);
        Assert.Equal(RemoteReviewSettlementJournal.Hash(legacy.Report),
            RemoteReviewSettlementJournal.ReportHash(read.Entry));
        Assert.Null(read.Entry.SubmittedReportSha256);
        Assert.Equal(1, read.Entry.EvidenceFailures);
    }

    [Fact]
    public void Current_hash_is_stable_when_a_release_adds_members_at_their_default()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        RemoteReviewSettlementJournal.Write(folder, RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Entry!);
        Edit(folder, ReleaseAttempt, stored =>
        {
            stored["report"]!["futureSummary"] = null;
            stored["report"]!["futureFlag"] = false;
            stored["report"]!["commands"]![0]!["futureCount"] = 0;
            stored["delivery"]!["futureDetail"] = null;
        });

        Assert.Equal(RemoteReviewSettlementReadStatus.Ready,
            RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Status);
    }

    [Fact]
    public void Current_entry_with_a_set_member_is_refused()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        RemoteReviewSettlementJournal.Write(folder, RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Entry!);
        Edit(folder, ReleaseAttempt, stored => stored["report"]!["artifactsReduced"] = true);

        var read = RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt);

        Assert.Equal(RemoteReviewSettlementReadStatus.Repair, read.Status);
        Assert.Equal("corrupt-review-settlement-journal:report-sha256-mismatch", read.Reason);
    }

    [Fact]
    public void Format_2_entry_still_verifies_after_format_3_is_current()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        RemoteReviewSettlementJournal.Write(folder, RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Entry!);
        var compact = RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Entry!;
        Edit(folder, ReleaseAttempt, stored =>
        {
            stored["version"] = RemoteReviewSettlementJournal.CanonicalVersion;
            stored["reportSha256"] = RemoteReviewSettlementJournal.Hash(compact.Report);
            stored.Remove("acceptedReportSha256");
        });

        Assert.Equal(RemoteReviewSettlementReadStatus.Ready,
            RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Status);
        Edit(folder, ReleaseAttempt, stored => stored["report"]!["outcome"] = "ProductFailure");
        Assert.Equal(RemoteReviewSettlementReadStatus.Repair,
            RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Status);
    }

    [Fact]
    public void Unknown_format_is_refused()
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        Edit(folder, ReleaseAttempt, stored => stored["version"] = 4);

        Assert.Equal("corrupt-review-settlement-journal:unsupported-format-4",
            RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Reason);
    }

    [Fact]
    public void Large_artifacts_leave_a_bounded_journal_with_failed_step_tails()
    {
        var (task, _, attemptId) = SettledReview();
        var original = RemoteReviewSettlementJournal.Read(task.FolderPath, attemptId).Entry!;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024));
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var command = new Contract.ReviewCommandEvidenceDto(
            "verify", "build-tests", "dotnet", ["test"], "expected", "head", "tree",
            new DateTime(2026, 11, 3, 11, 3, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 3, 11, 4, 0, DateTimeKind.Utc),
            1, null, digest, digest);
        var report = original.Report with
        {
            Commands = [command],
            Artifacts = [new Contract.ReviewArtifactEvidenceDto("stdout", "text/plain", digest,
                bytes.Length, Convert.ToBase64String(bytes))],
        };
        RemoteReviewSettlementJournal.Write(task.FolderPath, original with
        {
            Report = report,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            AcceptedReportSha256 = null,
        });

        var path = RemoteReviewSettlementJournal.PathFor(task.FolderPath, attemptId);
        Assert.InRange(new FileInfo(path).Length, 1, RemoteReviewSettlementJournal.MaximumFileBytes);
        var read = RemoteReviewSettlementJournal.Read(task.FolderPath, attemptId);
        Assert.Equal(RemoteReviewSettlementReadStatus.Ready, read.Status);
        Assert.Null(Assert.Single(read.Entry!.Report.Artifacts).ContentBase64);
        var tail = Assert.Single(read.Entry.Report.Commands).OutputTail!;
        Assert.InRange(Encoding.UTF8.GetByteCount(tail.Stdout) + Encoding.UTF8.GetByteCount(tail.Stderr),
            1, RemoteReviewSettlementJournal.MaximumStepTailBytes);
        Assert.True(tail.StdoutTruncated);
        Assert.True(tail.StderrTruncated);
        Assert.Equal(RemoteReviewSettlementJournal.Hash(report),
            RemoteReviewSettlementJournal.ReportHash(read.Entry));
        File.AppendAllText(path, new string(' ', RemoteReviewSettlementJournal.MaximumFileBytes));
        Assert.Equal("corrupt-review-settlement-journal:journal-size-limit",
            RemoteReviewSettlementJournal.Read(task.FolderPath, attemptId).Reason);
    }

    [Theory]
    [InlineData("reportSha256")]
    [InlineData("deliverySha256")]
    [InlineData("reviewBudgetSha256")]
    [InlineData("continuationSha256")]
    [InlineData("acceptedReportSha256")]
    public void Current_entry_refuses_tampered_hash_fields(string field)
    {
        var folder = Seed(ReleaseFixture, ReleaseAttempt);
        RemoteReviewSettlementJournal.Write(folder, RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Entry!);
        Edit(folder, ReleaseAttempt, stored => stored[field] = new string('f', 64));
        Assert.Equal(RemoteReviewSettlementReadStatus.Repair,
            RemoteReviewSettlementJournal.Read(folder, ReleaseAttempt).Status);
    }

    [Fact]
    public void Unbounded_metadata_is_refused_without_replacing_a_valid_journal()
    {
        var (task, _, attemptId) = SettledReview();
        var path = RemoteReviewSettlementJournal.PathFor(task.FolderPath, attemptId);
        var before = File.ReadAllText(path);
        var entry = RemoteReviewSettlementJournal.Read(task.FolderPath, attemptId).Entry!;
        var report = entry.Report with { Summary = new string('a', RemoteReviewSettlementJournal.MaximumFileBytes) };

        Assert.Throws<IOException>(() => RemoteReviewSettlementJournal.Write(task.FolderPath,
            entry with { Report = report, AcceptedReportSha256 = RemoteReviewSettlementJournal.Hash(report) }));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Completion_names_a_settlement_that_needs_repair()
    {
        var (task, authority, attemptId) = SettledReview();
        var path = RemoteReviewSettlementJournal.PathFor(task.FolderPath, attemptId);
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "\"status\": \"pass\"", "\"status\": \"block\"", StringComparison.Ordinal));

        var facts = CompletionContentEvidence.Read(task, authority);
        var decision = CompletionContentPolicy.Decide(facts);

        Assert.Null(facts.ContentStatus);
        Assert.Equal(CompletionSettlementProblem.JournalRepair, facts.Settlement!.Kind);
        Assert.Equal(attemptId, facts.Settlement.ReviewAttemptId);
        Assert.False(decision.Accepted);
        Assert.Contains($"the settlement of review {attemptId} needs repair "
            + "(corrupt-review-settlement-journal:report-sha256-mismatch)", decision.Message);
        Assert.DoesNotContain("only no aspects ran", decision.Message);
    }

    [Fact]
    public void Completion_names_a_missing_settlement()
    {
        var (task, authority, attemptId) = SettledReview();
        RemoteReviewSettlementJournal.Release(task.FolderPath, attemptId);

        var facts = CompletionContentEvidence.Read(task, authority);

        Assert.Equal(CompletionSettlementProblem.JournalMissing, facts.Settlement!.Kind);
        Assert.Contains($"the settlement of review {attemptId} is missing",
            CompletionContentPolicy.Decide(facts).Message);
    }

    [Fact]
    public void Completion_reads_the_content_verdict_of_a_ready_settlement()
    {
        var (task, authority, _) = SettledReview();

        var facts = CompletionContentEvidence.Read(task, authority);

        Assert.Equal("pass", facts.ContentStatus);
        Assert.Null(facts.Settlement);
    }

    [Fact]
    public void Repair_diagnostic_is_written_once_per_card_and_review()
    {
        var folder = Path.Combine(_root, "diagnostic");
        Directory.CreateDirectory(folder);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);

        Assert.True(RemoteReviewSettlementDiagnostics.RecordRepairOnce(timeline, folder, ReleaseAttempt,
            "corrupt-review-settlement-journal:report-sha256-mismatch"));
        Assert.False(RemoteReviewSettlementDiagnostics.RecordRepairOnce(timeline, folder, ReleaseAttempt,
            "corrupt-review-settlement-journal:report-sha256-mismatch"));
        Assert.True(RemoteReviewSettlementDiagnostics.RecordRepairOnce(timeline, folder, RecordedAttempt, null));

        var entries = timeline.ReadAll(folder)
            .Where(entry => entry.Kind == TimelineEventKinds.ReviewSettlementRepairRequired).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("corrupt-review-settlement-journal:report-sha256-mismatch", entries[0].Details!["reason"]);
        Assert.Equal(ReleaseAttempt, entries[0].Details!["reviewAttemptId"]);
    }

    private (TaskInfo Task, AttemptAuthorityService Authority, string AttemptId) SettledReview()
    {
        const string taskKey = "AGT-3467-FIXTURE";
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var folder = Path.Combine(_root, "card");
        Directory.CreateDirectory(folder);
        var brief = BriefVersionStore.ReadOrCreate(folder);
        var authority = new AttemptAuthorityService(NullLogger<AttemptAuthorityService>.Instance);
        var run = authority.AcquireRun(taskKey, "PROJ-FIXTURE", null, "fixture-runner", "fixture-host", 60,
            "claim-run", briefVersion: brief).RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-run"),
            Outcome = "done",
            ResultSha = sha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope("PROJ-FIXTURE", run.AttemptId,
                "fedcba9876543210fedcba9876543210fedcba98", sha, "refs/heads/agt-3467-fixture", null,
                new string('a', 64)),
        }).Accepted);
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, "PROJ-FIXTURE", sha, run.AttemptId, brief, AttemptAuthorityService.Hash("policy"), [],
            "create-review"));
        Assert.True(created.Accepted, created.Message);
        var review = authority.ClaimReview(created.AttemptId, "fixture-reviewer", "fixture-host", 60,
            "claim-review").ReviewAttempt!;
        var report = new Contract.ReviewReportRequest(
            "fixture-reviewer", "fixture-instance", review.Lease!.LeaseId, review.LastFence,
            "settle-review", "Pass", null, "Content fits the brief.",
            new Contract.ReviewWorkspaceProofDto("PROJ-FIXTURE", sha, sha, "fixture-tree", false, false,
                "fixture-workspace", "fixture-namespace"),
            new Contract.ReviewEnvironmentDto("fixture-host", "fixture-reviewer", "fixture-instance",
                "fixture-os", "fixture-arch", "fixture-runtime",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], [new Contract.ReviewVerdictDto("requirement-fit", "pass", "Product",
                "The fixture delivery answers its brief.")],
            AuthorityEpoch: review.AuthorityEpoch, BriefSha256: brief);
        RemoteReviewSettlementJournal.Write(folder, new RemoteReviewSettlementEntry
        {
            AttemptId = review.AttemptId,
            TaskKey = taskKey,
            IdempotencyKey = report.IdempotencyKey,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
            Report = report,
            ReceivedAtUtc = new DateTime(2026, 11, 3, 11, 4, 0, DateTimeKind.Utc),
        });
        var settled = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(review.AttemptId, review.LastFence, review.AuthorityEpoch,
                report.IdempotencyKey), sha, ReviewTerminalOutcome.Pass));
        Assert.True(settled.Accepted, settled.Message);
        return (new TaskInfo { Id = taskKey, TaskKey = taskKey, Key = taskKey, FolderPath = folder },
            authority, review.AttemptId);
    }

    private string Seed(string fixture, string attemptId)
    {
        var folder = Path.Combine(_root, attemptId);
        var path = RemoteReviewSettlementJournal.PathFor(folder, attemptId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(CSharpSourceScanner.RepoRoot(), "backend.Tests", "Fixtures", "review-settlement", fixture), path);
        return folder;
    }

    private static void Edit(string folder, string attemptId, Action<JsonObject> edit)
    {
        var path = RemoteReviewSettlementJournal.PathFor(folder, attemptId);
        var stored = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(stored);
        File.WriteAllText(path, stored.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
