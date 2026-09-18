using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// A <c>task.json</c> rewrite must never re-create the lane folder it was
/// rewriting.
///
/// <para>The scanner stamps <c>ownerClientId</c> on legacy task folders from
/// inside its own scan, and that is the one write on an otherwise read-only
/// path. When a lane writer renamed the folder between the scan's existence
/// check and the swap, the atomic writer's <c>Directory.CreateDirectory</c>
/// rebuilt the source folder and the swap dropped the pre-move
/// <c>task.json</c> into it. The task then existed in two lanes at once: the
/// scanner logged "Duplicate job id ... found in 2 locations", resolved it to
/// the resurrected ghost in the earlier lane, and a concurrent delete removed
/// the ghost and reported success while the real folder stayed in the target
/// lane. That is the surviving folder
/// <see cref="LaneMutexRegistryConcurrencyTests.ConcurrentMoveAndDelete_OnSameSlug_NeverProducesPartialFolder"/>
/// was failing on under a loaded host (AGT-2867).</para>
/// </summary>
public class TaskJsonWriteFolderRaceTests : IDisposable
{
    private readonly string _watchPath;

    public TaskJsonWriteFolderRaceTests()
    {
        _watchPath = Path.Combine(
            Path.GetTempPath(), "task-json-folder-race-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void UpdateField_WhenALaneMoveLandsMidWrite_ReportsTheLostWriteAndLeavesOneFolder()
    {
        var source = SeedJob(TaskStates.Progress, "race-target");
        var target = Path.Combine(_watchPath, TaskStates.AutoReview, "race-target");
        // Stand in for the lane writer that wins the race: the rename happens
        // after UpdateField has seen the file and built its content, exactly the
        // window the production race lands in.
        var writer = new MoveFolderBeforeWriting(() => Directory.Move(source, target));

        var written = TaskJsonFile.UpdateField(
            source, "ownerClientId", "local-default", NullLogger.Instance, writer);

        Assert.False(written);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_watchPath, TaskStates.Progress)));
        var moved = Assert.Single(Directory.GetDirectories(Path.Combine(_watchPath, TaskStates.AutoReview)));
        Assert.True(File.Exists(Path.Combine(moved, "task.json")));
    }

    [Fact]
    public void UpdateField_WhenTheFolderSurvives_StillWritesTheField()
    {
        var dir = SeedJob(TaskStates.Progress, "calm-target");

        var written = TaskJsonFile.UpdateField(
            dir, "ownerClientId", "local-default", NullLogger.Instance);

        Assert.True(written);
        Assert.True(TaskJsonFile.TryReadStringField(dir, "ownerClientId", NullLogger.Instance, out var owner));
        Assert.Equal("local-default", owner);
    }

    private string SeedJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
        return dir;
    }

    /// <summary>Runs the supplied lane move immediately before the swap.</summary>
    private sealed class MoveFolderBeforeWriting : IAtomicJsonFileWriter
    {
        private readonly AtomicJsonFileWriter _inner = new();
        private readonly Action _laneMove;

        public MoveFolderBeforeWriting(Action laneMove) => _laneMove = laneMove;

        public void Write(string path, string content)
        {
            _laneMove();
            _inner.Write(path, content);
        }

        public void ReplaceExisting(string path, string content)
        {
            _laneMove();
            _inner.ReplaceExisting(path, content);
        }
    }
}
