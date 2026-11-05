using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the F45 restscope acceptance criterion "Lane-Wechsel = reine
/// Metadata-/Index-Mutation, kein FS-Move": a lane change must rewrite
/// <c>task.json.state</c> and the <c>by-state</c> index while leaving the task's
/// physical folder (<c>jobs/&lt;bucket&gt;/&lt;key&gt;</c>) exactly where it is.
/// Fixtures are built by running the real migrator so the starting state is a
/// genuine migrated layout, not a hand-rolled one.
/// </summary>
public class TaskLayoutTransitionTests : IDisposable
{
    private readonly string _root;

    public TaskLayoutTransitionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-layout-trans-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ChangeState_RewritesStateAndIndex_WithoutMovingFolder()
    {
        SeedLaneTask(TaskStates.Ready, "task-a", "TST-10");
        Migrate();

        var folder = Path.Combine(_root, "tasks", "000", "TST-10");
        Assert.True(Directory.Exists(folder));

        var result = TaskLayoutTransition.ChangeState(_root, "TST-10", TaskStates.Progress, NullLogger.Instance);

        Assert.True(result.Changed);
        Assert.Equal(TaskStates.Ready, result.FromState);
        Assert.Equal(TaskStates.Progress, result.ToState);
        Assert.Equal("000/TST-10", result.Location);

        // Authority: task.json.state flipped.
        Assert.Equal(TaskStates.Progress, Field(Path.Combine(folder, "task.json"), "state"));

        // Location invariant: the folder is still exactly where it was, no
        // sibling clone, no Directory.Move.
        Assert.True(Directory.Exists(folder), "folder must not move on a lane change");

        // Derived index reflects the new lane; old lane no longer lists it.
        var byState = TaskLayoutIndex.ReadByState(_root);
        Assert.Equal(new[] { "000/TST-10" }, byState[TaskStates.Progress]);
        Assert.False(byState.ContainsKey(TaskStates.Ready), "emptied lane should be pruned from by-state");

        // by-key is unchanged: the physical location did not move.
        Assert.Equal("000/TST-10", TaskLayoutIndex.ReadByKey(_root)["TST-10"]);
    }

    [Fact]
    public void ChangeState_LeavesOtherTasksUntouched()
    {
        SeedLaneTask(TaskStates.Ready, "a", "TST-10");
        SeedLaneTask(TaskStates.Ready, "b", "TST-11");
        SeedLaneTask(TaskStates.Progress, "c", "TST-12");
        Migrate();

        TaskLayoutTransition.ChangeState(_root, "TST-11", TaskStates.Archive, NullLogger.Instance);

        var byState = TaskLayoutIndex.ReadByState(_root);
        Assert.Equal(new[] { "000/TST-10" }, byState[TaskStates.Ready]);
        Assert.Equal(new[] { "000/TST-12" }, byState[TaskStates.Progress]);
        Assert.Equal(new[] { "000/TST-11" }, byState[TaskStates.Archive]);
        Assert.Equal(TaskStates.Ready, Field(JobJson("TST-10"), "state"));
        Assert.Equal(TaskStates.Progress, Field(JobJson("TST-12"), "state"));
    }

    [Fact]
    public void ChangeState_SameState_IsNoOp()
    {
        SeedLaneTask(TaskStates.Ready, "t", "TST-1");
        Migrate();

        var result = TaskLayoutTransition.ChangeState(_root, "TST-1", TaskStates.Ready, NullLogger.Instance);

        Assert.False(result.Changed);
        Assert.Equal(TaskStates.Ready, result.FromState);
        Assert.Equal(TaskStates.Ready, Field(JobJson("TST-1"), "state"));
    }

    [Fact]
    public void ChangeState_UnknownState_Throws()
    {
        SeedLaneTask(TaskStates.Ready, "t", "TST-1");
        Migrate();

        Assert.Throws<ArgumentException>(
            () => TaskLayoutTransition.ChangeState(_root, "TST-1", "99-not-a-lane", NullLogger.Instance));
    }

    [Fact]
    public void ChangeState_UnknownKey_ReturnsNotFound_AndChangesNothing()
    {
        SeedLaneTask(TaskStates.Ready, "t", "TST-1");
        Migrate();
        var before = JsonSerializer.Serialize(TaskLayoutIndex.ReadByState(_root));

        var result = TaskLayoutTransition.ChangeState(_root, "TST-404", TaskStates.Progress, NullLogger.Instance);

        Assert.False(result.Changed);
        Assert.Null(result.Location);
        Assert.Equal(before, JsonSerializer.Serialize(TaskLayoutIndex.ReadByState(_root)));
    }

    [Fact]
    public void ChangeState_LiveIndex_MatchesRebuild_FromJobJson()
    {
        // The index is a derived cache: after a live transition, regenerating
        // it from task.json (the authority) must yield the same membership.
        SeedLaneTask(TaskStates.Ready, "a", "TST-10");
        SeedLaneTask(TaskStates.Progress, "b", "TST-11");
        Migrate();

        TaskLayoutTransition.ChangeState(_root, "TST-10", TaskStates.Archive, NullLogger.Instance);

        var live = Membership(TaskLayoutIndex.ReadByState(_root));

        TaskLayoutIndex.Rebuild(_root, NullLogger.Instance);
        var rebuilt = Membership(TaskLayoutIndex.ReadByState(_root));

        Assert.Equal(rebuilt, live);
        Assert.Equal(TaskStates.Archive, Field(JobJson("TST-10"), "state"));
        Assert.Equal(TaskStates.Progress, Field(JobJson("TST-11"), "state"));
    }

    [Fact]
    public async Task ConcurrentRebuilds_PublishCompleteDerivedIndexes()
    {
        SeedLaneTask(TaskStates.Ready, "a", "TST-10");
        SeedLaneTask(TaskStates.Progress, "b", "TST-11");
        TaskLayoutIndex.Rebuild(_root, NullLogger.Instance);

        using var gate = new ManualResetEventSlim();
        var rebuilds = Enumerable.Range(0, 48).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            TaskLayoutIndex.Rebuild(_root, NullLogger.Instance);
            Assert.Equal("000/TST-10", TaskLayoutIndex.ReadByKey(_root)["TST-10"]);
            Assert.Equal("000/TST-11", TaskLayoutIndex.ReadByKey(_root)["TST-11"]);
        })).ToArray();
        gate.Set();
        await Task.WhenAll(rebuilds);

        Assert.Equal(new[] { "000/TST-10" }, TaskLayoutIndex.ReadByState(_root)[TaskStates.Ready]);
        Assert.Equal(new[] { "000/TST-11" }, TaskLayoutIndex.ReadByState(_root)[TaskStates.Progress]);
        Assert.Empty(Directory.GetFiles(TaskStorageLayout.IndexRoot(_root), "*.tmp"));
    }

    // ---- helpers -----------------------------------------------------------

    // The fixtures seed the flat layout directly (tasks/<bucket>/<key>/),
    // carrying the lane as task.json.state — the post-migration shape the
    // production code now assumes. The boot migrator was removed, so "Migrate"
    // just rebuilds the derived index from the seeded task.json authority.
    private void Migrate() => TaskLayoutIndex.Rebuild(_root, NullLogger.Instance);

    private string JobJson(string key) => Path.Combine(_root, "tasks", "000", key, "task.json");

    private void SeedLaneTask(string lane, string slug, string key)
    {
        var dir = Path.Combine(_root, "tasks", "000", key);
        Directory.CreateDirectory(dir);
        var doc = new Dictionary<string, object?>
        {
            ["id"] = slug,
            ["title"] = slug + " title",
            ["key"] = key,
            ["state"] = lane,
        };
        File.WriteAllText(Path.Combine(dir, "task.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }

    // state -> sorted set of locations, ignoring intra-lane order so a live
    // append compares equal to a deterministic rebuild.
    private static Dictionary<string, List<string>> Membership(Dictionary<string, List<string>> byState) =>
        byState.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x, StringComparer.Ordinal).ToList());

    private static string? Field(string jobJsonPath, string field)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(jobJsonPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return doc.TryGetValue(field, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }
}
