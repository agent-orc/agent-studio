using System.Text.Json;
using AgentStudio.Tasks;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;
// clock-independent: fixed review receipt dates and injected authority time never meet real time.
public sealed class CompletionContentEvidenceTests : IDisposable
{
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
