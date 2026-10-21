using AgentStudio.Review.Observations;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewObservationTests
{
    private static readonly FakeTimeProvider Clock =
        new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private static readonly string Sha = new('a', 40);

    private static ReviewObservation Review(string id, string verdict, DateTime at,
        string model = "gpt-6-sol", string reasoning = "high", decimal? cost = 0.2m,
        ReviewFindingCounts? findings = null, int? confirmed = 0)
        => new(id, "AGT-1", "aspect", Sha, at, model, reasoning,
            100, 20, 30, 0, 1000, verdict, findings ?? new(), cost,
            $"aspect-{id}.md", confirmed);

    [Fact]
    public void Journal_records_review_and_later_evidence_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), "review-observations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            var review = Review("standard", "pass", now.AddHours(-2));
            var evidence = new ReviewLaterEvidence("later-1", review.Id, Sha, now,
                "operator", "results/deep-review.md", "blocker", "Missed defect");
            Assert.True(ReviewObservationStore.AppendReview(folder, review));
            Assert.False(ReviewObservationStore.AppendReview(folder, review));
            Assert.True(ReviewObservationStore.AppendEvidence(folder, evidence));
            Assert.False(ReviewObservationStore.AppendEvidence(folder, evidence));
            var outcome = new ReviewFindingDisposition(review.Id, 0, 0, now, "sighting.md");
            ReviewObservationStore.AppendDisposition(folder, outcome);
            var rows = ReviewObservationStore.Read(folder);
            Assert.Equal(review, Assert.Single(rows.Reviews));
            Assert.Equal(evidence, Assert.Single(rows.Evidence));
            Assert.Equal(outcome, Assert.Single(rows.Dispositions));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Pipeline_projection_records_tokens_duration_verdict_severity_and_sha()
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var step = new PipelineStepExecution
        {
            StepId = "aspect-code-quality", Kind = StepKind.Aspect,
            Status = PipelineStepStatus.Passed, StartedAt = now.AddSeconds(-2), CompletedAt = now,
            Model = "gpt-6-sol", ThinkingLevel = "high", ReviewedResultSha = Sha,
            ReviewCli = "codex", ReviewedFileCount = 4,
            InputTokens = 100, OutputTokens = 20, CacheReadTokens = 30,
            DurationMs = 2000, Verdict = "block", ReviewFindings = new(Blocker: 1),
            EvidenceRef = "aspect-code-quality.md", ExecutionAttemptId = "review-1",
        };
        var record = new PipelineExecutionRecord
        {
            Attempt = 2, Steps = [step], Project = "demo", JobId = "AGT-1",
            StartedAt = now.AddSeconds(-2), CompletedAt = now,
        };
        var rows = new List<ReviewObservation>();
        ReviewObservationStore.AddPipelineReviews(rows, "AGT-1", record, TokenTestServices.Pricing());
        var observed = Assert.Single(rows);
        Assert.Equal(Sha, observed.ResultSha);
        Assert.Equal("review-1", observed.ReviewRoundId);
        Assert.Equal("high", observed.ReasoningLevel);
        Assert.Equal(100, observed.InputTokens);
        Assert.Equal(30, observed.CacheReadTokens);
        Assert.Equal(2000, observed.DurationMs);
        Assert.Equal(1, observed.Findings.Blocker);
        Assert.Equal(4, observed.FilesReviewed);
    }

    [Fact]
    public void Miss_requires_approved_review_same_sha_later_time_and_citation()
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var approved = Review("r1", "pass", now.AddHours(-1));
        var cited = new ReviewLaterEvidence("e1", "r1", Sha, now, "gate", "gate.log", "blocker", "Defect");
        Assert.True(ReviewObservationPolicy.IsMiss(approved, cited));
        Assert.False(ReviewObservationPolicy.IsMiss(approved with { Verdict = "block" }, cited));
        Assert.False(ReviewObservationPolicy.IsMiss(approved, cited with { ResultSha = new string('b', 40) }));
        Assert.False(ReviewObservationPolicy.IsMiss(approved, cited with { FoundAt = approved.ReviewedAt }));
        Assert.False(ReviewObservationPolicy.IsMiss(approved, cited with { EvidenceRef = "" }));
    }

    [Fact]
    public void Later_blocker_attributes_each_prior_approval_on_same_delivery_once()
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var first = Review("standard", "pass", now.AddHours(-3));
        var second = Review("grade", "B", now.AddHours(-2));
        var block = Review("deep", "block", now.AddHours(-1), findings: new(Blocker: 1));
        var differentDelivery = Review("other-sha", "pass", now.AddHours(-4)) with { ResultSha = new string('b', 40) };
        var misses = ReviewObservationStore.InferLaterReviewMisses([first, second, block, differentDelivery]).ToArray();
        Assert.Equal(2, misses.Length);
        Assert.Contains(misses, item => item.ReviewId == first.Id && item.Source == "later-review");
        Assert.Contains(misses, item => item.ReviewId == second.Id && item.Source == "later-review");
        Assert.Empty(ReviewObservationStore.InferLaterReviewMisses([
            first with { ReviewRoundId = "same-attempt" },
            block with { ReviewRoundId = "same-attempt" },
        ]));
    }

    [Fact]
    public void Aggregate_groups_model_and_reasoning_without_zeroing_unknown_prices_or_unconfirmed_findings()
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var approved = Review("pass", "pass", now.AddHours(-2));
        var blocked = Review("block", "block", now.AddHours(-1),
            findings: new(Blocker: 1, Minor: 1), confirmed: 1);
        var luna = Review("luna", "pass", now.AddMinutes(-30), model: "gpt-6-luna", reasoning: "medium", cost: null);
        var evidence = new ReviewLaterEvidence("e", approved.Id, Sha, now, "operator", "report.md", "blocker", "Defect");
        var groups = ReviewObservationPolicy.Aggregate([approved, blocked, luna], [evidence]);
        var sol = Assert.Single(groups, row => row.Model == "gpt-6-sol");
        Assert.Equal(2, sol.Reviews);
        Assert.Equal(1, sol.Blockers);
        Assert.Equal(1, sol.Misses);
        Assert.Equal(0.4m, sol.CostUsd);
        Assert.Equal(0.2m, sol.CostPerReview);
        Assert.Equal(0.5, sol.BlockerRate);
        Assert.Equal(1.0, sol.MissRate);
        Assert.Equal(1, sol.RealFindings);
        Assert.Equal(0.4m, sol.CostPerRealFinding);
        var unpriced = Assert.Single(groups, row => row.Model == "gpt-6-luna");
        Assert.Null(unpriced.CostUsd);
        Assert.Null(unpriced.CostPerReview);
        Assert.Null(unpriced.CostPerRealFinding);
    }

    [Fact]
    public void Review_depth_requires_repeated_measured_misses()
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var reviews = Enumerable.Range(0, 5)
            .Select(index => Review($"r{index}", "pass", now.AddHours(-6 + index)))
            .ToArray();
        var evidence = reviews.Take(2).Select((row, index) => new ReviewLaterEvidence(
            $"e{index}", row.Id, Sha, now, "production", "rollback.md", "blocker", "Regression")).ToArray();
        Assert.True(ReviewObservationPolicy.RequireFullReview(ReviewObservationPolicy.Aggregate(reviews, evidence)));
        Assert.False(ReviewObservationPolicy.RequireFullReview(ReviewObservationPolicy.Aggregate(reviews, evidence.Take(1).ToArray())));
    }

    [Fact]
    public void Export_uses_token_economy_run_shape_and_excludes_missing_scope()
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var observed = Review("r1", "block", now,
            findings: new(Blocker: 1), confirmed: 1) with
        {
            Cli = "codex", Aspect = "code-quality", FilesReviewed = 3,
            DismissedFindings = 0,
        };
        var run = Assert.IsType<TokenEconomyReviewRun>(TokenEconomyReviewExport.From(observed));
        Assert.Equal("quality-studio-review-run.schema.json", run.Schema);
        Assert.Equal("observational", run.EvidenceStatus);
        Assert.Equal(3, run.Scope.FilesReviewed);
        Assert.Equal(1, run.Outcomes?.ConfirmedFindings);
        var json = JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"$schema\":\"quality-studio-review-run.schema.json\"", json);
        Assert.Contains("\"evidenceStatus\":\"observational\"", json);
        Assert.Null(TokenEconomyReviewExport.From(observed with { FilesReviewed = null }));
    }
}
