using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Watcher;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// OP-2 acceptance on the real stores: each event type is
/// published by the running publisher loop within 10 seconds of the fact it
/// reports, once, on a controlled clock.
/// </summary>
public sealed class OperatorDecisionPublisherTests : IDisposable
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-decision-publisher-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-04T09:00:00Z"));
    private readonly OperatorDecisionOptions _options = new(
        TimeSpan.FromSeconds(5), TimeSpan.FromHours(24), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(15));
    private volatile TaskInfo[]? _tasks = [];

    [Fact]
    public async Task Each_event_type_follows_its_fact_within_ten_seconds_and_only_once()
    {
        var log = new OperatorDecisionLog(Path.Combine(_root, "operator"), _options.Retention, _clock);
        var cases = new WatcherCaseStore(NullLogger<WatcherCaseStore>.Instance);
        var batches = new BatchGateStore(Path.Combine(_root, "batch-gates"));
        var pushes = new BatchIntegrationReservationService(Path.Combine(_root, "pushes"), _clock, TimeSpan.FromMinutes(3));
        var limits = new ProviderLimitRegistry();
        var leases = new WatcherLeaseService(Path.Combine(_root, "operator"), _options, log,
            new ControlPlaneAuditLog(Path.Combine(_root, "security"), _clock), _clock);
        using var publisher = new OperatorDecisionPublisher(log,
            [
                new OperatorTaskFactSource(() => _tasks,
                    task => task.Id == "A-6" ? new QuotaFallbackStatus("codex", "fallback-model", "primary exhausted") : null),
                new OperatorWatcherCaseFactSource(cases, _root),
                new OperatorBatchGateFactSource(batches),
                new OperatorPendingPushFactSource(pushes, jobId => jobId == "job-8" ? "A-8" : null),
                new OperatorProviderLimitFactSource(limits),
            ],
            _options, _clock, NullLogger<OperatorDecisionPublisher>.Instance);

        // The push turns overdue at the batch gate's own 3-minute alert, two
        // seconds after the other facts below.
        pushes.MarkPushPending("alpha", "job-8", Sha);
        _clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(2));
        await publisher.StartAsync(CancellationToken.None);
        await WaitUntil(() => publisher.IntervalWaits >= 1);
        Assert.Equal(0, log.Head);

        var factTime = _clock.GetUtcNow().UtcDateTime;
        var expected = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var type in OperatorDecisionTypes.All) expected[type] = factTime;
        expected[OperatorDecisionTypes.PushPendingOverdue] = factTime.AddSeconds(2);
        _tasks =
        [
            Card("A-1", TaskStates.Escalated, factTime),
            Card("A-2", TaskStates.HumanReview, factTime),
            Card("A-3", TaskStates.Preparation, factTime) with
            {
                Kind = TaskKinds.Decision, Decision = new DecisionContent { Question = "Which store?" },
            },
            Card("A-5", TaskStates.Ready, factTime) with
            {
                QuotaWait = new QuotaWaitStatus("claude", factTime, factTime.AddHours(2), 30, "weekly limit"),
            },
            Card("A-6", TaskStates.Progress, factTime),
        ];
        cases.Save(_root, Case("host-offline", factTime));
        cases.Save(_root, Case("idle-capacity", factTime));
        cases.Save(_root, Case("batch-gate-stuck", factTime));
        var parent = Close(batches, null, "A-9", "A-10");
        batches.AppendState(new BatchGateState(parent.BatchId, parent.MembershipDigest, BatchPhase.Red, null, 1,
            _clock.GetUtcNow(), "deterministic suite red"));
        Close(batches, parent.BatchId, "A-9");
        limits.Record(new ProviderLimitStatus("claude", factTime, factTime.AddHours(1), "rate limited", true));
        Assert.Equal(WatcherLeaseOutcome.Granted, leases.Acquire("service-account:watcher", new WatcherLeaseRequest("laptop")).Outcome);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntil(() => Types(log).IsSupersetOf(OperatorDecisionTypes.All));

        var events = log.Read(0, 1000, _ => true).Events;
        foreach (var type in OperatorDecisionTypes.All)
        {
            var published = events.Where(item => item.Type == type).ToArray();
            Assert.NotEmpty(published);
            Assert.All(published, item => Assert.InRange(item.Time - expected[type], TimeSpan.Zero, Limit));
        }
        Assert.Equal("A-8", events.Single(item => item.Type == OperatorDecisionTypes.PushPendingOverdue).CardKey);
        Assert.Equal(2, events.Count(item => item.Type == OperatorDecisionTypes.QuotaExhausted));

        // Facts that last are not published again by later sweeps.
        var head = log.Head;
        var waits = publisher.IntervalWaits;
        _clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntil(() => publisher.IntervalWaits > waits);
        Assert.Equal(head, log.Head);
        await publisher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_restarted_publisher_does_not_publish_a_lasting_fact_again()
    {
        _tasks = [Card("A-1", TaskStates.Escalated, _clock.GetUtcNow().UtcDateTime)];
        var folder = Path.Combine(_root, "operator");
        var first = Publisher(new OperatorDecisionLog(folder, _options.Retention, _clock));
        Assert.Single(await first.SweepOnceAsync());

        var log = new OperatorDecisionLog(folder, _options.Retention, _clock);
        var restarted = Publisher(log);
        Assert.Empty(await restarted.SweepOnceAsync());
        Assert.Equal(1, log.Head);
    }

    [Fact]
    public async Task A_failing_source_publishes_nothing_twice_and_does_not_stop_the_others()
    {
        _tasks = [Card("A-1", TaskStates.Escalated, _clock.GetUtcNow().UtcDateTime)];
        var flaky = new FlakySource();
        var publisher = Publisher(new OperatorDecisionLog(null, _options.Retention, _clock), flaky);

        Assert.Equal(2, (await publisher.SweepOnceAsync()).Count);
        flaky.Fail = true;
        _tasks = [.. _tasks!, Card("A-2", TaskStates.HumanReview, _clock.GetUtcNow().UtcDateTime)];
        Assert.Equal([OperatorDecisionTypes.HumanReviewReady], (await publisher.SweepOnceAsync()).Select(item => item.Type));
        flaky.Fail = false;
        Assert.Empty(await publisher.SweepOnceAsync());
    }

    [Fact]
    public async Task An_unpublished_task_index_keeps_the_published_card_facts_and_reads_no_empty_board()
    {
        _tasks = [Card("A-1", TaskStates.Escalated, _clock.GetUtcNow().UtcDateTime)];
        var publisher = Publisher(new OperatorDecisionLog(null, _options.Retention, _clock));
        Assert.Single(await publisher.SweepOnceAsync());

        // A restarted index has no publication yet: the sweep reads nothing
        // and must not forget A-1, or the next publication repeats it.
        _tasks = null;
        Assert.Empty(await publisher.SweepOnceAsync());
        _tasks = [Card("A-1", TaskStates.Escalated, _clock.GetUtcNow().UtcDateTime)];
        Assert.Empty(await publisher.SweepOnceAsync());
    }

    private OperatorDecisionPublisher Publisher(OperatorDecisionLog log, params IOperatorDecisionFactSource[] more)
        => new(log, [new OperatorTaskFactSource(() => _tasks, _ => null), .. more], _options, _clock,
            NullLogger<OperatorDecisionPublisher>.Instance);

    private static HashSet<string> Types(OperatorDecisionLog log)
        => log.Read(0, 1000, _ => true).Events.Select(item => item.Type).ToHashSet(StringComparer.Ordinal);

    private static async Task WaitUntil(Func<bool> condition)
    {
        // Synchronisation with the background loop only; the measured times come from the fake clock.
        for (var attempt = 0; attempt < 600 && !condition(); attempt++) await Task.Delay(50);
        Assert.True(condition());
    }

    private static TaskInfo Card(string key, string state, DateTime at) => new()
    {
        Id = key, Key = key, ProjectName = "alpha", State = state, EnteredLaneAt = at, CreatedAt = at,
    };

    private static WatcherCase Case(string kind, DateTime since) => new()
    {
        Id = "WCH-" + kind, Fingerprint = "silence-alpha-" + kind, DetectorClass = WatcherDetectorClasses.Silence,
        Project = "alpha", Subject = "host:h1:coding", SinceUtc = since, FirstSeenUtc = since, LastSeenUtc = since,
        LastSummary = kind + " observed", LastDetails = new Dictionary<string, string> { ["stallKind"] = kind },
    };

    private BatchGateManifest Close(BatchGateStore store, string? parent, params string[] keys)
    {
        var scope = new BatchGateScope("alpha", "repo", "develop", "full", "digest", "v1");
        var members = keys.Select((key, index) => new BatchGateSubject(key, "alpha", "repo", "develop", "full", "digest", "v1",
            "refs/heads/result", Sha, "run-1", 1, 1, Sha, true, true, true, true, false, null, false,
            _clock.GetUtcNow(), index + 1)).ToArray();
        var manifest = new BatchGateManifest(Guid.NewGuid().ToString("N"), scope, Sha,
            BatchGatePolicy.MembershipDigest(Sha, scope, members), _clock.GetUtcNow(), members, [], keys, parent);
        store.CloseManifest(manifest);
        return manifest;
    }

    private sealed class FlakySource : IOperatorDecisionFactSource
    {
        public bool Fail { get; set; }

        public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.BatchRed];

        public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc) => Fail
            ? throw new IOException("store unavailable")
            : [new OperatorDecisionFact(OperatorDecisionTypes.BatchRed, "batch.red|b1|t0", "alpha", null, "red", nowUtc, [])];
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
