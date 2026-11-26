using AgentStudio.Git;
using AgentStudio.Operator;
using AgentStudio.Runner;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OverlapDispatchGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "overlap-guard-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly CaptureLogger _logger = new();
    private OverlapPredictionLedger Evidence => new(_root);
    private OverlapDispatchGuard Guard => new(_clock, _logger, Evidence);
    private static TaskInfo Card => new() { Id = "AGT-3988", Key = "AGT-3988", ProjectName = "Agent Studio",
        State = TaskStates.Ready, Title = "Edit backend/Features/Runner/ProjectRunner.cs" };
    private static InFlightTask Review(string id = "AGT-3481", string lane = TaskStates.AutoReview)
        => new(id, new(false, ["backend/Features/Runner/ProjectRunner.cs"]), lane);
    private static ProjectSettings Settings(string _) => new() { DispatchOverlapWindow = "in-flight" };

    [Fact]
    public void Unchanged_comparison_is_cached_and_logs_only_on_change_or_reminder()
    {
        var guard = Guard;
        var first = guard.Decide(Card, [], [Review()], 10, "in-flight", "remote-claim", "develop");
        for (var i = 0; i < 60; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Same(first, guard.Decide(Card, [], [Review()], 10, "in-flight", "remote-claim", "develop"));
        }
        Assert.Equal(2, _logger.Lines.Count(line => line.Contains("overlap-serialize")));
        guard.Decide(Card, [], [Review(lane: TaskStates.HumanReview)], 10, "in-flight", "remote-claim", "develop");
        Assert.Equal(3, _logger.Lines.Count(line => line.Contains("overlap-serialize")));
        Assert.Single(Evidence.Read(Card.ProjectName));
    }

    [Fact]
    public void Advisory_records_every_pair_once_and_never_serializes_scope()
    {
        var guard = Guard;
        for (var i = 0; i < 100; i++)
            Assert.True(guard.Decide(Card, [], [Review(), Review("AGT-3974")], 10, "off", "local", "develop").Admitted);
        Assert.Equal(2, _logger.Lines.Count(line => line.Contains("overlap-advisory")));
        Assert.Equal(2, Evidence.Read(Card.ProjectName).Count);
    }

    [Fact]
    public void Advisory_logs_a_named_pair_once_when_the_other_card_changes_delivery_state()
    {
        var guard = Guard;
        guard.Decide(Card, [], [Review() with { Version = "review" }], 10, "off", "remote-claim", "develop");
        guard.Decide(Card, [], [Review() with { Version = "human-review" }], 10, "off", "remote-claim", "develop");
        Assert.Single(Evidence.Read(Card.ProjectName));
        Assert.Single(_logger.Lines.Where(line => line.Contains("overlap-advisory")));
    }

    [Theory]
    [InlineData("delivery-attribution-ambiguous")]
    [InlineData("merge-conflict")]
    [InlineData("source-needs-rebase")]
    [InlineData("delivery-verdict-stale")]
    [InlineData("acceptance-rail:merge-conflict:retry-1")]
    [InlineData("integration-merge-round-queued")]
    public void Pipeline_delivery_round_bypasses_running_and_review_scope_but_keeps_capacity(string reason)
    {
        var guard = Guard;
        var recovery = Card with { PendingIntent = new() { SavedReason = reason } };
        var running = new[] { new RunningTask("AGT-4003", Review().Parallelism) };
        Assert.True(guard.Decide(recovery, running, [Review()], 10, "in-flight", "remote-claim", "develop").Admitted);
        Assert.False(guard.Decide(recovery, running, [Review()], 1, "in-flight", "remote-claim", "develop").Admitted);
    }

    [Fact]
    public void Scope_only_starvation_offers_a_named_single_use_override_after_the_threshold()
    {
        var guard = Guard;
        guard.Decide(Card, [], [Review()], 10, "in-flight", "remote-claim", "develop");
        guard.FinishSweep(Card.ProjectName, true);
        Assert.Empty(guard.Facts([Card], Settings, _clock.GetUtcNow().UtcDateTime));
        _clock.Advance(TimeSpan.FromMinutes(30));
        guard.FinishSweep(Card.ProjectName, true);
        var fact = Assert.Single(guard.Facts([Card], Settings, _clock.GetUtcNow().UtcDateTime));
        Assert.Equal(["AGT-3988", "AGT-3481"], fact.Cards);
        Assert.Contains("Run anyway", fact.Reason);
        Assert.Contains(OperatorOperations.RunAnyway, OperatorOperations.For(fact.Type));
        Assert.True(guard.RunAnyway(Card.ProjectName, fact.Key));
        Assert.True(guard.Decide(Card, [], [Review()], 10, "in-flight", "remote-claim", "develop").Admitted);
        guard.Claimed(Card, "attempt-1");
        Assert.False(guard.Decide(Card, [], [Review()], 10, "in-flight", "remote-claim", "develop").Admitted);
        Assert.Equal("attempt-1", Assert.Single(Evidence.Read(Card.ProjectName)).AttemptId);
    }

    [Fact]
    public void Other_refusals_clear_starvation_and_overrides_expire_or_lose_their_subject()
    {
        var guard = Guard;
        guard.Decide(Card, [], [Review()], 10, "in-flight", "local", "develop");
        _clock.Advance(TimeSpan.FromMinutes(31));
        guard.FinishSweep(Card.ProjectName, true);
        var fact = Assert.Single(guard.Facts([Card], Settings, _clock.GetUtcNow().UtcDateTime));
        guard.FinishSweep(Card.ProjectName, false);
        Assert.Empty(guard.Facts([Card], Settings, _clock.GetUtcNow().UtcDateTime));
        Assert.False(guard.RunAnyway(Card.ProjectName, fact.Key));
        guard.FinishSweep(Card.ProjectName, true);
        Assert.True(guard.RunAnyway(Card.ProjectName, fact.Key));
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(guard.Decide(Card, [], [Review()], 10, "in-flight", "local", "develop").Admitted);
        guard.RunAnyway(Card.ProjectName, fact.Key);
        Assert.False(guard.Decide(Card, [], [Review("AGT-4003")], 10, "in-flight", "local", "develop").Admitted);
    }

    [Fact]
    public void An_unrelated_comparison_change_does_not_restart_a_continuous_pair_wait()
    {
        var guard = Guard;
        guard.Decide(Card, [], [Review()], 10, "in-flight", "remote-claim", "develop");
        _clock.Advance(TimeSpan.FromMinutes(31));
        var unrelated = new InFlightTask("other", new(false, ["frontend/src/app/other.ts"]), TaskStates.AutoReview);
        guard.Decide(Card, [], [Review(), unrelated], 10, "in-flight", "remote-claim", "develop");
        guard.FinishSweep(Card.ProjectName, true);
        Assert.Single(guard.Facts([Card], Settings, _clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Prediction_score_requires_observed_delivery_and_pair_attribution()
    {
        var at = _clock.GetUtcNow();
        OverlapPrediction Prediction(string id) => new(id, "demo", id, "AGT-3481", id, "AGT-3481", "", "develop", "a.cs", at.UtcDateTime);
        var conflict = new IntegrationConflictEntry(1, "conflict", "demo", "yes", "develop", "sha-yes", "base", "tip",
            at.AddMinutes(1), null, null, 1, [new("a.cs", 1, ["AGT-3481"])], "agent-round", at.AddMinutes(1));
        var score = OverlapPredictionLedger.Score([Prediction("yes"), Prediction("no"), Prediction("unknown")], [conflict],
            row => row.Id == "unknown" ? (null, false) : ("sha-" + row.Id, row.Id == "no"));
        Assert.Equal(new OverlapPredictionScore(3, 1, 1, 1), score);
    }

    [Fact]
    public void A_conflict_without_counterpart_attribution_stays_unknown_after_integration()
    {
        var at = _clock.GetUtcNow();
        var prediction = new OverlapPrediction("prediction", "demo", "AGT-3988", "AGT-3481", "AGT-3988", "AGT-3481",
            "", "develop", "a.cs", at.UtcDateTime);
        var conflict = new IntegrationConflictEntry(1, "conflict", "demo", "AGT-3988", "develop", "delivery", "base", "tip",
            at.AddMinutes(1), null, null, 1, [new("a.cs", 1, [])], "mechanical", at.AddMinutes(1));
        Assert.Equal(new OverlapPredictionScore(1, 0, 0, 1),
            OverlapPredictionLedger.Score([prediction], [conflict], _ => ("delivery", true)));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 24, 3, 26, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
    private sealed class CaptureLogger : ILogger<OverlapDispatchGuard>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
