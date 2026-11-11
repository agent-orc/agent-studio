using AgentStudio.Git;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix tests for the board source policies (pipeline delivery
/// target, M1 and M2, decision D7 A-B): the setting, the mirror delta, the
/// shadow comparison, the cutover gate, the task-server composition and the
/// difference ledger.
/// </summary>
public sealed class BoardSourcePolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, BoardSourceMode.Workspace, null)]
    [InlineData("", BoardSourceMode.Workspace, null)]
    [InlineData("workspace", BoardSourceMode.Workspace, null)]
    [InlineData(" Task-Server-Shadow ", BoardSourceMode.TaskServerShadow, null)]
    [InlineData("task-server", BoardSourceMode.TaskServer, null)]
    [InlineData("taskserver", BoardSourceMode.Workspace, "taskserver")]
    public void Setting_resolves_known_values_and_falls_back_to_the_workspace(
        string? configured, BoardSourceMode mode, string? rejected)
    {
        var resolution = BoardSourceSetting.Resolve(configured);
        Assert.Equal(mode, resolution.Mode);
        Assert.Equal(rejected, resolution.Rejected);
        Assert.Equal(BoardSourceSetting.Format(mode), resolution.Configured);
    }

    [Fact]
    public void Card_core_uses_the_upper_cased_key_and_falls_back_to_the_id_and_key_for_a_blank_title()
    {
        Assert.Equal(new BoardCardCore("P", "AGT-1", "Title", TaskStates.Ready, 3),
            BoardCardCore.From(Info("P", "x", "agt-1", "Title", TaskStates.Ready, 3)));
        Assert.Equal(new BoardCardCore("P", "FOLDER-ID", "FOLDER-ID", TaskStates.Backlog, 999),
            BoardCardCore.From(Info("P", "folder-id", null, " ", TaskStates.Backlog, 999)));
    }

    [Fact]
    public void Mirror_plan_sends_a_new_project_whole_then_only_changed_and_removed_cards()
    {
        var first = Group(Card("P", "A-1", TaskStates.Backlog), Card("P", "A-2", TaskStates.Ready));
        var initial = Assert.Single(BoardMirrorPlan.Create(Empty(), first));
        Assert.Equal(["A-1", "A-2"], initial.Upserts.Select(card => card.TaskKey));
        Assert.Empty(initial.Removals);

        Assert.Empty(BoardMirrorPlan.Create(first, first));

        var moved = Group(Card("P", "A-1", TaskStates.Ready), Card("Q", "B-1", TaskStates.Backlog));
        var deltas = BoardMirrorPlan.Create(first, moved);
        var p = deltas.Single(delta => delta.Project == "P");
        Assert.Equal(["A-1"], p.Upserts.Select(card => card.TaskKey));
        Assert.Equal(["A-2"], p.Removals);
        Assert.Equal(["B-1"], deltas.Single(delta => delta.Project == "Q").Upserts.Select(card => card.TaskKey));

        var vanished = Assert.Single(BoardMirrorPlan.Create(Group(Card("P", "A-1", TaskStates.Ready)), Empty()));
        Assert.Equal(("P", 0, 1), (vanished.Project, vanished.Upserts.Count, vanished.Removals.Count));
    }

    [Fact]
    public void Comparison_of_equal_sources_reports_nothing()
    {
        var cards = Group(Card("P", "A-1", TaskStates.Ready, rank: 2), Card("P", "A-2", TaskStates.Archive))["P"];
        var model = Model(archived: 1, ServerCard("A-1", TaskStates.Ready, rank: 2));
        Assert.Empty(BoardShadowComparison.Compare("P", cards, model));
    }

    [Fact]
    public void Comparison_names_card_and_field_for_every_disagreement()
    {
        var cards = Group(
            Card("P", "A-1", TaskStates.Ready, rank: 2, title: "Mine"),
            Card("P", "A-2", TaskStates.Backlog),
            Card("P", "A-3", TaskStates.Archive))["P"];
        var model = Model(archived: 0,
            ServerCard("A-1", TaskStates.Progress, rank: 5, title: "Theirs"),
            ServerCard("A-9", TaskStates.Backlog));

        var differences = BoardShadowComparison.Compare("P", cards, model)
            .Select(difference => (difference.TaskKey, difference.Field, difference.WorkspaceValue, difference.TaskServerValue))
            .ToList();

        Assert.Contains(("A-1", BoardShadowComparison.LaneField, TaskStates.Ready, TaskStates.Progress), differences);
        Assert.Contains(("A-1", BoardShadowComparison.TitleField, "Mine", "Theirs"), differences);
        Assert.Contains(("A-1", BoardShadowComparison.OrderField, "2", "5"), differences);
        Assert.Contains(("A-2", BoardShadowComparison.PresenceField, TaskStates.Backlog, "absent"), differences);
        Assert.Contains(("A-9", BoardShadowComparison.PresenceField, "absent", TaskStates.Backlog), differences);
        Assert.Contains(($"lane:{TaskStates.Ready}", BoardShadowComparison.CountField, "1", "0"), differences);
        Assert.Contains(($"lane:{TaskStates.Progress}", BoardShadowComparison.CountField, "0", "1"), differences);
        Assert.Contains((BoardShadowComparison.AnyCard, BoardShadowComparison.ArchivedCountField, "1", "0"), differences);
        Assert.Equal(
            (BoardShadowComparison.AnyCard, BoardShadowComparison.ProjectField),
            BoardShadowComparison.Compare("P", cards, null).Select(d => (d.TaskKey, d.Field)).Single());
    }

    [Theory]
    [InlineData(BoardSourceMode.Workspace, 8, 0, null, BoardCutoverReadinessStates.ShadowOff)]
    [InlineData(BoardSourceMode.TaskServer, 8, 0, null, BoardCutoverReadinessStates.Switched)]
    [InlineData(BoardSourceMode.TaskServerShadow, null, null, null, BoardCutoverReadinessStates.NoComparisons)]
    [InlineData(BoardSourceMode.TaskServerShadow, 8, 2, null, BoardCutoverReadinessStates.ComparisonsStalled)]
    [InlineData(BoardSourceMode.TaskServerShadow, 6, 0, null, BoardCutoverReadinessStates.CleanWindowOpen)]
    [InlineData(BoardSourceMode.TaskServerShadow, 8, 0, 3, BoardCutoverReadinessStates.CleanWindowOpen)]
    [InlineData(BoardSourceMode.TaskServerShadow, 8, 0, 7, BoardCutoverReadinessStates.Ready)]
    [InlineData(BoardSourceMode.TaskServerShadow, 7, 0, null, BoardCutoverReadinessStates.Ready)]
    public void Cutover_gate_needs_seven_continuous_days_without_an_unexplained_difference(
        BoardSourceMode mode, int? comparingForDays, int? lastComparisonDaysAgo, int? lastUnexplainedDaysAgo, string state)
    {
        var now = Start;
        var readiness = BoardCutoverReadinessPolicy.Evaluate(
            mode,
            now,
            comparingForDays is { } days ? now.AddDays(-days) : null,
            lastComparisonDaysAgo is { } ago ? now.AddDays(-ago) : null,
            lastUnexplainedDaysAgo is { } seen ? now.AddDays(-seen) : null);
        Assert.Equal(state, readiness.State);
        Assert.Equal(state == BoardCutoverReadinessStates.Ready, readiness.Ready);
    }

    [Fact]
    public void Continuous_comparison_restarts_after_a_gap_longer_than_a_day()
    {
        var since = Start.AddDays(-3);
        Assert.Equal(since, BoardCutoverReadinessPolicy.NextContinuousSince(Start, since, Start.AddHours(-23)));
        Assert.Equal(Start, BoardCutoverReadinessPolicy.NextContinuousSince(Start, since, Start.AddHours(-25)));
        Assert.Equal(Start, BoardCutoverReadinessPolicy.NextContinuousSince(Start, null, null));
    }

    [Fact]
    public void Task_server_composition_takes_lane_title_and_order_from_the_task_server_and_enriches_known_cards()
    {
        var known = Info("P", "folder-1", "A-1", "Old title", TaskStates.Backlog, 4) with { Agent = "claude", WatchPath = "/w" };
        var model = Model(archived: 0,
            ServerCard("A-1", TaskStates.Ready, rank: 1, title: "New title"),
            ServerCard("A-2", TaskStates.Backlog, rank: 0, title: "Server only"));

        var tasks = TaskServerBoardComposition.Compose([("P", model)], [known], _ => "/w");

        var enriched = tasks.Single(task => task.Key == "A-1");
        Assert.Equal((TaskStates.Ready, "New title", 1, "claude", "folder-1"),
            (enriched.State, enriched.Title, enriched.Order, enriched.Agent, enriched.Id));
        var bare = tasks.Single(task => task.Key == "A-2");
        Assert.Equal(("A-2", "/w::A-2", "P", "/w"), (bare.Id, bare.TaskKey, bare.ProjectName, bare.WatchPath));
    }

    [Fact]
    public void Board_read_acceptance_needs_a_p95_under_200_ms_and_no_git_spawn()
    {
        Assert.False(BoardReadAcceptance.From(null).Met);
        var met = BoardReadAcceptance.From(new EndpointStats("tasks/grouped", 40, 20, 150, 0, 0, 40));
        Assert.Equal((true, 0d), (met.Met, met.SpawnsPerRequest));
        Assert.False(BoardReadAcceptance.From(new EndpointStats("tasks/grouped", 40, 20, 250, 0, 0, 40)).Met);
        var spawning = BoardReadAcceptance.From(new EndpointStats("tasks/grouped", 40, 20, 150, 10, 1, 40));
        Assert.Equal((false, 0.25), (spawning.Met, spawning.SpawnsPerRequest));
    }

    [Fact]
    public void Ledger_tracks_differences_explanations_and_presence_and_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "board-shadow-ledger-" + Guid.NewGuid().ToString("N"), BoardShadowLedger.FileName);
        try
        {
            var clock = new FakeTimeProvider(Start);
            var ledger = new BoardShadowLedger(path, clock, NullLogger<BoardShadowLedger>.Instance);
            var shadow = BoardSourceSetting.Resolve(BoardSourceSetting.TaskServerShadow);
            var title = new BoardShadowDifference("P", "A-1", BoardShadowComparison.TitleField, "Mine", "Theirs");

            ledger.RecordComparisonRequest();
            ledger.RecordComparison(5, ["P"], [title]);
            var first = ledger.Snapshot(shadow);
            Assert.Equal((1L, 1L, 1, 0), (first.ComparisonRequests, first.Comparisons, first.UnexplainedDifferences, first.ExplainedDifferences));
            Assert.Equal("P|A-1|title", Assert.Single(first.Differences).Fingerprint);
            Assert.Equal(BoardCutoverReadinessStates.CleanWindowOpen, first.Readiness.State);

            clock.Advance(TimeSpan.FromHours(1));
            ledger.RecordComparison(6, ["P"], []);
            Assert.False(Assert.Single(ledger.Snapshot(shadow).Differences).Present);

            Assert.False(ledger.Explain("P|A-9|title", "unknown", "operator"));
            Assert.True(ledger.Explain(title.Fingerprint, "Title trimmed by the importer", "operator"));
            var explained = ledger.Snapshot(shadow);
            Assert.Equal((0, 1), (explained.UnexplainedDifferences, explained.ExplainedDifferences));

            // 1 h + 8 x 23 h = 185 h: past the 168 h window, never a gap over a day.
            for (var day = 0; day < 8; day++)
            {
                clock.Advance(TimeSpan.FromHours(23));
                ledger.RecordComparison(7 + day, ["P"], []);
            }
            Assert.True(ledger.Snapshot(shadow).Readiness.Ready);

            var reloaded = new BoardShadowLedger(path, clock, NullLogger<BoardShadowLedger>.Instance).Snapshot(shadow);
            Assert.Equal((1L, 10L, 1), (reloaded.ComparisonRequests, reloaded.Comparisons, reloaded.ExplainedDifferences));
            Assert.True(reloaded.Readiness.Ready);

            ledger.RecordComparison(20, ["P"], [title with { TaskServerValue = "Other" }]);
            var reopened = ledger.Snapshot(shadow);
            Assert.Equal(1, reopened.UnexplainedDifferences);
            Assert.False(reopened.Readiness.Ready);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Ledger_starts_a_fresh_window_from_an_unreadable_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "board-shadow-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BoardShadowLedger.FileName);
        File.WriteAllText(path, "{ not json");
        try
        {
            var ledger = new BoardShadowLedger(path, new FakeTimeProvider(Start), NullLogger<BoardShadowLedger>.Instance);
            var snapshot = ledger.Snapshot(BoardSourceSetting.Resolve("bogus"));
            Assert.Equal((0L, "bogus", BoardSourceSetting.Workspace), (snapshot.Comparisons, snapshot.RejectedSetting, snapshot.Source));
            Assert.Equal(BoardCutoverReadinessStates.ShadowOff, snapshot.Readiness.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reader_fails_fast_after_a_failed_read_and_serves_a_304_from_its_cached_model()
    {
        var clock = new FakeTimeProvider(Start);
        var handler = new StubHandler();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Board:TaskServer:BaseUrl"] = "http://localhost/" })
            .Build();
        var reader = new TaskServerBoardReader(new TaskServerBoardClient(new StubFactory(handler), configuration), clock);

        handler.Next = _ => throw new HttpRequestException("connection refused");
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync("P", default));
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync("P", default));
        Assert.Equal(1, handler.Calls);

        clock.Advance(TaskServerBoardReader.OpenFor + TimeSpan.FromSeconds(1));
        var model = Model(archived: 0, ServerCard("A-1", TaskStates.Ready));
        handler.Next = request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = System.Net.Http.Json.JsonContent.Create(model),
        };
        Assert.Equal(model.ETag, (await reader.ReadAsync("P", default))!.ETag);

        handler.Next = request =>
        {
            Assert.Equal(model.ETag, request.Headers.IfNoneMatch.Single().ToString());
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotModified);
        };
        Assert.Same(reader.Cached("P"), await reader.ReadAsync("P", default));
        handler.Next = _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        Assert.Null(await reader.ReadAsync("P", default));
        Assert.Null(reader.Cached("P"));
        Assert.Equal(4, handler.Calls);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Next { get; set; } = _ => new HttpResponseMessage();
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Next(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
    }

    private static TaskInfo Info(string project, string id, string? key, string title, string state, int order)
        => new() { ProjectName = project, Id = id, Key = key, Title = title, State = state, Order = order };

    private static BoardCardCore Card(string project, string key, string state, long rank = 0, string? title = null)
        => new(project, key, title ?? key, state, rank);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, BoardCardCore>> Group(params BoardCardCore[] cards)
        => cards.GroupBy(card => card.Project, StringComparer.OrdinalIgnoreCase).ToDictionary(
            group => group.Key,
            group => (IReadOnlyDictionary<string, BoardCardCore>)group.ToDictionary(card => card.TaskKey, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, BoardCardCore>> Empty()
        => new Dictionary<string, IReadOnlyDictionary<string, BoardCardCore>>(StringComparer.OrdinalIgnoreCase);

    private static BoardCardDto ServerCard(string key, string state, long rank = 0, string? title = null)
        => new($"tsk-{key}", key, title ?? key, state, rank, 1, null, Start.UtcDateTime);

    private static BoardReadModelResponse Model(int archived, params BoardCardDto[] cards)
    {
        var lanes = cards.GroupBy(card => card.State)
            .Select(group => new BoardLaneDto(group.Key, group.Count(), group.ToList()))
            .ToList();
        return new BoardReadModelResponse("prj", "P", 1, "\"board-v1-g1\"", lanes, cards.Length, archived, 1);
    }
}
