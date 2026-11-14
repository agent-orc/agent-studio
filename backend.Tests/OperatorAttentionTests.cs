using AgentStudio.Operator;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3576: the attention list reads the facts of the operator decision
/// stream as one current list. Its count is the number of its rows, it keeps
/// the stream's project scope, and reading it never publishes an event.
/// </summary>
public sealed class OperatorAttentionTests : IDisposable
{
    private static readonly DateTime At = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-attention-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(At));
    private readonly OperatorDecisionOptions _options = new(
        TimeSpan.FromSeconds(5), TimeSpan.FromHours(24), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(15));

    [Fact]
    public void The_list_keeps_the_readers_scope_and_its_count_equals_its_rows()
    {
        var facts = new[]
        {
            Fact(OperatorDecisionTypes.HumanReviewReady, "alpha", "A-2", At.AddMinutes(5)),
            Fact(OperatorDecisionTypes.EscalationOpened, "alpha", "A-1", At),
            Fact(OperatorDecisionTypes.EscalationOpened, "beta", "B-1", At),
            Fact(OperatorDecisionTypes.QuotaExhausted, null, null, At),
            Fact(OperatorDecisionTypes.WatcherHeartbeat, null, null, At),
            Fact(OperatorDecisionTypes.EscalationOpened, "alpha", "A-1", At),
        };

        var scoped = OperatorAttention.Project(facts, new OperatorDecisionReader(false, project => project == "alpha"));
        var all = OperatorAttention.Project(facts, OperatorDecisionReader.Everything);

        Assert.Equal(["A-1", "A-2"], scoped.Items.Select(item => item.CardKey));
        Assert.Equal(scoped.Items.Count, scoped.Count);
        Assert.Equal(4, all.Count);
        Assert.Equal(all.Items.Count, all.Count);
        Assert.DoesNotContain(all.Items, item => item.Type == OperatorDecisionTypes.WatcherHeartbeat);
        Assert.Equal(["A-2"], scoped.Items[1].Cards);
        Assert.Equal(OperatorOperations.For(OperatorDecisionTypes.HumanReviewReady), scoped.Items[1].SuggestedOperations);
    }

    [Fact]
    public async Task Reading_the_current_facts_publishes_nothing_and_reuses_a_fresh_read()
    {
        var source = new CountingSource([Fact(OperatorDecisionTypes.EscalationOpened, "alpha", "A-1", At)]);
        var log = new OperatorDecisionLog(Path.Combine(_root, "operator"), _options.Retention, _clock);
        using var publisher = Publisher(log, source);

        var first = await publisher.CurrentFactsAsync();
        var again = await publisher.CurrentFactsAsync();
        _clock.Advance(_options.SweepInterval);
        await publisher.CurrentFactsAsync();

        Assert.Equal(["A-1"], first.Select(fact => fact.CardKey));
        Assert.Same(first, again);
        Assert.Equal(2, source.Reads);
        Assert.Equal(0, log.Head);
    }

    [Fact]
    public async Task A_failing_source_keeps_the_facts_of_its_previous_read()
    {
        var source = new CountingSource([Fact(OperatorDecisionTypes.EscalationOpened, "alpha", "A-1", At)]);
        var log = new OperatorDecisionLog(Path.Combine(_root, "operator"), _options.Retention, _clock);
        using var publisher = Publisher(log, source);
        await publisher.SweepOnceAsync();

        source.Fail = true;
        _clock.Advance(_options.SweepInterval);
        var current = await publisher.CurrentFactsAsync();

        Assert.Equal(["A-1"], current.Select(fact => fact.CardKey));
    }

    /// <summary>
    /// AGT-3695: a Post Processing card whose publication was refused is on
    /// the list with the plain reason; a passed merge or another lane is not.
    /// </summary>
    [Fact]
    public void A_post_processing_card_with_a_refused_publication_needs_the_owner()
    {
        var sha = new string('c', 40);
        var blocked = new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId, Status = PipelineStepStatus.Failed, Verdict = "error",
            FailureCode = AcceptedIntegrationFailureCodes.IntegrationPushBlocked, CompletedAt = At,
            Reason = $"Publication failed (sha-not-on-branch); The approved merge result {sha} is not contained in local develop; refusing to push it.",
        };
        var passed = blocked with { Status = PipelineStepStatus.Passed, Verdict = "merged", FailureCode = null };
        var tasks = new[]
        {
            Card("A-1", TaskStates.AutoReview), Card("A-2", TaskStates.AutoReview), Card("A-3", TaskStates.HumanReview),
        };

        var facts = OperatorDecisionFacts.FromBlockedPublications(tasks, task => task.Id == "A-2" ? passed : blocked);
        var item = Assert.Single(OperatorAttention.Project(facts, OperatorDecisionReader.Everything).Items);

        Assert.Equal(OperatorDecisionTypes.PushBlocked, item.Type);
        Assert.Equal("A-1", item.CardKey);
        Assert.Equal(At, item.ObservedAt);
        Assert.StartsWith("Push of A-1 is blocked: Publication stopped: the approved merge result ccccccccc", item.Reason);
        Assert.Equal(OperatorOperations.For(OperatorDecisionTypes.PushBlocked), item.SuggestedOperations);
    }

    private static TaskInfo Card(string key, string state) => new()
    {
        Id = key, Key = key, ProjectName = "alpha", State = state, EnteredLaneAt = At, CreatedAt = At,
    };

    private OperatorDecisionPublisher Publisher(OperatorDecisionLog log, IOperatorDecisionFactSource source)
        => new(log, [source], _options, _clock, NullLogger<OperatorDecisionPublisher>.Instance);

    private static OperatorDecisionFact Fact(string type, string? project, string? card, DateTime observed)
        => new(type, $"{type}|{project}|{card}", project, card, $"{card ?? "installation"} needs attention.",
            observed, []);

    private sealed class CountingSource(IReadOnlyList<OperatorDecisionFact> facts) : IOperatorDecisionFactSource
    {
        public int Reads { get; private set; }
        public bool Fail { get; set; }

        public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.EscalationOpened];

        public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        {
            Reads++;
            if (Fail) throw new IOException("store unavailable");
            return facts;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
