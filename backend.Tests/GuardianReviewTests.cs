using AgentStudio.Personas;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GuardianReviewTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    private static GuardianProjectState State() => new(false, Monday, null, [], [], [], [], []);
    private static GuardianMeasurements Measurements(params double?[] numbers) => new(
        GuardianReviewProjection.DefaultWeights.Keys.Zip(numbers).ToDictionary(p => p.First, p => p.Second),
        GuardianReviewProjection.DefaultWeights.Keys.ToDictionary(k => k, _ => (IReadOnlyList<string>)["fixture:two-incidents"]));

    [Fact]
    public void Two_incidents_one_false_blame_one_escape_produce_expected_scores_and_value()
    {
        // Common two-incident cohort: 1 / 2 * 10 = 5 false blames per ten.
        var applied = Measurements(5, 120, 1, 30, 1.2, 90);
        var guardian = Measurements(0, 60, 1, 15, .8, 45);
        var report = GuardianReviewProjection.Build("PROJ-001", Monday, State(), applied, guardian, Monday.AddDays(7));
        Assert.Equal(new double[] { 25, 20, 20, 15, 10, 10 }, report.Criteria.Select(c => c.Weight));
        Assert.Equal(5, report.Criteria[0].Applied);
        Assert.Equal(1, report.Criteria[2].Guardian);
        Assert.Equal(100, report.GuardianValue);
        Assert.Equal(20, report.AppliedValue); // Equal escape count ties at 100; all other baseline scores are zero.
        Assert.All(report.Criteria, c => Assert.Contains("fixture:two-incidents", c.Provenance));
        Assert.True(report.ShadowWeekComplete);
        Assert.Equal(Monday.AddDays(7), report.To);
        Assert.Equal("guardian-v1", report.Template);
        Assert.NotNull(PersonaReportTemplates.Find(report.Template));
        if (Environment.GetEnvironmentVariable("JOB_RESULTS_DIR") is { Length: > 0 } results)
        {
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "guardian-fixture-week.json"), System.Text.Json.JsonSerializer.Serialize(report,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
    }
    [Fact]
    public void Missing_measurements_are_not_zero_and_zero_weight_can_exclude_them()
    {
        var state = State();
        var measurements = Measurements(5, 60, 1, 15, null, 90);
        Assert.Null(GuardianReviewProjection.Build("PROJ-001", Monday, state, measurements, measurements, Monday).GuardianValue);
        state = state with { ReviewWeights = state.ReviewWeights.ToDictionary(p => p.Key, p => p.Key == "repair-cost" ? 0 : p.Value) };
        Assert.Equal(100, GuardianReviewProjection.Build("PROJ-001", Monday, state, measurements, measurements, Monday).GuardianValue);
    }
    [Fact]
    public void Every_disagreement_is_retained_with_later_outcome_and_half_open_window()
    {
        RedTestTriageDecision Decide(RedTestResult repeat) => RedTestTriagePolicy.Decide(new("Test.A", "routine", RedTestCategory.Routine)
        { FirstResult = RedTestResult.Fail, SameShaRepeat = repeat, EnvelopeCanary = RedTestResult.Pass, FingerprintMatchesReference = true });
        var a = Decide(RedTestResult.Pass); var b = Decide(RedTestResult.Fail);
        PostMergeIncidentLogEntry Log(string id, DateTimeOffset at) => new(id, "rule", "v1", "digest", "Flaky", null, [], [], "passes", "reason", "run", "sha", "Test.A", "fp", "guardian", at);
        var state = State() with { Decisions = [new(Log("a", Monday), true, a, b), new(Log("b", Monday.AddDays(7)), true, a, b)],
            ReviewEvidence = [new("outcome", "fp", Monday.AddDays(9), "outcome", "receipt:later", DecisionId: "a", Outcome: "Flaky confirmed")] };
        var report = GuardianReviewProjection.Build("PROJ-001", Monday, state, Measurements(0, 0, 0, 0, 0, 0), Measurements(0, 0, 0, 0, 0, 0), Monday.AddDays(10));
        Assert.Equal("Flaky confirmed", Assert.Single(report.Disagreements).Outcome);
        Assert.Contains("receipt:later", report.Disagreements[0].Provenance);
    }
    [Fact]
    public void Weight_and_evidence_updates_are_validated_persisted_and_idempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "guardian-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LineGuardianStore(root);
            Assert.Throws<ArgumentException>(() => store.SetReviewWeights("PROJ-001", new Dictionary<string, double>()));
            Assert.Throws<ArgumentException>(() => store.SetReviewWeights("PROJ-001", GuardianReviewProjection.DefaultWeights.ToDictionary(p => p.Key, _ => double.NaN)));
            var weights = GuardianReviewProjection.DefaultWeights.ToDictionary(p => p.Key, p => p.Value * 2);
            store.SetReviewWeights("PROJ-001", weights);
            var evidence = new GuardianReviewEvidence("one", "fp", Monday, "false-blame", "receipt:one");
            store.RecordReviewEvidence("PROJ-001", evidence); store.RecordReviewEvidence("PROJ-001", evidence);
            Assert.Throws<InvalidOperationException>(() => store.RecordReviewEvidence("PROJ-001", evidence with { Value = 2 }));
            var read = new LineGuardianStore(root).Read("PROJ-001");
            Assert.Single(read.ReviewEvidence); Assert.Equal(50, read.ReviewWeights["false-blames"]);
            Assert.Empty(store.Read("PROJ-002").ReviewEvidence);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Weekly_collector_uses_two_incidents_refusals_and_later_evidence_with_provenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "guardian-week-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LineGuardianStore(Path.Combine(root, "guardian"));
            var runs = new AgentStudio.Pipeline.PostMergeRunStore(Path.Combine(root, "runs"));
            var quarantine = new AgentStudio.Pipeline.TestQuarantineStore(Path.Combine(root, "quarantine"));
            var first = Monday.AddHours(1);
            runs.UpdateIncidents("PROJ-001", _ => [
                new("one", "card1", "FixForward", first, first.AddMinutes(60), null, ["Test.A"], [])
                    { TestId = "Test.A", LineStopReason = "base" },
                new("two", "card2", "FixForward", first, first.AddMinutes(40), null, ["Test.B"], []) { TestId = "Test.B" }]);
            foreach (var (id, test, delay) in new[] { ("one", "Test.A", 10), ("two", "Test.B", 20) })
                quarantine.Log("PROJ-001", new(id, "rule", "v1", "digest", "Regression", null,
                    ["StartHeld"], [], "passes", "reason", "run-" + id, "sha", test, id, "system", first.AddMinutes(delay)));
            store.RecordReviewEvidence("PROJ-001", new("blame", "one", first.AddHours(1), "false-blame", "attribution:withdrawn"));
            store.RecordReviewEvidence("PROJ-001", new("escape", "two", first.AddHours(2), "escape", "receipt:relapse"));
            store.RecordReviewEvidence("PROJ-001", new("wait", "one", first, "held-wait", "refusal:one", End: first.AddMinutes(40)));
            // The Dossier adds unrelated-refusal minutes to line-stop minutes, even when they overlap.
            store.RecordReviewEvidence("PROJ-001", new("unrelated", "two", first.AddMinutes(30), "unrelated-refusal", "refusal:two", End: first.AddMinutes(45)));
            var facts = new RedTestTriageFacts("Test.A", "routine", RedTestCategory.Routine)
            { FirstResult = RedTestResult.Fail, SameShaRepeat = RedTestResult.Pass,
                EnvelopeCanary = RedTestResult.Pass, FingerprintMatchesReference = true, IncidentFingerprint = "one" };
            store.Evaluate("PROJ-001", new("observed-one", "PROJ-001", "window", "sha", null, null, null, 1, [], [], "red", first, first),
                facts, RedTestTriagePolicy.Decide(facts), first.AddMinutes(5));
            var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Monday.AddDays(7));
            var service = new GuardianReviewService(store, runs, quarantine, clock);
            var report = service.Review("PROJ-001", Monday);
            Assert.Equal(5, report.Criteria.Single(c => c.Id == "false-blames").Applied);
            Assert.Null(report.Criteria.Single(c => c.Id == "false-blames").Guardian);
            Assert.Contains("Guardian decisions for every incident in the common cohort", report.MissingSources);
            Assert.Equal(1, report.Criteria.Single(c => c.Id == "escapes").Applied);
            Assert.Equal(75, report.Criteria.Single(c => c.Id == "unusable-minutes").Applied);
            Assert.Equal(40, report.Criteria.Single(c => c.Id == "held-wait").Applied);
            Assert.Equal(15, report.Criteria.Single(c => c.Id == "isolation-minutes").Applied);
            Assert.Contains("19.5", report.Criteria.Single(c => c.Id == "isolation-minutes").Note);
            Assert.Contains("refusal:one", report.Criteria.Single(c => c.Id == "held-wait").Provenance);
            Assert.Equal(Monday, service.Review("PROJ-001").From);
            Assert.Throws<ArgumentException>(() => service.Review("PROJ-001", Monday.AddDays(1)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Integration_wait_journal_records_boundaries_without_repeated_poll_noise()
    {
        var folder = Path.Combine(Path.GetTempPath(), "guardian-wait-" + Guid.NewGuid().ToString("N"));
        var timeline = new AgentStudio.Tasks.TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentStudio.Tasks.TimelineLog>.Instance);
        try
        {
            AgentStudio.Pipeline.GuardianIntegrationWaitJournal.Record(timeline, folder, false, true, Monday, "delivery");
            Assert.Empty(timeline.ReadAll(folder));
            AgentStudio.Pipeline.GuardianIntegrationWaitJournal.Record(timeline, folder, true, true, Monday, "delivery");
            AgentStudio.Pipeline.GuardianIntegrationWaitJournal.Record(timeline, folder, true, true, Monday.AddMinutes(5), "delivery");
            Assert.Single(timeline.ReadAll(folder));
            AgentStudio.Pipeline.GuardianIntegrationWaitJournal.Record(timeline, folder, false, true, Monday.AddMinutes(15), "delivery");
            var events = timeline.ReadAll(folder);
            Assert.Equal(2, events.Count);
            Assert.Equal("blocked", events[0].Details!["state"]);
            Assert.Equal("released", events[1].Details!["state"]);
            Assert.Equal("held-area", events[0].Details!["scope"]);
            Assert.Equal(15, (events[1].Ts - events[0].Ts).TotalMinutes);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public void Escape_expiry_is_reported_even_when_no_new_incident_started_in_the_week()
    {
        var root = Path.Combine(Path.GetTempPath(), "guardian-expiry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var guardian = new LineGuardianStore(Path.Combine(root, "guardian"));
            var runs = new AgentStudio.Pipeline.PostMergeRunStore(Path.Combine(root, "runs"));
            var quarantines = new AgentStudio.Pipeline.TestQuarantineStore(Path.Combine(root, "quarantines"));
            quarantines.Start("PROJ-001", new("expiry", "Test.A", "card", DateOnly.FromDateTime(Monday.UtcDateTime),
                null, "Confirmed red", "held", "system", Monday.AddHours(-12), Monday.AddHours(12), "fp", [], []), []);
            var report = new GuardianReviewService(guardian, runs, quarantines,
                new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Monday.AddDays(7))).Review("PROJ-001", Monday);
            var escapes = report.Criteria.Single(c => c.Id == "escapes");
            Assert.Equal(1, escapes.Applied);
            Assert.Contains("quarantine:expiry", escapes.Provenance);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Percentiles_interpolate_two_incidents()
    {
        Assert.Equal(15, GuardianReviewProjection.Percentile([10, 20], .5));
        Assert.Equal(19.5, GuardianReviewProjection.Percentile([10, 20], .95));
        Assert.Null(GuardianReviewProjection.Percentile([], .5));
    }
}
