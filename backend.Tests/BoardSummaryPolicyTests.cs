using System.Text;
using System.Text.Json;

using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The pure decisions of the persisted board (Git facts S5): row building,
/// the write plan, restored selection and body composition.
/// </summary>
public sealed class BoardSummaryPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static TaskInfo Card(string id, string state, string project = "alpha", bool fixture = false)
        => new()
        {
            Id = id, Title = id, State = state, ProjectName = project, Fixture = fixture,
            WatchPath = "/w/" + project, TaskKey = "/w/" + project + "::" + id,
        };

    private static byte[] Bytes(TaskInfo card) => Encoding.UTF8.GetBytes($"{{\"id\":\"{card.Id}\"}}");

    private static BoardSummaryCard Row(string id, string state, int laneRank, int scanRank,
        string project = "alpha", bool fixture = false)
        => new(project, "/w/" + project + "::" + id, "/w/" + project, state, fixture, laneRank, scanRank,
            Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\"}}"));

    private static BoardSummaryProject Project(string name, params BoardSummaryCard[] cards)
        => new(name, 7, At, At.AddMinutes(-1), false, BoardSummaryPolicy.ContentHash(cards, At.AddMinutes(-1), false), cards);

    private static string[] Ids(JsonElement lane) => lane.EnumerateArray().Select(card => card.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public void Build_records_index_order_scan_ranks_and_the_lane_sort_rank()
    {
        var cards = new[] { Card("a", TaskStates.Ready), Card("b", TaskStates.Backlog), Card("c", TaskStates.Ready) };
        // The lane sort reverses Ready: c before a.
        IReadOnlyList<TaskInfo> SortLane(string lane) =>
            cards.Where(card => card.State == lane).Reverse().ToList();

        var project = BoardSummaryPolicy.Build("alpha", 12, At, null, true, cards, [4, 9, 11], SortLane, Bytes);

        Assert.Equal(["/w/alpha::a", "/w/alpha::b", "/w/alpha::c"], project.Cards.Select(card => card.TaskKey));
        Assert.Equal([4, 9, 11], project.Cards.Select(card => card.ScanRank));
        Assert.Equal([1, 0, 0], project.Cards.Select(card => card.LaneRank));
        Assert.Equal(12, project.IndexGeneration);
        Assert.True(project.GitStale);
        Assert.Equal("{\"id\":\"a\"}", Encoding.UTF8.GetString(project.Cards[0].Summary));
    }

    [Fact]
    public void Content_hash_ignores_the_absolute_index_position_but_not_order_lane_or_bytes()
    {
        var baseline = BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Ready, 0, 1), Row("b", TaskStates.Ready, 1, 2)], At, false);

        Assert.Equal(baseline, BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Ready, 0, 40), Row("b", TaskStates.Ready, 1, 41)], At, false));
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([Row("b", TaskStates.Ready, 1, 2), Row("a", TaskStates.Ready, 0, 1)], At, false));
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Ready, 1, 1), Row("b", TaskStates.Ready, 0, 2)], At, false));
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Backlog, 0, 1), Row("b", TaskStates.Ready, 1, 2)], At, false));
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Ready, 0, 1, fixture: true), Row("b", TaskStates.Ready, 1, 2)], At, false));
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Ready, 0, 1)], At, false));
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Ready, 0, 1), Row("b", TaskStates.Ready, 1, 2)], At, true));
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([Row("a", TaskStates.Ready, 0, 1), Row("b", TaskStates.Ready, 1, 2)], null, false));
        var changedBytes = Row("a", TaskStates.Ready, 0, 1) with { Summary = Encoding.UTF8.GetBytes("{\"id\":\"a\",\"x\":1}") };
        Assert.NotEqual(baseline, BoardSummaryPolicy.ContentHash([changedBytes, Row("b", TaskStates.Ready, 1, 2)], At, false));
    }

    [Theory]
    // persisted hashes, built (name:hash), expected changed, expected removed
    [InlineData("", "alpha:1", "alpha", "")]
    [InlineData("alpha:1", "alpha:1", "", "")]
    [InlineData("alpha:1", "alpha:2", "alpha", "")]
    [InlineData("alpha:1,beta:1", "alpha:1", "", "beta")]
    [InlineData("alpha:1,beta:1", "beta:2,gamma:1", "beta,gamma", "alpha")]
    [InlineData("alpha:1", "", "", "alpha")]
    public void Plan_writes_changed_and_new_projects_and_removes_absent_ones(
        string persisted, string built, string changed, string removed)
    {
        static IEnumerable<(string Name, string Hash)> Parse(string value) => value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(':')).Select(pair => (pair[0], pair[1]));
        var hashes = Parse(persisted).ToDictionary(entry => entry.Name, entry => entry.Hash);
        var projects = Parse(built)
            .Select(entry => new BoardSummaryProject(entry.Name, 1, At, null, false, entry.Hash, []))
            .ToList();

        var plan = BoardSummaryPolicy.Plan(hashes, projects);

        Assert.Equal(changed.Split(',', StringSplitOptions.RemoveEmptyEntries),
            plan.Changed.Select(project => project.ProjectName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(removed.Split(',', StringSplitOptions.RemoveEmptyEntries),
            plan.Removed.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Select_serves_visible_projects_one_watch_path_and_fixtures_only_on_request()
    {
        IReadOnlyList<BoardSummaryProject> projects =
        [
            Project("alpha", Row("a1", TaskStates.Ready, 0, 0), Row("a2", TaskStates.Ready, 1, 1, fixture: true)),
            Project("beta", Row("b1", TaskStates.Backlog, 0, 2, project: "beta")),
            Project("secret", Row("s1", TaskStates.Backlog, 0, 3, project: "secret")),
        ];
        bool Visible(string name) => name != "secret";

        Assert.Equal(["a1", "b1"], Keys(BoardSummaryPolicy.Select(projects, Visible, null, includeFixtures: false)));
        Assert.Equal(["a1", "a2", "b1"], Keys(BoardSummaryPolicy.Select(projects, Visible, null, includeFixtures: true)));
        Assert.Equal(["b1"], Keys(BoardSummaryPolicy.Select(projects, Visible, path => path == "/w/beta", includeFixtures: true)));
        Assert.Empty(BoardSummaryPolicy.Select(projects, _ => false, null, includeFixtures: true));

        static string[] Keys(IReadOnlyList<BoardSummaryCard> cards) => cards.Select(card => card.TaskKey.Split("::")[1]).ToArray();
    }

    [Fact]
    public void Git_stamp_is_the_oldest_among_the_represented_projects()
    {
        IReadOnlyList<BoardSummaryProject> projects =
        [
            Project("alpha", Row("a1", TaskStates.Ready, 0, 0)) with { GitStateAt = At },
            Project("beta", Row("b1", TaskStates.Ready, 0, 1, project: "beta")) with { GitStateAt = At.AddHours(-2) },
            Project("gamma", Row("g1", TaskStates.Ready, 0, 2, project: "gamma")) with { GitStateAt = null },
        ];

        Assert.Equal(At, BoardSummaryPolicy.GitStateAt(projects, projects[0].Cards));
        Assert.Equal(At.AddHours(-2), BoardSummaryPolicy.GitStateAt(projects, projects.SelectMany(p => p.Cards).ToList()));
        Assert.Null(BoardSummaryPolicy.GitStateAt(projects, projects[2].Cards));
    }

    [Fact]
    public void Composed_list_keeps_index_order()
    {
        var body = BoardSummaryPolicy.ComposeList(
            [Row("c", TaskStates.Ready, 0, 9), Row("a", TaskStates.Backlog, 0, 1), Row("b", TaskStates.Ready, 1, 4, project: "beta")]);

        Assert.Equal(["a", "b", "c"], Ids(JsonDocument.Parse(body).RootElement));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Composed_grouped_body_has_the_live_lanes_in_wire_order_and_is_stale(bool legacyReviewLane)
    {
        var body = BoardSummaryPolicy.ComposeGrouped(
        [
            Row("r2", TaskStates.Ready, 1, 0),
            Row("r1", TaskStates.Ready, 0, 1),
            // Another project sorts after alpha whatever its rank.
            Row("rb", TaskStates.Ready, 0, 2, project: "Beta"),
            Row("ar", TaskStates.AutoReview, 0, 3),
            Row("done", TaskStates.Completed, 0, 4),
        ], legacyReviewLane, At);

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(
            ["backlog", "preparation", "orchestratorPrep", "ready", "progress", "failedPickup", "codeNotComplete",
             "autoReview", "humanReview", "escalated", "review", "completed", "archive", "gitStateAt", "stale"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["r1", "r2", "rb"], Ids(root.GetProperty("ready")));
        Assert.Equal(["ar"], Ids(root.GetProperty("autoReview")));
        Assert.Equal(legacyReviewLane ? ["ar"] : [], Ids(root.GetProperty("review")));
        Assert.Equal(["done"], Ids(root.GetProperty("completed")));
        Assert.Empty(Ids(root.GetProperty("archive")));
        Assert.Equal(At, root.GetProperty("gitStateAt").GetDateTimeOffset());
        Assert.True(root.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public void Grouped_lanes_match_the_live_serializer_byte_for_byte()
    {
        var cards = new[] { Card("x", TaskStates.HumanReview), Card("y", TaskStates.AutoReview) };
        var live = BoardCardSummary.SerializeGrouped(
            BoardCardSummary.GroupedLanes<TaskInfo>(lane => cards.Where(card => card.State == lane).ToList(), true),
            At, stale: true, Bytes);
        var restored = BoardSummaryPolicy.ComposeGrouped(
            cards.Select((card, index) => new BoardSummaryCard(card.ProjectName, card.TaskKey, card.WatchPath,
                card.State, false, 0, index, Bytes(card))).ToList(), legacyReviewLane: true, At);

        Assert.Equal(live, restored);
    }
}
