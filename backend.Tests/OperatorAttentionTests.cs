using AgentStudio.Cli;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using Microsoft.Extensions.Configuration;
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
    public void Owner_options_preserve_policy_labels_and_recommendations()
    {
        OperatorOwnerOption[] options = [new("Extend24Hours", "extend 24 hours", false),
            new("ReportOnlyWithCard", "report-only with a card", true)];
        var fact = new OperatorDecisionFact(OperatorDecisionTypes.PostMergeRedIncident, "expired", "alpha", "AGT-1",
            "Quarantine reached 48 hours.", At, [], OwnerOptions: options);
        var item = Assert.Single(OperatorAttention.Project([fact], OperatorDecisionReader.Everything).Items);
        Assert.Equal(options, item.OwnerOptions);
        Assert.Equal("ReportOnlyWithCard", Assert.Single(item.OwnerOptions!, option => option.Recommended).Id);
    }

    [Fact]
    public void Open_health_incident_is_first_and_clears_on_recovery_while_successful_deploys_stay_quiet()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        var store = new HealthCheckStore(configuration);
        var started = new DateTimeOffset(At.AddHours(-1));
        var incident = new EnvironmentIncident("production/incident", "production", "Production", started,
            CardId: "health-production-1");
        var check = new HealthIncident("check-1", "production", "http", started, "HTTP 503", 3);
        var state = new ProjectHealthState();
        state.Environments["production"] = new EnvironmentHealthState { OpenIncident = incident };
        state.Checks[HealthCheckStore.Key("production", "http")] = new HealthCheckState { OpenIncident = check };
        store.Save("alpha", state);
        var source = new OperatorEnvironmentIncidentFactSource(store, () => ["alpha"],
            (project, cardId) => project == "alpha" && cardId == "health-production-1" ? "NS-2" : null);

        var facts = source.Collect(At)!.Concat([
            Fact(OperatorDecisionTypes.DeployCompleted, "alpha", "raw-job-id", At),
            Fact(OperatorDecisionTypes.HumanReviewReady, "alpha", "A-2", At.AddMinutes(-5)),
        ]);
        var projected = OperatorAttention.Project(facts, OperatorDecisionReader.Everything);
        var item = Assert.Single(projected.Items.Where(item => item.Type == OperatorDecisionTypes.EnvironmentIncidentOpen));
        Assert.Equal(item, projected.Items[0]);
        Assert.Equal("NS-2", item.CardKey);
        Assert.Contains(item.Evidence, link => link.Href == "/#/tasks/NS-2");
        Assert.Contains("Production", item.Title);
        Assert.Contains("HTTP 503", item.Reason);
        Assert.Contains(item.Evidence, link => link.Href.Contains("/operations"));
        Assert.DoesNotContain(projected.Items, item => item.Type == OperatorDecisionTypes.DeployCompleted);

        state.Environments["production"].OpenIncident = null;
        store.Save("alpha", state);
        Assert.Empty(source.Collect(At.AddMinutes(1))!);
    }

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
    public void Archived_project_facts_do_not_need_attention()
    {
        var facts = new[]
        {
            Fact(OperatorDecisionTypes.HumanReviewReady, "archived", "TLY-1", At),
            Fact(OperatorDecisionTypes.HumanReviewReady, "active", "ACT-1", At),
            Fact(OperatorDecisionTypes.QuotaExhausted, null, null, At),
        };

        var result = OperatorAttention.Project(facts, OperatorDecisionReader.Everything,
            project => project != "archived");

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result.Items, item => item.CardKey == "TLY-1");
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

    [Theory]
    [InlineData(OperatorDecisionTypes.HumanReviewReady, TaskStates.HumanReview)]
    [InlineData(OperatorDecisionTypes.EscalationOpened, TaskStates.Escalated)]
    [InlineData(OperatorDecisionTypes.QuotaFallbackActive, TaskStates.Progress)]
    [InlineData(OperatorDecisionTypes.PushBlocked, TaskStates.AutoReview)]
    [InlineData(OperatorDecisionTypes.OverlapStarved, TaskStates.Ready)]
    public void A_card_occurrence_closes_when_deleted_moved_archived_or_a_fixture(string type, string lane)
    {
        var fact = Fact(type, "alpha", "A-1", At);
        var card = Card("A-1", lane);
        bool Open(params TaskInfo[] cards) => new OperatorAttentionBoard(cards, _ => null, _ => null).IsOpen(fact);
        Assert.True(Open(card));
        Assert.False(Open());
        Assert.False(Open(card with { State = TaskStates.Backlog }));
        Assert.False(Open(card with { State = TaskStates.Archive }));
        Assert.False(Open(card with { Fixture = true }));
        Assert.False(Open(card with { ProjectName = "other" }));
    }

    [Fact]
    public void A_quota_wait_closes_at_its_own_reset_or_after_a_later_run()
    {
        var wait = new ModelSelectionEvent { Kind = "waiting", At = At, TaskKey = "A-1", Project = "alpha",
            Quota = new("codex", "week", null, null, null, At.AddHours(1), null) };
        var fact = Assert.Single(OperatorProviderLimitFactSource.FromWaits([wait], At));
        Assert.Empty(OperatorProviderLimitFactSource.FromWaits([wait], At.AddHours(1)));
        var observed = new AgentStudio.Shared.QuotaSnapshot { CliType = "codex", FetchedAt = At.AddMinutes(3),
            Windows = [new() { Label = "week", ObservedStartAt = At.AddMinutes(2) }] };
        Assert.Empty(OperatorProviderLimitFactSource.FromWaits([wait], observed.FetchedAt, _ => observed));
        Assert.Single(OperatorProviderLimitFactSource.FromWaits([wait], observed.FetchedAt, _ => observed with { Suspicious = true }));
        var card = Card("A-1", TaskStates.Ready);
        Assert.True(new OperatorAttentionBoard([card], _ => At, _ => null).IsOpen(fact));
        Assert.False(new OperatorAttentionBoard([card], _ => At.AddSeconds(1), _ => null).IsOpen(fact));
        Assert.False(new OperatorAttentionBoard([], _ => null, _ => null).IsOpen(fact));
    }

    [Fact]
    public void A_red_batch_requires_a_member_still_waiting_for_that_batch()
    {
        var fact = Fact(OperatorDecisionTypes.BatchRed, "alpha", null, At) with { BatchId = "old", Cards = ["A-1"] };
        var card = Card("A-1", TaskStates.AutoReview);
        Assert.True(new OperatorAttentionBoard([card], _ => null, _ => "old").IsOpen(fact));
        Assert.False(new OperatorAttentionBoard([card], _ => null, _ => "successor").IsOpen(fact));
        Assert.False(new OperatorAttentionBoard([card], _ => null, _ => null).IsOpen(fact));
        Assert.False(new OperatorAttentionBoard([], _ => null, _ => "old").IsOpen(fact));
    }

    [Fact]
    public void A_later_green_clears_unresolved_red_incidents_only_in_its_own_project()
    {
        var question = new PostMergeOwnerQuestion(["Refuse"], "Refuse", "Review first", "red", "abc");
        var incident = new PostMergeIncident("test", "repair", "repair", At, null, null, [], [],
            OwnerAskedAtUtc: At, OwnerQuestion: question);
        var green = new PostMergeRunRecord("green", "alpha", "window", "def", null, null, null, 1,
            [], [], "green", At, At.AddMinutes(1));
        var red = PostMergeProjectState.Empty("beta") with { DevelopState = "red", Incidents = [incident] };
        var source = new OperatorPostMergeRedIncidentFactSource(() => [
            ("alpha", red with { Project = "alpha", DevelopState = "green", Runs = [green] }), ("beta", red)]);
        Assert.Equal("beta", Assert.Single(source.Collect(At.AddHours(2))).Project);
    }

    [Fact]
    public void November_27_fixture_reconciles_each_of_the_722_reported_rows()
    {
        var cards = new List<TaskInfo>();
        var raw = new List<OperatorDecisionFact>();
        var waits = new List<ModelSelectionEvent>();
        for (int i = 0; i < 239; i++)
        {
            var project = i < 234 ? "Studio" : "Other";
            var card = Card($"quota-{i}", TaskStates.Ready) with { ProjectName = project };
            cards.Add(card);
            waits.Add(new ModelSelectionEvent { Kind = "waiting", TaskKey = card.Id, Project = project,
                Id = card.Id, At = At, ReturnToPrimaryAt = i < 200 ? At.AddMinutes(1) : null });
        }
        raw.AddRange(OperatorProviderLimitFactSource.FromWaits(waits, At));
        void AddCards(string type, string lane, int total, int deleted, int studio)
        {
            for (int i = 0; i < total; i++)
            {
                var project = i < deleted ? "Runbook" : i < deleted + studio ? "Studio" : "Other";
                var card = Card($"{type}-{i}", lane) with { ProjectName = project };
                if (i >= deleted) cards.Add(card);
                raw.Add(Fact(type, project, card.Id, At));
            }
        }
        AddCards(OperatorDecisionTypes.HumanReviewReady, TaskStates.HumanReview, 230, 140, 78);
        AddCards(OperatorDecisionTypes.EscalationOpened, TaskStates.Escalated, 102, 93, 9);
        for (int i = 0; i < 125; i++)
            raw.Add(Fact(OperatorDecisionTypes.BatchRed, "Studio", $"former-{i}", At)
                with { BatchId = $"batch-{i}", Cards = [$"former-{i}"] });
        for (int i = 0; i < 21; i++)
            raw.Add(Fact(i % 2 == 0 ? OperatorDecisionTypes.PostMergeRedIncident : OperatorDecisionTypes.PostMergeRunsMissing,
                "Studio", $"repair-{i}", At));
        for (int i = 0; i < 5; i++)
            raw.Add(Fact(new[] { OperatorDecisionTypes.HostDiskPressure, OperatorDecisionTypes.BackendStalled,
                OperatorDecisionTypes.ProgressIdleWithReady }[i % 3], null, $"host-{i}", At));

        Assert.Equal(722, raw.Select(fact => fact.Key).Distinct().Count());
        var board = new OperatorAttentionBoard(cards, card => card.ProjectName == "Studio" ? At.AddMinutes(2) : null, _ => null);
        var current = raw.Where(fact => fact.Type != OperatorDecisionTypes.QuotaExhausted)
            .Concat(OperatorProviderLimitFactSource.FromWaits(waits, At.AddHours(1)));
        var result = OperatorAttention.Project(current, OperatorDecisionReader.Everything, board: board);
        Assert.Equal(99, result.DecisionCount);
        Assert.Equal(11, result.NoticeCount);
        Assert.Equal(5, result.Items.Count(item => item.Type == OperatorDecisionTypes.QuotaExhausted));
    }

    [Fact]
    public void Dismissal_survives_restart_and_failed_observations_until_the_cause_clears()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        var audit = new ControlPlaneAuditLog(_root, _clock);
        var fact = Fact(OperatorDecisionTypes.HostDiskPressure, null, null, At);
        var row = Assert.Single(OperatorAttention.Project([fact], OperatorDecisionReader.Everything).Items);
        new OperatorAttentionDismissals(config, audit).Dismiss([row], "operator", "Capacity ticket opened");
        var store = new OperatorAttentionDismissals(config, audit);
        store.Observe([], new HashSet<string>());
        store.Observe([fact], new HashSet<string> { fact.Type });
        Assert.Contains(row.Id, store.Hidden());
        store.Observe([], new HashSet<string> { fact.Type });
        store.Observe([fact], new HashSet<string> { fact.Type });
        Assert.Empty(store.Hidden());
        Assert.Contains("Capacity ticket opened", File.ReadAllText(audit.FilePath));
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
