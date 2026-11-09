using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F35: per-lane sort strategy applied to the kanban grouped endpoint.
/// Covers strategy resolution (defaults, overrides, invalid values),
/// each comparator's order, key-aware semantic ordering for newest/oldest,
/// the runner-pickup strategy, and that ProjectSettingsService persists
/// overrides across reload.
/// </summary>
public sealed class LaneSortStrategyTests : IDisposable
{
    private readonly string _workspace;

    public LaneSortStrategyTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-lane-sort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData(TaskStates.Backlog)]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.OrchestratorPrep)]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    [InlineData(TaskStates.FailedPickup)]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void EveryLaneDefaultsToLaneEntry(string lane)
    {
        Assert.Equal(LaneSortStrategies.LaneEntry, LaneSortStrategies.GetDefaultForLane(lane));
    }

    [Fact]
    public void Resolve_UsesOverrideWhenSet()
    {
        var settings = new ProjectSettings
        {
            LaneSortStrategyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TaskStates.Ready] = LaneSortStrategies.Manual,
            },
        };
        Assert.Equal(LaneSortStrategies.Manual,
            LaneSortStrategies.Resolve(settings, TaskStates.Ready));
    }

    [Fact]
    public void Resolve_FallsBackToDefaultForUnknownOverride()
    {
        var settings = new ProjectSettings
        {
            LaneSortStrategyOverrides = new Dictionary<string, string>
            {
                [TaskStates.Ready] = "bogus-strategy",
            },
        };
        Assert.Equal(LaneSortStrategies.LaneEntry,
            LaneSortStrategies.Resolve(settings, TaskStates.Ready));
    }

    [Fact]
    public void NewestFirstComparator_OrdersByKeyDescThenCreatedAtDesc()
    {
        var jobs = new[]
        {
            J("a", key: "ATP-3", createdAt: T(1)),
            J("b", key: "ATP-10", createdAt: T(2)),
            J("c", key: "ATP-2", createdAt: T(3)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.NewestFirst);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        // ATP-10 has the largest numeric suffix, so it sits on top.
        Assert.Equal(new[] { "b", "a", "c" }, sorted);
    }

    [Fact]
    public void OldestFirstComparator_OrdersByKeyAsc()
    {
        var jobs = new[]
        {
            J("a", key: "ATP-3", createdAt: T(1)),
            J("b", key: "ATP-10", createdAt: T(2)),
            J("c", key: "ATP-2", createdAt: T(3)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.OldestFirst);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "c", "a", "b" }, sorted);
    }

    [Fact]
    public void LastActivityComparator_OrdersByLastActivityDesc()
    {
        var jobs = new[]
        {
            J("a", lastActivity: T(1), order: 99),
            J("b", lastActivity: T(3), order: 1),
            J("c", lastActivity: T(2), order: 50),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.LastActivity);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "b", "c", "a" }, sorted);
    }

    [Fact]
    public void PickupPriorityComparator_OrdersByOrderAscThenLastActivityAsc()
    {
        var jobs = new[]
        {
            J("a", order: 5, lastActivity: T(2)),
            J("b", order: 1, lastActivity: T(3)),
            J("c", order: 1, lastActivity: T(1)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.PickupPriority);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "c", "b", "a" }, sorted);
    }

    [Fact]
    public void ManualComparator_OrdersByOrderAscThenKeyDescAsTiebreaker()
    {
        var jobs = new[]
        {
            J("a", order: 2, key: "ATP-1"),
            J("b", order: 1, key: "ATP-7"),
            J("c", order: 1, key: "ATP-3"),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.Manual);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "b", "c", "a" }, sorted);
    }

    [Fact]
    public void LaneEntryComparator_OrdersByEnteredLaneAtDescIgnoringCreatedAt()
    {
        // All cards are unpinned (sentinel order). The most recently entered
        // lane sits on top even when its CreatedAt is the oldest — acceptance (a).
        var jobs = new[]
        {
            J("a", order: LaneSortStrategies.UnpinnedOrder, createdAt: T(100), enteredLaneAt: T(1)),
            J("b", order: LaneSortStrategies.UnpinnedOrder, createdAt: T(1), enteredLaneAt: T(3)),
            J("c", order: LaneSortStrategies.UnpinnedOrder, createdAt: T(50), enteredLaneAt: T(2)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.LaneEntry);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "b", "c", "a" }, sorted);
    }

    [Fact]
    public void LaneEntryComparator_PinnedCardStaysOnTopWhileOthersFlowByEntryTime()
    {
        // "p" was dragged (explicit order) so it pins to the top even though it
        // entered the lane first; the unpinned cards flow by entry desc below
        // it — acceptance (b): drag overrides the time-based flow.
        var jobs = new[]
        {
            J("x", order: LaneSortStrategies.UnpinnedOrder, enteredLaneAt: T(5)),
            J("p", order: 1, enteredLaneAt: T(1)),
            J("y", order: LaneSortStrategies.UnpinnedOrder, enteredLaneAt: T(3)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.LaneEntry);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "p", "x", "y" }, sorted);
    }

    [Fact]
    public void LaneEntryComparator_MultiplePinnedClusterByOrderAboveUnpinned()
    {
        // Two dragged cards cluster on top by order asc regardless of entry
        // time; the unpinned card flows below them.
        var jobs = new[]
        {
            J("p2", order: 2, enteredLaneAt: T(9)),
            J("u", order: LaneSortStrategies.UnpinnedOrder, enteredLaneAt: T(8)),
            J("p1", order: 1, enteredLaneAt: T(1)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.LaneEntry);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal(new[] { "p1", "p2", "u" }, sorted);
    }

    [Fact]
    public void NullKey_PushedToBottomInKeyBasedStrategies()
    {
        var jobs = new[]
        {
            J("a", key: null, createdAt: T(5)),
            J("b", key: "ATP-1"),
            J("c", key: null, createdAt: T(2)),
        };
        var cmp = LaneSortStrategies.GetComparer(LaneSortStrategies.NewestFirst);
        var sorted = jobs.OrderBy(j => j, cmp).Select(j => j.Id).ToArray();
        Assert.Equal("b", sorted[0]);
        Assert.Contains("a", sorted[1..]);
        Assert.Contains("c", sorted[1..]);
    }

    [Fact]
    public void LaneSortApplier_GroupsByProjectAndSortsEachIndependently()
    {
        var jobs = new[]
        {
            J("a", project: "Alpha", key: "ALP-3"),
            J("b", project: "Bravo", key: "BRA-1", order: 5),
            J("c", project: "Alpha", key: "ALP-7"),
            J("d", project: "Bravo", key: "BRA-9", order: 1),
        };
        ProjectSettings Resolver(string projectName)
        {
            // Alpha overrides to newest-first; Bravo overrides to manual. Both
            // are explicit so this test isolates per-project grouping rather
            // than tracking whatever the lane default happens to be.
            var strategy = string.Equals(projectName, "Bravo", StringComparison.OrdinalIgnoreCase)
                ? LaneSortStrategies.Manual
                : LaneSortStrategies.NewestFirst;
            return new ProjectSettings
            {
                LaneSortStrategyOverrides = new Dictionary<string, string>
                {
                    [TaskStates.Ready] = strategy,
                },
            };
        }

        var sorted = LaneSortApplier
            .Sort(jobs, TaskStates.Ready, Resolver)
            .Select(j => j.Id)
            .ToArray();

        // Alpha first (alphabetical project order): newest-first → ALP-7 above ALP-3.
        // Then Bravo: manual → BRA-9 (order=1) above BRA-1 (order=5).
        Assert.Equal(new[] { "c", "a", "d", "b" }, sorted);
    }

    [Fact]
    public void PickupOrder_KeepsEachProjectsDisplayedOrderAndOffersEveryHeadFirst()
    {
        // AGT-3587: the remote claim used Order then CreatedAt across all
        // projects, so an unplaced card was claimed oldest-created first while
        // the board and the local runner showed the newest lane entry on top.
        var jobs = new[]
        {
            J("alpha-old", project: "Alpha", order: LaneSortStrategies.UnpinnedOrder, createdAt: T(0), enteredLaneAt: T(10)),
            J("alpha-new", project: "Alpha", order: LaneSortStrategies.UnpinnedOrder, createdAt: T(5), enteredLaneAt: T(50)),
            J("bravo-2", project: "Bravo", order: 20),
            J("bravo-1", project: "Bravo", order: 10),
            J("alpha-placed", project: "Alpha", order: 300),
        };

        var board = LaneSortApplier.Sort(jobs, TaskStates.Ready, _ => new ProjectSettings())
            .Select(j => j.Id).ToArray();
        var pickup = LaneSortApplier.PickupOrder(jobs, _ => new ProjectSettings())
            .Select(j => j.Id).ToArray();

        Assert.Equal(new[] { "alpha-placed", "alpha-new", "alpha-old", "bravo-1", "bravo-2" }, board);
        // Heads first (Bravo's order 10 before Alpha's 300), then second cards.
        Assert.Equal(new[] { "bravo-1", "alpha-placed", "bravo-2", "alpha-new", "alpha-old" }, pickup);
        foreach (var project in new[] { "Alpha", "Bravo" })
        {
            Assert.Equal(
                board.Where(id => id.StartsWith(project.ToLowerInvariant(), StringComparison.Ordinal)),
                pickup.Where(id => id.StartsWith(project.ToLowerInvariant(), StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void ProjectSettingsService_SetLaneSortStrategyPersistsAcrossReload()
    {
        var svc = BuildSettings();
        svc.SetLaneSortStrategy("acme", TaskStates.Ready, LaneSortStrategies.Manual);

        var reloaded = BuildSettings();
        var s = reloaded.Get("acme");

        Assert.NotNull(s.LaneSortStrategyOverrides);
        Assert.Equal(LaneSortStrategies.Manual, s.LaneSortStrategyOverrides![TaskStates.Ready]);
        Assert.Equal(LaneSortStrategies.Manual, LaneSortStrategies.Resolve(s, TaskStates.Ready));
        // Lanes without an override still resolve via defaults.
        Assert.Equal(LaneSortStrategies.LaneEntry, LaneSortStrategies.Resolve(s, TaskStates.Backlog));
    }

    [Fact]
    public void ProjectSettingsService_NullStrategyClearsOverride()
    {
        var svc = BuildSettings();
        svc.SetLaneSortStrategy("acme", TaskStates.Ready, LaneSortStrategies.Manual);
        svc.SetLaneSortStrategy("acme", TaskStates.Ready, null);

        var s = svc.Get("acme");
        Assert.Null(s.LaneSortStrategyOverrides);
        Assert.Equal(LaneSortStrategies.LaneEntry, LaneSortStrategies.Resolve(s, TaskStates.Ready));
    }

    private ProjectSettingsService BuildSettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }

    private static DateTime T(int seconds) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

    private static TaskInfo J(
        string id,
        string? key = null,
        string? project = null,
        int order = 1,
        DateTime? createdAt = null,
        DateTime? lastActivity = null,
        DateTime? enteredLaneAt = null)
    {
        return new TaskInfo
        {
            Id = id,
            TaskKey = (project ?? "TestProject") + "::" + id,
            Key = key,
            Title = id,
            Order = order,
            State = TaskStates.Ready,
            ProjectName = project ?? "TestProject",
            WatchPath = "/tmp/" + (project ?? "TestProject"),
            CreatedAt = createdAt ?? T(0),
            LastActivity = lastActivity ?? createdAt ?? T(0),
            EnteredLaneAt = enteredLaneAt ?? lastActivity ?? createdAt ?? T(0),
        };
    }
}
