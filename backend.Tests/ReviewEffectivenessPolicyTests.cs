using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure policy, aggregation and export inputs; no code here reads "now".

/// <summary>
/// AGT-3269: miss attribution, per-route aggregates, review-depth advice and
/// the Token Economy export, as direct matrix tests on the pure parts.
/// </summary>
public sealed class ReviewEffectivenessPolicyTests
{
    private static readonly DateTime At = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ReviewDelivery Delivery = new("demo", "AGT-1", 1, 1);
    private static readonly ReviewDelivery FixRound = Delivery with { DeliveryRound = 2 };

    private static ReviewObservation Review(
        string id, string level, string verdict, ReviewDelivery? delivery = null, string model = "gpt-6-sol",
        string? reasoning = "high", decimal? cost = 0.10m, ReviewFindingCounts? findings = null, long durationMs = 1000)
    {
        var outcome = ReviewOutcomes.From(verdict)!;
        return new ReviewObservation(id, delivery ?? Delivery, level, level, model, reasoning,
            1000, 200, 300, 40, durationMs, cost, verdict, outcome,
            findings ?? (level == ReviewLevels.Aspect ? ReviewFindingCounts.ForAspect(outcome) : ReviewFindingCounts.None),
            At, ReviewObservationOrigins.Pipeline);
    }

    private static ReviewDefectEvidence Evidence(string id, string source, ReviewDelivery? delivery = null)
        => new(id, delivery ?? Delivery, source, "defect", At, "test");

    [Theory]
    [InlineData("pass", "pass", true)]
    [InlineData("concerns", "concerns", true)]
    [InlineData("block", "block", false)]
    [InlineData("A", "pass", true)]
    [InlineData("B", "pass", true)]
    [InlineData("C", "concerns", true)]
    [InlineData("D", "block", false)]
    [InlineData("grade-d", "block", false)]
    public void Verdicts_map_to_outcomes_and_approvals(string verdict, string outcome, bool approves)
    {
        Assert.Equal(outcome, ReviewOutcomes.From(verdict));
        Assert.Equal(approves, ReviewOutcomes.Approves(outcome));
    }

    [Theory]
    [InlineData("environmental")]
    [InlineData("?")]
    [InlineData("")]
    [InlineData(null)]
    public void Verdicts_without_a_semantic_outcome_are_not_observations(string? verdict)
        => Assert.Null(ReviewOutcomes.From(verdict));

    [Theory]
    [InlineData("[major] Missing null check in the store", "major")]
    [InlineData("Blocker: data loss on restart", "blocker")]
    [InlineData("**Critical** race in the lease", "blocker")]
    [InlineData("(minor) naming", "minor")]
    [InlineData("nit - trailing space", "nit")]
    [InlineData("P1 retries are unbounded", "major")]
    [InlineData("The handler swallows errors", "unclassified")]
    public void Finding_severity_comes_from_the_leading_tag(string finding, string severity)
        => Assert.Equal(severity, ReviewFindingSeverity.Of(finding));

    [Fact]
    public void Real_findings_exclude_nits_and_unclassified_text()
    {
        var counts = ReviewFindingCounts.Classify(["[blocker] a", "[major] b", "[minor] c", "[nit] d", "plain e"]);

        Assert.Equal(new ReviewFindingCounts(1, 1, 1, 1, 1), counts);
        Assert.Equal(3, counts.Real);
        Assert.Equal(5, counts.Total);
    }

    public static TheoryData<string, string?, string, string, bool> AttributionMatrix => new()
    {
        // source, finder level, observation level, observation verdict, expected miss
        { ReviewDefectSources.Gate, null, ReviewLevels.Aspect, "pass", true },
        { ReviewDefectSources.Gate, null, ReviewLevels.Grade, "C", true },
        { ReviewDefectSources.Gate, null, ReviewLevels.Aspect, "block", false },
        { ReviewDefectSources.Operator, null, ReviewLevels.Grade, "A", true },
        { ReviewDefectSources.ProductionIncident, null, ReviewLevels.Deep, "pass", true },
        { ReviewDefectSources.Rollback, null, ReviewLevels.Aspect, "concerns", true },
        { ReviewDefectSources.LaterReview, ReviewLevels.Deep, ReviewLevels.Grade, "B", true },
        { ReviewDefectSources.LaterReview, ReviewLevels.Deep, ReviewLevels.Aspect, "pass", true },
        { ReviewDefectSources.LaterReview, ReviewLevels.Grade, ReviewLevels.Aspect, "pass", true },
        { ReviewDefectSources.LaterReview, ReviewLevels.Aspect, ReviewLevels.Aspect, "pass", false },
        { ReviewDefectSources.LaterReview, ReviewLevels.Deep, ReviewLevels.Deep, "pass", false },
        { ReviewDefectSources.LaterReview, null, ReviewLevels.Grade, "A", false },
    };

    [Theory]
    [MemberData(nameof(AttributionMatrix))]
    public void Attribution_matrix(string source, string? finderLevel, string level, string verdict, bool expected)
    {
        var evidence = Evidence("e1", source) with { FinderLevel = finderLevel, FinderObservationId = "finder" };

        Assert.Equal(expected, ReviewMissAttributionPolicy.Contradicts(evidence, Review("o1", level, verdict)));
    }

    [Fact]
    public void Evidence_never_crosses_deliveries_or_counts_against_its_own_finder()
    {
        var gateOnFixRound = Evidence("gate", ReviewDefectSources.Gate, FixRound);
        var deepBlock = Review("deep", ReviewLevels.Deep, "block");
        var selfEvidence = ReviewMissAttributionPolicy.EvidenceFrom(deepBlock)!;

        Assert.False(ReviewMissAttributionPolicy.Contradicts(gateOnFixRound, Review("round-1", ReviewLevels.Aspect, "pass")));
        Assert.True(ReviewMissAttributionPolicy.Contradicts(gateOnFixRound, Review("round-2", ReviewLevels.Aspect, "pass", FixRound)));
        Assert.False(ReviewMissAttributionPolicy.Contradicts(selfEvidence with { Source = ReviewDefectSources.Gate }, deepBlock));
    }

    [Fact]
    public void A_blocking_review_or_a_named_blocker_is_evidence_and_an_approval_is_not()
    {
        Assert.NotNull(ReviewMissAttributionPolicy.EvidenceFrom(Review("b", ReviewLevels.Aspect, "block")));
        Assert.NotNull(ReviewMissAttributionPolicy.EvidenceFrom(
            Review("c", ReviewLevels.Deep, "concerns", findings: new ReviewFindingCounts(Blocker: 1))));
        Assert.Null(ReviewMissAttributionPolicy.EvidenceFrom(Review("p", ReviewLevels.Grade, "A")));

        var evidence = ReviewMissAttributionPolicy.EvidenceFrom(Review("b2", ReviewLevels.Grade, "D"))!;
        Assert.Equal(ReviewDefectSources.LaterReview, evidence.Source);
        Assert.Equal(ReviewLevels.Grade, evidence.FinderLevel);
        Assert.Equal("b2", evidence.FinderObservationId);
    }

    [Fact]
    public void Attribute_returns_one_miss_per_contradicted_approval_and_evidence_pair()
    {
        var observations = new[]
        {
            Review("aspect-pass", ReviewLevels.Aspect, "pass"),
            Review("aspect-block", ReviewLevels.Aspect, "block"),
            Review("grade-a", ReviewLevels.Grade, "A", model: "claude-opus-5-5", reasoning: "xhigh"),
        };
        var evidence = new[]
        {
            ReviewMissAttributionPolicy.EvidenceFrom(observations[1])!,
            Evidence("gate", ReviewDefectSources.Gate),
        };

        var misses = ReviewMissAttributionPolicy.Attribute(observations, evidence, At);

        Assert.Equal(
            ["grade-a|review:aspect-block", "aspect-pass|gate", "grade-a|gate"],
            misses.Select(miss => $"{miss.ObservationId}|{miss.EvidenceId}"));
        var gradeMiss = misses.First(miss => miss.ObservationId == "grade-a");
        Assert.Equal((ReviewLevels.Grade, "claude-opus-5-5", "xhigh"), (gradeMiss.Level, gradeMiss.Model, gradeMiss.ReasoningLevel));
    }

    [Fact]
    public void Aggregate_computes_cost_blocker_rate_miss_rate_and_cost_per_real_finding_per_route()
    {
        var observations = new[]
        {
            Review("h1", ReviewLevels.Grade, "A", cost: 0.20m, durationMs: 1000),
            Review("h2", ReviewLevels.Grade, "B", cost: 0.30m, durationMs: 3000,
                findings: new ReviewFindingCounts(Major: 1, Nit: 2)),
            Review("h3", ReviewLevels.Grade, "D", cost: 0.10m, findings: new ReviewFindingCounts(Blocker: 1, Minor: 1)),
            Review("h4", ReviewLevels.Grade, "C", cost: null),
            Review("x1", ReviewLevels.Grade, "A", reasoning: "xhigh", cost: 0.90m),
            Review("a1", ReviewLevels.Aspect, "pass", reasoning: null, cost: null),
        };
        var misses = new[]
        {
            new ReviewMiss("h1", "gate", ReviewLevels.Grade, "gpt-6-sol", "high", ReviewDefectSources.Gate, At),
            new ReviewMiss("h1", "op", ReviewLevels.Grade, "gpt-6-sol", "high", ReviewDefectSources.Operator, At),
        };

        var rows = ReviewEffectivenessAggregation.Aggregate(observations, misses);

        Assert.Equal(
            ["aspect/gpt-6-sol/unknown", "grade/gpt-6-sol/high", "grade/gpt-6-sol/xhigh"],
            rows.Select(row => $"{row.Level}/{row.Model}/{row.ReasoningLevel}"));
        var high = rows[1];
        Assert.Equal((4, 3, 1), (high.Reviews, high.Approvals, high.Blocks));
        Assert.Equal(0.25, high.BlockerRate);
        Assert.Equal(1, high.MissedApprovals);
        Assert.Equal(0.3333, high.MissRate);
        Assert.Equal(new Dictionary<string, int> { ["gate"] = 1, ["operator"] = 1 }, high.MissesBySource);
        Assert.Equal((4000L, 800L, 1200L, 160L), (high.InputTokens, high.OutputTokens, high.CacheReadTokens, high.CacheCreationTokens));
        Assert.Equal(3, high.PricedReviews);
        Assert.Equal(0.60m, high.TotalCostUsd);
        Assert.Equal(0.20m, high.CostPerReviewUsd);
        Assert.Equal(new ReviewFindingCounts(1, 1, 1, 2, 0), high.Findings);
        Assert.Equal(3, high.RealFindings);
        Assert.Equal(0.20m, high.CostPerRealFindingUsd);
        Assert.Equal(1500L, high.AverageDurationMs);

        var aspect = rows[0];
        Assert.Null(aspect.TotalCostUsd);
        Assert.Null(aspect.CostPerReviewUsd);
        Assert.Null(aspect.CostPerRealFindingUsd);
        Assert.Equal(0.0, aspect.MissRate);
        Assert.Null(rows[2].CostPerRealFindingUsd);
    }

    [Fact]
    public void Aggregate_of_nothing_is_empty_and_rates_without_denominator_are_null()
    {
        Assert.Empty(ReviewEffectivenessAggregation.Aggregate([], []));

        var onlyBlocks = ReviewEffectivenessAggregation.Aggregate([Review("b", ReviewLevels.Aspect, "block")], []);
        Assert.Null(onlyBlocks[0].MissRate);
        Assert.Equal(1.0, onlyBlocks[0].BlockerRate);
    }

    private static ReviewEffectivenessRow Row(string reasoning, int approvals, double missRate, decimal? costPerReview)
        => new(ReviewLevels.Grade, "gpt-6-sol", reasoning, approvals, approvals, 0, 0, (int)(approvals * missRate),
            missRate, new Dictionary<string, int>(), 0, 0, 0, 0, costPerReview is null ? 0 : approvals,
            costPerReview * approvals, costPerReview, ReviewFindingCounts.None, 0, null, null);

    public static TheoryData<string, ReviewDepthAction, string?> DepthMatrix => new()
    {
        // case, expected action, suggested reasoning level
        { "too-few-approvals", ReviewDepthAction.InsufficientEvidence, null },
        { "unknown-route", ReviewDepthAction.InsufficientEvidence, null },
        { "misses-too-often", ReviewDepthAction.AddDeepReview, null },
        { "misses-too-often-even-with-cheaper-level", ReviewDepthAction.AddDeepReview, null },
        { "cheaper-level-equivalent", ReviewDepthAction.PreferCheaperReasoning, "high" },
        { "cheaper-level-misses-more", ReviewDepthAction.KeepRoute, null },
        { "cheaper-level-too-small", ReviewDepthAction.KeepRoute, null },
        { "cheaper-level-unpriced", ReviewDepthAction.KeepRoute, null },
        { "current-unpriced", ReviewDepthAction.KeepRoute, null },
    };

    [Theory]
    [MemberData(nameof(DepthMatrix))]
    public void Depth_advice_matrix(string scenario, ReviewDepthAction action, string? suggested)
    {
        ReviewEffectivenessRow[] rows = scenario switch
        {
            "too-few-approvals" => [Row("xhigh", 10, 0.0, 0.9m)],
            "unknown-route" => [Row("high", 50, 0.0, 0.2m)],
            "misses-too-often" => [Row("xhigh", 40, 0.20, 0.9m)],
            "misses-too-often-even-with-cheaper-level" => [Row("xhigh", 40, 0.20, 0.9m), Row("high", 40, 0.10, 0.2m)],
            "cheaper-level-equivalent" => [Row("xhigh", 40, 0.05, 0.9m), Row("high", 40, 0.06, 0.2m), Row("medium", 40, 0.10, 0.1m)],
            "cheaper-level-misses-more" => [Row("xhigh", 40, 0.05, 0.9m), Row("high", 40, 0.10, 0.2m)],
            "cheaper-level-too-small" => [Row("xhigh", 40, 0.05, 0.9m), Row("high", 5, 0.0, 0.2m)],
            "cheaper-level-unpriced" => [Row("xhigh", 40, 0.05, 0.9m), Row("high", 40, 0.0, null)],
            "current-unpriced" => [Row("xhigh", 40, 0.05, null), Row("high", 40, 0.0, 0.2m)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var advice = ReviewDepthAdvisor.Advise(ReviewLevels.Grade, "gpt-6-sol", "xhigh", rows);

        Assert.Equal(action, advice.Action);
        Assert.Equal(suggested, advice.SuggestedReasoningLevel);
        Assert.False(string.IsNullOrWhiteSpace(advice.Reason));
    }

    [Fact]
    public void Depth_advice_reads_only_the_requested_level()
    {
        var aspectRow = Row("high", 40, 0.0, 0.1m) with { Level = ReviewLevels.Aspect };

        var advice = ReviewDepthAdvisor.Advise(ReviewLevels.Grade, "gpt-6-sol", "xhigh", [Row("xhigh", 40, 0.05, 0.9m), aspectRow]);

        Assert.Equal(ReviewDepthAction.KeepRoute, advice.Action);
    }

    [Fact]
    public void Export_is_dated_states_its_method_and_carries_no_published_facts()
    {
        var summary = new ReviewEffectivenessSummary(At, At.AddDays(-30),
            ReviewEffectivenessAggregation.Aggregate([Review("h1", ReviewLevels.Grade, "A")], []),
            new ReviewEffectivenessCoverage(1, 1, 1, 1, 0, 0));

        var document = ReviewEffectivenessExport.Build(summary);
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(At, root.GetProperty("generatedAt").GetDateTime());
        Assert.Equal("observational", root.GetProperty("evidenceClass").GetString());
        Assert.Equal("ObservedRun", root.GetProperty("evidenceSource").GetString());
        Assert.False(root.GetProperty("publishedFact").GetBoolean());
        Assert.Equal(At.AddDays(-30), root.GetProperty("source").GetProperty("windowStartUtc").GetDateTime());
        foreach (var field in new[] { "description", "delivery", "approval", "miss", "blockerRate", "realFinding", "cost" })
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("method").GetProperty(field).GetString()));
        Assert.Equal(1, root.GetProperty("cohorts").GetArrayLength());
        Assert.NotEmpty(root.GetProperty("validityWarning").EnumerateArray());
        foreach (var publishedSection in new[] { "prices", "priceCatalog", "benchmarks", "facts", "models" })
            Assert.False(root.TryGetProperty(publishedSection, out _), publishedSection);
    }
}
