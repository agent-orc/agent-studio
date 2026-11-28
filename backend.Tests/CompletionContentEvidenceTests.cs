using System.Text.Json;
using AgentStudio.Tasks;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;
// clock-independent: fixed review receipt dates and injected authority time never meet real time.
public sealed class CompletionContentEvidenceTests : IDisposable
{
    private CompletionContentEvidence.LocalReviewEvidence? _reviewedEvidence;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "completion-lineage-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime ReviewedAt = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);
    private readonly AttemptAuthorityService _authority;
    private readonly TaskInfo _card;

    public CompletionContentEvidenceTests()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        _authority = new(config, NullLogger<AttemptAuthorityService>.Instance, () => ReviewedAt);
        BriefVersionStore.Record(_root, "Deliver the bounded slice.");
        _card = new TaskInfo { Id = "slice", Key = "AGT-3991", FolderPath = _root, Mode = "coding",
            AcceptanceScope = new() { DeliveryMode = TaskAcceptanceDeliveryModes.BoundedSlice,
                Slice = "Show the concern", Criteria = ["The card names its concern."] } };
    }

    [Fact]
    public void Superseded_delivery_concerns_do_not_block_the_current_bounded_slice()
    {
        var oldRun = Run("old");
        Review(oldRun, "concerns", "The earlier slice omitted the concern.", "old");
        var currentRun = Run("current");
        var currentReview = Review(currentRun, "pass", "This slice meets its criteria.", "current");

        var facts = CompletionContentEvidence.Read(_card, _authority);
        Assert.Equal("pass", facts.ContentStatus);
        Assert.Equal("This slice meets its criteria.", facts.ContentSummary);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        var json = JsonSerializer.SerializeToElement(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(currentReview, json.GetProperty("reviewAttemptId").GetString());
    }

    [Fact]
    public void Older_review_of_same_delivery_is_ignored_when_current_review_passes()
    {
        var run = Run("one");
        Review(run, "concerns", "Earlier review concern.", "first");
        Review(run, "pass", "Current review passes.", "second");
        var facts = CompletionContentEvidence.Read(_card, _authority);
        Assert.Equal("pass", facts.ContentStatus);
        Assert.Equal("Current review passes.", facts.ContentSummary);
    }

    [Fact]
    public void Pass_outcome_can_carry_bounded_slice_concerns_and_refusal_names_the_source()
    {
        var run = Run("one");
        var attempt = Review(run, "concerns", "The card hides the concern text.", "one");
        Assert.Equal(ReviewTerminalOutcome.Pass, _authority.GetTaskProjection(_card.Key!).CurrentReviewAttempt!.Outcome);
        var facts = CompletionContentEvidence.Read(_card, _authority);
        var decision = CompletionContentPolicy.Decide(facts);
        Assert.False(decision.Accepted);
        var followUp = AgentStudio.OrchestrationDecisions.ReviewFollowUpPolicy.Decide(
            [new Contract.ReviewFollowUpFinding("requirement-fit", facts.ContentStatus, facts.ContentSummary)], 0, 0);
        Assert.Equal(Contract.ReviewGrade.PassWithConcerns, followUp.Grade);
        Assert.Equal(Contract.ReviewFollowUpAction.Accept, followUp.Action);
        Assert.Contains("The card hides the concern text.", decision.Message);
        Assert.Contains(attempt, decision.Message);
        Assert.Contains("2026-11-26", decision.Message);
        Assert.True(CompletionContentPolicy.Decide(facts with { OperatorOverride = true,
            OverrideReason = "Manually checked this bounded slice." }).Accepted);
    }

    [Fact]
    public void New_delivery_without_review_never_falls_back_to_old_concerns_or_local_artifact()
    {
        Review(Run("old"), "concerns", "Old concern.", "old");
        CompletionContentFixture.RecordPassedReview(_root);
        Run("new");
        var facts = CompletionContentEvidence.Read(_card, _authority);
        Assert.Null(facts.ContentStatus);
        Assert.Null(facts.ContentSummary);
        Assert.False(CompletionContentPolicy.Decide(facts).Accepted);
    }

    [Fact]
    public void Local_concerns_keep_the_recorded_date_and_missing_evidence()
    {
        var version = BriefVersionStore.ReadCurrent(_root);
        CompletionContentEvidence.WriteLocalContext(_root, version, null, ReviewedAt);
        File.WriteAllText(Path.Combine(_root, "aspect-requirement-fit.json"), JsonSerializer.Serialize(new {
            status = "concerns", summary = "The slice has no cited example.", missing = "A concrete example.",
        }));
        var facts = CompletionContentEvidence.Read(_card, null);
        Assert.Equal("A concrete example.", facts.Missing);
        var json = JsonSerializer.SerializeToElement(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(ReviewedAt, json.GetProperty("reviewedAtUtc").GetDateTime());
        Assert.Contains("The slice has no cited example.", CompletionContentPolicy.Decide(facts).Message);
    }

    [Theory]
    [InlineData("Consider clearer wording.", 0, "advisory")]
    [InlineData("backend/Feature.cs fails to name the optional diagnostic.", 1, "concern-budget-accepted")]
    public void Final_local_budget_admission_completes_and_keeps_the_original_finding(
        string summary, int used, string disposition)
    {
        var budget = LocalAdmission(summary, used);
        Assert.Equal(Contract.ReviewFollowUpAction.Accept, budget.FollowUp.Action);
        var facts = CompletionContentEvidence.Read(_card, _authority);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.Equal("local-review-1", facts.ReviewAttemptId);
        Assert.Equal(ReviewedAt, facts.ReviewedAtUtc);
        Assert.Equal("A concrete example.", facts.Missing);
        Assert.Contains(summary, facts.ContentSummary);
        Assert.Contains(disposition, facts.ContentSummary);
        Assert.Contains("original concerns", facts.ContentSummary);
        Assert.Contains("local-content-admission.json", facts.ContentSummary);
        Assert.Contains(new string('a', 40), facts.ContentSummary);
        using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "aspect-requirement-fit.json")));
        Assert.Equal("concerns", original.RootElement.GetProperty("status").GetString());
        Assert.False(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim {
            CurrentBriefVersion = facts.CurrentBriefVersion, DeliveryBriefVersion = facts.DeliveryBriefVersion,
            ReviewBriefVersion = facts.ReviewBriefVersion, ContentAspect = facts.RequiredAspect,
            ContentStatus = facts.ContentStatus, ContentSummary = facts.ContentSummary,
        }, "coding"));
    }

    [Theory]
    [InlineData("delivery")]
    [InlineData("same-sha-new-run")]
    [InlineData("brief")]
    [InlineData("review")]
    [InlineData("finding")]
    [InlineData("receipt")]
    public void Local_admission_cannot_be_reused_after_its_identity_changes(string change)
    {
        LocalAdmission("Consider clearer wording.");
        switch (change)
        {
            case "delivery": LocalDelivery(new string('b', 40)); break;
            case "same-sha-new-run": LocalDelivery(new string('a', 40), ReviewedAt.AddHours(1)); break;
            case "brief": BriefVersionStore.Record(_root, "A different brief."); break;
            case "review": CompletionContentEvidence.WriteLocalContext(_root,
                BriefVersionStore.ReadCurrent(_root), null, ReviewedAt.AddHours(1)); break;
            case "finding": File.WriteAllText(Path.Combine(_root, "aspect-requirement-fit.json"),
                JsonSerializer.Serialize(new { status = "concerns", summary = "A later finding." })); break;
            case "receipt": File.WriteAllText(Path.Combine(_root, "local-content-admission.json"), "null"); break;
        }
        var facts = CompletionContentEvidence.Read(_card, null);
        Assert.False(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.NotNull(facts.AdmissionProblem);
    }

    [Theory]
    [InlineData("concerns")]
    [InlineData("pass")]
    public void Local_transfer_requires_the_follow_up_for_its_exact_review(string status)
    {
        var budget = LocalAdmission("backend/Feature.cs fails to name the optional diagnostic.",
            used: 0, maximum: 1, qualityBlock: status == "pass", status: status);
        Assert.True(budget.ShouldDegrade);
        var refusal = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null));
        Assert.False(refusal.Accepted);
        Assert.Contains("still owes its linked follow-up", refusal.Message);
        ReviewRoundBudgetStore.Record(_root, AgentStudio.OrchestrationDecisions.ReviewRoundBudgetLedger.Empty,
            new("older-review", [], [], FollowUpTaskKey: "AGT-OLD"));
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);
        ReviewRoundBudgetStore.MarkFollowUp(_root, "local-review-1", "AGT-FOLLOW-UP");
        var facts = CompletionContentEvidence.Read(_card, null);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.Contains("AGT-FOLLOW-UP", facts.ContentSummary);
        Assert.DoesNotContain("AGT-OLD", facts.ContentSummary);
    }

    [Theory]
    [InlineData("requirement-fit")]
    [InlineData("concept-fit")]
    public void Exhausted_local_budgets_preserve_blocking_content(string aspect)
    {
        var budget = LocalAdmission("backend/Feature.cs fails the required behavior.", 1,
            status: "block", aspect: aspect);
        Assert.False(budget.ShouldDegrade);
        var facts = CompletionContentEvidence.Read(_card with {
            Mode = aspect == "concept-fit" ? "concept" : "coding",
        }, null);
        Assert.Equal("block", facts.ContentStatus);
        Assert.False(CompletionContentPolicy.Decide(facts).Accepted);
    }

    [Fact]
    public void New_local_run_invalidates_the_previous_admission()
    {
        LocalAdmission("Consider clearer wording.");
        CompletionContentEvidence.StampLocalRun(_root);
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty-dispositions")]
    public void Lost_or_incomplete_local_receipt_cannot_bypass_transfer_of_a_literal_pass(string damage)
    {
        LocalAdmission("The content meets the brief.", qualityBlock: true, status: "pass");
        var path = Path.Combine(_root, "local-content-admission.json");
        if (damage == "missing") File.Delete(path);
        else
        {
            var receipt = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            receipt["dispositions"] = new System.Text.Json.Nodes.JsonArray();
            File.WriteAllText(path, receipt.ToJsonString());
        }
        var facts = CompletionContentEvidence.Read(_card, null);
        Assert.NotNull(facts.AdmissionProblem);
        Assert.False(CompletionContentPolicy.Decide(facts).Accepted);
    }

    [Theory]
    [InlineData("context", "io", "concerns")]
    [InlineData("context", "access", "concerns")]
    [InlineData("context", "json", "concerns")]
    [InlineData("receipt", "io", "concerns")]
    [InlineData("receipt", "access", "concerns")]
    [InlineData("receipt", "json", "concerns")]
    [InlineData("receipt", "io", "pass")]
    public void Failed_local_admission_write_is_reported_and_can_be_retried(
        string stage, string failure, string status)
    {
        var budget = LocalAdmission("Consider clearer wording.", qualityBlock: status == "pass",
            status: status, writeAdmission: false);
        var findingPath = Path.Combine(_root, "aspect-requirement-fit.json");
        var originalFinding = File.ReadAllText(findingPath);
        var expected = failure switch {
            "access" => (Exception)new UnauthorizedAccessException("Injected access failure."),
            "json" => new JsonException("Injected serialization failure."),
            _ => new IOException("Injected write failure."),
        };
        var writer = new FailingAdmissionWriter(Path.Combine(_root, stage == "context"
            ? CompletionContentEvidence.LocalContextFile : CompletionContentEvidence.LocalAdmissionFile), expected);
        // Reproduce the original unguarded call before exercising its recovery boundary.
        Assert.Same(expected, Record.Exception(() => CompletionContentEvidence.WriteLocalAdmission(
            _root, "local-review-1", new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, writer)));

        var logger = new AdmissionLogger();
        Assert.False(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, logger, writer));
        var error = Assert.Single(logger.Errors);
        Assert.Same(expected, error.Exception);
        Assert.Contains("local-content-admission-record-failed", error.Message);
        Assert.Contains("local-review-1", error.Message);
        Assert.Contains(_root, error.Message);
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);
        Assert.Equal(originalFinding, File.ReadAllText(findingPath));

        Assert.True(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, logger));
        if (status == "pass")
        {
            Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);
            ReviewRoundBudgetStore.MarkFollowUp(_root, "local-review-1", "AGT-FOLLOW-UP");
        }
        var facts = CompletionContentEvidence.Read(_card, null);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.Equal("local-review-1", facts.ReviewAttemptId);
        Assert.Contains(CompletionContentEvidence.LocalAdmissionFile, facts.ContentSummary);
        Assert.Equal(originalFinding, File.ReadAllText(findingPath));
    }

    [Theory]
    [InlineData("null-dispositions")]
    [InlineData("empty-dispositions")]
    [InlineData("missing-sha")]
    [InlineData("blank-sha")]
    [InlineData("missing-delivery")]
    [InlineData("different-delivery")]
    [InlineData("same-sha-new-run")]
    [InlineData("replaced-review-context")]
    [InlineData("missing-snapshot")]
    [InlineData("malformed-snapshot-context")]
    [InlineData("missing-context")]
    [InlineData("missing-finding")]
    [InlineData("malformed-context")]
    [InlineData("malformed-finding")]
    public void Skipped_local_admission_write_keeps_promotion_pending_until_evidence_is_repaired(string damage)
    {
        var budget = LocalAdmission("Consider clearer wording.", writeAdmission: false);
        var contextPath = Path.Combine(_root, CompletionContentEvidence.LocalContextFile);
        var findingPath = Path.Combine(_root, "aspect-requirement-fit.json");
        var context = File.ReadAllText(contextPath);
        var finding = File.ReadAllText(findingPath);
        var dispositions = budget.RoundBudget.ContentAdmissions;
        var reviewedEvidence = _reviewedEvidence;
        string? sha = new string('a', 40);
        switch (damage)
        {
            case "null-dispositions": dispositions = null; break;
            case "empty-dispositions": dispositions = []; break;
            case "missing-sha": sha = null; break;
            case "blank-sha": sha = " "; break;
            case "missing-delivery": File.Delete(TaskPaths.SessionEventsLog(_root)); break;
            case "different-delivery": LocalDelivery(new string('b', 40)); break;
            case "same-sha-new-run": LocalDelivery(new string('a', 40), ReviewedAt.AddHours(1)); break;
            case "replaced-review-context": CompletionContentEvidence.WriteLocalContext(_root,
                BriefVersionStore.ReadCurrent(_root), null, ReviewedAt.AddHours(1)); break;
            case "missing-snapshot": reviewedEvidence = null; break;
            case "malformed-snapshot-context": reviewedEvidence = reviewedEvidence! with { ContextJson = "[]" }; break;
            case "missing-context": File.Delete(contextPath); break;
            case "missing-finding": File.Delete(findingPath); break;
            case "malformed-context": File.WriteAllText(contextPath, "null"); break;
            case "malformed-finding": File.WriteAllText(findingPath, "null"); break;
        }

        var logger = new AdmissionLogger();
        // This is the promotion boundary used immediately before GuardedMoveJob.
        Assert.False(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            sha, dispositions, reviewedEvidence, logger));
        Assert.Contains("local-content-admission-record-failed", Assert.Single(logger.Errors).Message);
        Assert.False(File.Exists(Path.Combine(_root, CompletionContentEvidence.LocalAdmissionFile)));
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);

        LocalDelivery(new string('a', 40));
        File.WriteAllText(contextPath, context);
        File.WriteAllText(findingPath, finding);
        Assert.True(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, logger));
        Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);
        Assert.Equal(finding, File.ReadAllText(findingPath));
    }

    [Theory]
    [InlineData("requirement-fit", "concerns", "pass")]
    [InlineData("requirement-fit", "concerns", "block")]
    [InlineData("requirement-fit", "pass", "concerns")]
    [InlineData("concept-fit", "concerns", "block")]
    public void Mismatched_local_finding_keeps_promotion_pending_until_the_review_is_repaired(
        string aspect, string reviewedStatus, string savedStatus)
    {
        var budget = LocalAdmission("Consider clearer wording.", status: reviewedStatus,
            aspect: aspect, writeAdmission: false);
        var contextPath = Path.Combine(_root, CompletionContentEvidence.LocalContextFile);
        var context = File.ReadAllText(contextPath);
        var findingPath = Path.Combine(_root, $"aspect-{aspect}.json");
        var reviewedFinding = File.ReadAllText(findingPath);
        var finding = System.Text.Json.Nodes.JsonNode.Parse(reviewedFinding)!;
        finding["status"] = savedStatus;
        File.WriteAllText(findingPath, finding.ToJsonString());
        var mismatchedFinding = File.ReadAllText(findingPath);
        var logger = new AdmissionLogger();

        Assert.False(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, logger));
        Assert.Contains("original finding", Assert.Single(logger.Errors).Exception!.Message);
        Assert.Equal(context, File.ReadAllText(contextPath));
        Assert.Equal(mismatchedFinding, File.ReadAllText(findingPath));
        Assert.False(File.Exists(Path.Combine(_root, CompletionContentEvidence.LocalAdmissionFile)));

        File.WriteAllText(findingPath, reviewedFinding);
        Assert.True(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, logger));
        Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card with {
            Mode = aspect == "concept-fit" ? "concept" : "coding",
        }, null)).Accepted);
        Assert.Equal(reviewedFinding, File.ReadAllText(findingPath));
    }

    [Theory]
    [InlineData("requirement-fit", "summary")]
    [InlineData("requirement-fit", "details")]
    [InlineData("requirement-fit", "missing")]
    [InlineData("requirement-fit", "evidenceChecked")]
    [InlineData("concept-fit", "summary")]
    public void Replacement_local_finding_with_same_status_cannot_inherit_the_reviewed_admission(
        string aspect, string changedField)
    {
        var budget = LocalAdmission("Consider clearer wording.", aspect: aspect, writeAdmission: false);
        var contextPath = Path.Combine(_root, CompletionContentEvidence.LocalContextFile);
        var context = File.ReadAllText(contextPath);
        var findingPath = Path.Combine(_root, $"aspect-{aspect}.json");
        var original = File.ReadAllText(findingPath);
        var replacement = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        replacement[changedField] = "A different finding needs a separate review.";
        File.WriteAllText(findingPath, replacement.ToJsonString());

        Assert.False(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, new AdmissionLogger()));
        Assert.Equal(context, File.ReadAllText(contextPath));
        Assert.False(File.Exists(Path.Combine(_root, CompletionContentEvidence.LocalAdmissionFile)));
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card with {
            Mode = aspect == "concept-fit" ? "concept" : "coding",
        }, null)).Accepted);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("details")]
    [InlineData("evidenceChecked")]
    public void Local_snapshot_rejects_artifacts_that_do_not_match_the_returned_verdict(string changedField)
    {
        LocalAdmission("Consider clearer wording.", writeAdmission: false);
        var path = Path.Combine(_root, "aspect-requirement-fit.json");
        var replacement = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        replacement[changedField] = "A different finding needs a separate review.";
        File.WriteAllText(path, replacement.ToJsonString());

        Assert.Null(CompletionContentEvidence.CaptureLocalReviewEvidence(_root, [new AspectVerdict(
            "requirement-fit", AspectStatus.Concerns, "Consider clearer wording.", "Consider clearer wording.", null)
            { EvidenceChecked = "The bounded implementation." }]));
        Assert.False(File.Exists(Path.Combine(_root, CompletionContentEvidence.LocalAdmissionFile)));
    }

    [Fact]
    public void Finding_replaced_during_the_receipt_write_keeps_promotion_pending_and_retry_uses_the_original()
    {
        var budget = LocalAdmission("Consider clearer wording.", writeAdmission: false);
        var path = Path.Combine(_root, "aspect-requirement-fit.json");
        var original = File.ReadAllText(path);
        var logger = new AdmissionLogger();
        var writer = new ReplacingFindingWriter(path);

        Assert.False(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, logger, writer));
        Assert.Contains("changed while recording", Assert.Single(logger.Errors).Exception!.Message);
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);

        File.WriteAllText(path, original);
        Assert.True(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), budget.RoundBudget.ContentAdmissions, _reviewedEvidence, logger));
        Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_card, null)).Accepted);
        Assert.Equal(original, File.ReadAllText(path));
    }

    private sealed class ReplacingFindingWriter(string findingPath) : AgentStudio.Persistence.IAtomicJsonFileWriter
    {
        public void Write(string path, string content) => ReplaceExisting(path, content);
        public void ReplaceExisting(string path, string content)
        {
            new AgentStudio.Persistence.AtomicJsonFileWriter().ReplaceExisting(path, content);
            if (Path.GetFileName(path) != CompletionContentEvidence.LocalContextFile) return;
            var finding = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(findingPath))!;
            finding["summary"] = "A replacement concern raised during the write.";
            File.WriteAllText(findingPath, finding.ToJsonString());
        }
    }

    [Fact]
    public void Local_admission_validates_every_content_finding_before_writing()
    {
        var budget = LocalAdmission("Consider clearer wording.", writeAdmission: false);
        File.WriteAllText(Path.Combine(_root, "aspect-concept-fit.json"),
            JsonSerializer.Serialize(new { status = "block", summary = "Required concept missing." }));
        var dispositions = budget.RoundBudget.ContentAdmissions!.Concat([
            new AgentStudio.OrchestrationDecisions.ReviewContentAdmission("concept-fit", "concerns",
                "advisory", "Consider clearer wording."),
        ]).ToArray();
        var contextPath = Path.Combine(_root, CompletionContentEvidence.LocalContextFile);
        var context = File.ReadAllText(contextPath);

        Assert.False(CompletionContentEvidence.TryWriteLocalAdmission(_root, "local-review-1",
            new string('a', 40), dispositions, _reviewedEvidence, new AdmissionLogger()));
        Assert.Equal(context, File.ReadAllText(contextPath));
        Assert.False(File.Exists(Path.Combine(_root, CompletionContentEvidence.LocalAdmissionFile)));
    }

    [Theory]
    [InlineData("stale-brief")]
    [InlineData("missing-brief")]
    [InlineData("blank-review")]
    public void Local_admission_requires_current_brief_and_review_identity_before_writing(string damage)
    {
        var budget = LocalAdmission("Consider clearer wording.", writeAdmission: false);
        if (damage == "stale-brief") BriefVersionStore.Record(_root, "A different brief.");
        if (damage == "missing-brief")
            CompletionContentEvidence.WriteLocalContext(_root, null, null, ReviewedAt);
        var contextPath = Path.Combine(_root, CompletionContentEvidence.LocalContextFile);
        var context = File.ReadAllText(contextPath);

        Assert.False(CompletionContentEvidence.TryWriteLocalAdmission(_root,
            damage == "blank-review" ? " " : "local-review-1", new string('a', 40),
            budget.RoundBudget.ContentAdmissions, _reviewedEvidence, new AdmissionLogger()));
        Assert.Equal(context, File.ReadAllText(contextPath));
        Assert.False(File.Exists(Path.Combine(_root, CompletionContentEvidence.LocalAdmissionFile)));
    }

    private sealed class FailingAdmissionWriter(string failedPath, Exception failure)
        : AgentStudio.Persistence.IAtomicJsonFileWriter
    {
        public void Write(string path, string content) => ReplaceExisting(path, content);

        public void ReplaceExisting(string path, string content)
        {
            if (path == failedPath) throw failure;
            new AgentStudio.Persistence.AtomicJsonFileWriter().ReplaceExisting(path, content);
        }
    }

    private sealed class AdmissionLogger : ILogger
    {
        public List<(Exception? Exception, string Message)> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Error) Errors.Add((exception, formatter(state, exception)));
        }
    }

    private AgentStudio.OrchestrationDecisions.ReviewBudgetDecision LocalAdmission(string summary,
        int used = 0, int maximum = 0, bool qualityBlock = false,
        string status = "concerns", string aspect = "requirement-fit", bool writeAdmission = true)
    {
        LocalDelivery(new string('a', 40));
        CompletionContentEvidence.StampLocalRun(_root);
        CompletionContentEvidence.WriteLocalContext(_root, BriefVersionStore.ReadCurrent(_root), null, ReviewedAt);
        File.WriteAllText(Path.Combine(_root, $"aspect-{aspect}.json"), JsonSerializer.Serialize(new {
            aspect, status, summary, details = summary,
            evidenceChecked = "The bounded implementation.", missing = "A concrete example.",
        }));
        _reviewedEvidence = CompletionContentEvidence.CaptureLocalReviewEvidence(_root, [new AspectVerdict(
            aspect, status == "pass" ? AspectStatus.Pass : status == "block" ? AspectStatus.Block : AspectStatus.Concerns,
            summary, summary, null) { EvidenceChecked = "The bounded implementation." }]);
        Assert.NotNull(_reviewedEvidence);
        Contract.ReviewFollowUpFinding[] findings = qualityBlock
            ? [new(aspect, status, summary), new("code-quality", "block", "backend/Quality.cs fails an optional diagnostic.")]
            : [new(aspect, status, summary)];
        var budget = AgentStudio.OrchestrationDecisions.ReviewBudgetPolicy.Decide(new(
            qualityBlock || status == "block" ? "ProductFailure" : "Pass", findings, used, maximum,
            new(4, 4), AgentStudio.OrchestrationDecisions.ReviewRoundBudgetLedger.Empty,
            "local-review-1", 1, 2, 2, 2));
        ReviewRoundBudgetStore.Record(_root, AgentStudio.OrchestrationDecisions.ReviewRoundBudgetLedger.Empty,
            new("local-review-1", budget.BlockingAspects, budget.RoundBudget.DegradedAspects));
        if (writeAdmission)
            CompletionContentEvidence.WriteLocalAdmission(_root, "local-review-1", new string('a', 40),
                budget.RoundBudget.ContentAdmissions, _reviewedEvidence);
        return budget;
    }

    private void LocalDelivery(string sha, DateTime? startedAt = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(TaskPaths.SessionEventsLog(_root), JsonSerializer.Serialize(new SessionEvent {
            Ts = startedAt ?? ReviewedAt.AddMinutes(-2), Kind = "start", Status = "completed", Result = "done",
            FinishedAt = (startedAt ?? ReviewedAt).AddMinutes(1), ExitCode = 0,
            CapturedSessionId = "local-session", HeadShaBefore = new string('0', 40), HeadShaAfter = sha,
        }) + "\n");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Unkeyed_bounded_slice_reads_current_composite_key_review_instead_of_superseded_local_concern(string? key)
    {
        var card = _card with { Key = key, TaskKey = "/fixtures/project::slice" };
        CompletionContentEvidence.WriteLocalContext(_root, BriefVersionStore.ReadCurrent(_root),
            "superseded-local-run", ReviewedAt.AddDays(-1));
        File.WriteAllText(Path.Combine(_root, "aspect-requirement-fit.json"), JsonSerializer.Serialize(new {
            status = "concerns", summary = "The superseded local delivery omitted the concern.",
        }));
        var run = Run("current", card);
        var currentReview = Review(run, "pass", "The current bounded slice meets its criteria.", "current", card);

        var facts = CompletionContentEvidence.Read(card, _authority);
        Assert.Equal("pass", facts.ContentStatus);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        var json = JsonSerializer.SerializeToElement(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(currentReview, json.GetProperty("reviewAttemptId").GetString());
        Assert.Equal(run.AttemptId, json.GetProperty("runAttemptId").GetString());
    }

    private RunAttemptDto Run(string key, TaskInfo? card = null)
    {
        var authorityKey = TaskIdentity.AuthorityKey(card ?? _card);
        var run = _authority.AcquireRun(authorityKey, "PROJ-TEST", null, "runner", "host", 60, "run-" + key,
            briefVersion: BriefVersionStore.ReadCurrent(_root)).RunAttempt!;
        Assert.True(_authority.SettleRun(new SettleRunAttemptRequest {
            Write = new(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + key),
            Outcome = "done", ResultSha = "sha-" + key,
        }).Accepted);
        return run;
    }

    private string Review(RunAttemptDto run, string status, string summary, string key, TaskInfo? card = null)
    {
        var authorityKey = TaskIdentity.AuthorityKey(card ?? _card);
        var sha = _authority.GetTaskProjection(authorityKey).CurrentRunAttempt!.ResultSha!;
        var created = _authority.CreateReviewAttempt(new CreateReviewAttemptRequest(authorityKey, "PROJ-TEST", sha,
            run.AttemptId, BriefVersionStore.ReadCurrent(_root), AttemptAuthorityService.Hash("policy-" + key), [], "review-" + key));
        Assert.True(created.Accepted, created.Message);
        var review = _authority.ClaimReview(created.AttemptId, "reviewer", "host", 60, "claim-" + key).ReviewAttempt!;
        var report = new Contract.ReviewReportRequest("reviewer", "instance", review.Lease!.LeaseId,
            review.LastFence, "report-" + key, "Pass", null, "Verification passed.",
            new("PROJ-TEST", sha, sha, "tree", false, false, "workspace", "namespace"),
            new("host", "reviewer", "instance", "os", "arch", "runtime", new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], [new("requirement-fit", status, "Product", summary)],
            AuthorityEpoch: review.AuthorityEpoch, BriefSha256: BriefVersionStore.ReadCurrent(_root));
        RemoteReviewSettlementJournal.Write(_root, new RemoteReviewSettlementEntry {
            AttemptId = review.AttemptId, TaskKey = authorityKey, IdempotencyKey = report.IdempotencyKey,
            ReportSha256 = RemoteReviewSettlementJournal.Hash(report), Report = report, ReceivedAtUtc = ReviewedAt,
        });
        Assert.True(_authority.SettleReview(new SettleReviewAttemptRequest(
            new(review.AttemptId, review.LastFence, review.AuthorityEpoch, report.IdempotencyKey), sha, ReviewTerminalOutcome.Pass)).Accepted);
        return review.AttemptId;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
