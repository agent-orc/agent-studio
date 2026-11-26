using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: policy and parser inputs use explicit timestamps; stored fixture dates never meet the wall clock.
public sealed class DispatchReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-review-" + Guid.NewGuid().ToString("N"));
    private IConfiguration Config => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root }).Build();
    private static readonly DateTimeOffset At = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private static DispatchReviewRaw Raw(string persona, double conflicts, double parallelism, int count = 40) => new(
        persona, persona == "queue-keeper" ? DispatchStrategies.LaneEntry : DispatchStrategies.OnePerArea,
        "shadow", count, Enumerable.Range(1, count).Select(i => $"card-{i}").ToArray(),
        new Dictionary<string, double?>
        {
            ["conflicts"] = conflicts, ["tokens"] = 1000, ["rework"] = .2, ["forecast"] = .52,
            ["parallelism"] = parallelism, ["lead"] = 2, ["ownCost"] = 0, ["explanation"] = .85,
        }, .7, true, []);

    [Fact]
    public void MinMax_ratios_ties_and_gate_match_the_concept()
    {
        var window = new DispatchReviewWindow("p", At, At.AddDays(30), [Raw("queue-keeper", 20, 1), Raw("warden", 0, .4)]);
        var result = DispatchReviewScoring.Score(window, DispatchWeightRevision.Default, [DispatchWeightRevision.Default]);
        var warden = result.Personas.Single(r => r.Raw.Persona == "warden");
        Assert.Equal(100, warden.Scores["conflicts"]);
        Assert.Equal(100, warden.Scores["tokens"]);
        Assert.Equal(52, warden.Scores["forecast"]);
        Assert.Equal(40, warden.Scores["parallelism"]);
        Assert.Equal(.5, warden.GateMultiplier);
        Assert.Equal(warden.PlainValue / 2, warden.Value);
        Assert.Equal(0, result.Personas.Single(r => r.Raw.Persona == "queue-keeper").Scores["conflicts"]);
    }

    [Theory]
    [InlineData(39, false)]
    [InlineData(40, true)]
    public void Values_require_forty_integrated_cards(int count, bool scored)
    {
        var result = DispatchReviewScoring.Score(new("p", At, At.AddDays(30), [Raw("queue-keeper", 0, .8, count)]), DispatchWeightRevision.Default, []);
        Assert.Equal(scored, result.Personas[0].Value.HasValue);
        Assert.Equal(scored ? "scored" : "too few cards", result.Personas[0].Status);
    }

    [Fact]
    public void Missing_metrics_use_one_comparison_denominator_and_never_become_zero_cost()
    {
        var baseline = Raw("queue-keeper", 10, 1);
        var other = Raw("warden", 0, .8) with { Criteria = new Dictionary<string, double?>(baseline.Criteria) { ["tokens"] = null, ["conflicts"] = 0, ["parallelism"] = .8 } };
        var result = DispatchReviewScoring.Score(new("p", At, At.AddDays(30), [baseline, other]), DispatchWeightRevision.Default, []);
        Assert.Null(result.Personas.Single(r => r.Raw.Persona == "warden").Scores["tokens"]);
        Assert.Equal((30 * 100 + 10 * 100 + 10 * 52 + 10 * 80 + 5 * 100 + 5 * 100 + 5 * 100) / 75.0,
            result.Personas.Single(r => r.Raw.Persona == "warden").PlainValue);
    }

    [Fact]
    public void Revisions_and_raw_windows_survive_restart_and_rescore_without_mutating_raw()
    {
        var settings = new WorkspaceSettingsService(NullLogger<WorkspaceSettingsService>.Instance, Config);
        var weights = DispatchWeightRevision.Default.Weights.ToDictionary(p => p.Key, _ => 0);
        weights["conflicts"] = 100;
        var revision = settings.ReviseDispatchReviewWeights("workspace", weights, .8, "owner", At, "Prefer fewer conflicts");
        Assert.Equal("w2", revision.Id);
        Assert.Equal("owner", revision.Author);
        var reloaded = new WorkspaceSettingsService(NullLogger<WorkspaceSettingsService>.Instance, Config).Get("workspace");
        Assert.Equal(new[] { "w1", "w2" }, reloaded.DispatchReviewWeights.Select(r => r.Id));
        var store = new DispatchStore(Config);
        var window = new DispatchReviewWindow("p", At, At.AddDays(30), [Raw("queue-keeper", 10, 1), Raw("warden", 0, .8)]);
        store.WriteReviewWindow(window);
        var persisted = new DispatchStore(Config).ReadReviewWindow("p", At, At.AddDays(30))!;
        var old = DispatchReviewScoring.Score(persisted, reloaded.DispatchReviewWeights[0], reloaded.DispatchReviewWeights);
        var current = DispatchReviewScoring.Score(persisted, revision, reloaded.DispatchReviewWeights);
        Assert.Equal(100, current.Personas.Single(r => r.Raw.Persona == "warden").Value);
        Assert.NotEqual(old.Personas.Single(r => r.Raw.Persona == "warden").Value, current.Personas.Single(r => r.Raw.Persona == "warden").Value);
        Assert.Equal(.52, persisted.Raw[1].Criteria["forecast"]);
        Assert.Null(store.ReadReviewWindow("other-project", At, At.AddDays(30)));
    }

    [Fact]
    public void Invalid_weights_gates_and_windows_are_rejected()
    {
        var weights = DispatchWeightRevision.Default.Weights.ToDictionary(p => p.Key, p => p.Value);
        foreach (var gate in new[] { .49, 1.01, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => DispatchWeightRevision.Validate(weights, gate));
        weights["conflicts"]++;
        Assert.Throws<ArgumentException>(() => DispatchWeightRevision.Validate(weights, .8));
        weights["conflicts"]--;
        weights["unknown"] = 0;
        Assert.Throws<ArgumentException>(() => DispatchWeightRevision.Validate(weights, .8));
        foreach (var window in new[] { "bad", "2026-11-22/2026-11-20", "2020-01-01/2026-01-01" })
            Assert.Throws<ArgumentException>(() => DispatchReviewService.ParseWindow(window, At));
        var range = DispatchReviewService.ParseWindow(null, At);
        Assert.Equal(TimeSpan.FromDays(30), range.To - range.From);
    }

    [Fact]
    public void Comparison_requires_baseline_and_identical_card_cohorts()
    {
        Assert.Throws<ArgumentException>(() => DispatchReviewScoring.Score(
            new("p", At, At.AddDays(1), [Raw("warden", 0, .8)]), DispatchWeightRevision.Default, []));
        Assert.Throws<ArgumentException>(() => DispatchReviewScoring.Score(
            new("p", At, At.AddDays(1), [Raw("queue-keeper", 10, 1), Raw("warden", 0, .8) with { Cards = ["other"] }]), DispatchWeightRevision.Default, []));
    }

    [Fact]
    public void Missing_parallelism_cannot_produce_a_gated_value()
    {
        var raw = Raw("queue-keeper", 0, 1);
        raw = raw with { Criteria = new Dictionary<string, double?>(raw.Criteria) { ["parallelism"] = null } };
        var result = DispatchReviewScoring.Score(new("p", At, At.AddDays(1), [raw]), DispatchWeightRevision.Default, []);
        Assert.Null(result.Personas[0].Value);
        Assert.Equal("missing parallelism evidence", result.Personas[0].Status);
    }

    [Fact]
    public void Explanation_requires_each_cited_part_and_owner_rating_invalidates_the_raw_window()
    {
        var store = new DispatchStore(Config);
        var plan = DispatchPlanPolicy.Plan(new("p", At,
            [new("card", 999, At, [], true, DispatchForecast.Unknown, [])], [], 1, DispatchLedgerSummary.Empty, []),
            DispatchStrategies.Starter(DispatchStrategies.LaneEntry));
        store.AppendShadow(new("evaluation", "decision", "facts-digest", At, "local", plan));
        var entry = Assert.Single(store.ReadDispatcherLog("p"));
        Assert.True(store.VerifyDispatcherLog("p"));
        Assert.True(DispatchReviewService.ExplanationPasses(entry));
        Assert.False(DispatchReviewService.ExplanationPasses(entry with { ExpectedEffect = null }));
        Assert.False(DispatchReviewService.ExplanationPasses(entry with { Rule = "" }));
        Assert.False(DispatchReviewService.ExplanationPasses(entry with { FactsDigest = "" }));
        Assert.False(DispatchReviewService.ExplanationPasses(entry with { PassedOver = [new("other", "", null)] }));
        store.WriteReviewWindow(new("p", At, At.AddDays(1), [Raw("queue-keeper", 0, 1)]));
        store.RateExplanation("p", new(entry.Id, entry.PersonaId, 80, "owner", At));
        Assert.Null(store.ReadReviewWindow("p", At, At.AddDays(1)));
        Assert.Equal(80, Assert.Single(new DispatchStore(Config).ReadExplanationRatings("p")).Score);
        Assert.Throws<ArgumentException>(() => store.RateExplanation("p", new(entry.Id, entry.PersonaId, 101, "owner", At)));
        Assert.Throws<ArgumentException>(() => store.RateExplanation("p", new("unknown", entry.PersonaId, 80, "owner", At)));
    }

    [Theory]
    [InlineData(99, 2, 4, 5, false)]
    [InlineData(100, 1, 4, 5, false)]
    [InlineData(100, 2, 3, 5, false)]
    [InlineData(100, 2, 4, 4.99, false)]
    [InlineData(100, 2, 4, 5, true)]
    public void Champion_requires_shadow_evidence_margin_and_four_live_waves(int cards, int shadowWaves, int liveWaves, double margin, bool eligible)
    {
        var facts = new DispatchCompetitionFacts(60, 60 + margin, cards, shadowWaves, liveWaves, true, 20, 15, 5, true);
        var result = DispatchCompetitionPolicy.Evaluate(facts);
        Assert.Equal(eligible, result.ChampionEligible);
        Assert.True(result.GuardExceeded);
        Assert.Equal(DispatchStrategies.LaneEntry, result.FallbackStrategy);
        Assert.Equal(70, result.FeedImportance);
        Assert.True(result.OwnerUndo);
        Assert.False(DispatchCompetitionPolicy.Evaluate(facts with { SameGuardCards = false }).GuardExceeded);
        Assert.False(DispatchCompetitionPolicy.Evaluate(facts with { GuardCards = 19 }).GuardExceeded);
        Assert.False(DispatchCompetitionPolicy.Evaluate(facts with { LiveConflictsPer100 = 14.99 }).GuardExceeded);
        Assert.False(DispatchCompetitionPolicy.Evaluate(facts with { AheadInEveryLiveWave = false }).ChampionEligible);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
