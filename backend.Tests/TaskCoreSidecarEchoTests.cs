using AgentStudio.Shared;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3369: every publication of one card rebuilt the core index for the
/// whole workspace, and the file watcher triggered a second publication for
/// each sidecar write the API had already published. The watcher now skips a
/// sidecar whose stamp the published record carries, and an unchanged record
/// keeps its instance through a publication.
/// </summary>
public sealed class TaskCoreSidecarEchoTests : IDisposable
{
    private static readonly TaskCoreSourceStamp Stamp = new(Bytes: 120, WriteTicks: 638_000_000_000_000_000);
    private static readonly TaskCoreSourceStamp Grown = new(Bytes: 180, WriteTicks: 638_000_000_010_000_000);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "core-echo-" + Guid.NewGuid().ToString("N"));

    public TaskCoreSidecarEchoTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("status.md", true)]
    [InlineData("prompt.md", true)]
    [InlineData("timeline.jsonl", true)]
    [InlineData("task.json", false)]
    [InlineData("notes.md", false)]
    public void Sidecar_with_the_published_stamp_is_an_echo(string fileName, bool echo)
        => Assert.Equal(echo, TaskIndexCache.SidecarAlreadyPublished(Published(), fileName, Stamp));

    [Theory]
    [InlineData("status.md")]
    [InlineData("prompt.md")]
    [InlineData("timeline.jsonl")]
    public void Sidecar_that_changed_after_publication_is_published_again(string fileName)
        => Assert.False(TaskIndexCache.SidecarAlreadyPublished(Published(), fileName, Grown));

    [Fact]
    public void Sidecar_that_was_unreadable_at_publication_is_published_again()
    {
        var stale = Published() with
        {
            Status = new TaskCoreText("stale", null, 0, null, null),
            Prompt = new TaskCoreText("stale", null, 0, null, null),
            Timeline = TaskCoreTimeline.Missing with { State = "stale" },
        };

        Assert.False(TaskIndexCache.SidecarAlreadyPublished(stale, "status.md", Stamp));
        Assert.False(TaskIndexCache.SidecarAlreadyPublished(stale, "prompt.md", Stamp));
        Assert.False(TaskIndexCache.SidecarAlreadyPublished(stale, "timeline.jsonl", Stamp));
    }

    [Fact]
    public void Unchanged_dependencies_keep_the_record_instance_and_a_change_replaces_it()
    {
        var info = Task();
        var record = TaskCoreRecord.Create(info);

        Assert.NotEqual(0, record.Version);
        Assert.Same(record, record.WithDependencies(info, waitsOn: null));

        var waiting = info with
        {
            References = new TaskReferences { DependsOn = [new TaskDependencyReference("AGT-1")] },
        };
        var changed = record.WithDependencies(waiting, waitsOn: null);

        Assert.NotSame(record, changed);
        Assert.Equal("warming", changed.DependencyState);
        Assert.Equal(["AGT-1"], changed.DependsOn);
        Assert.NotEqual(record.Version, changed.Version);
        Assert.Same(changed, changed.WithDependencies(waiting, waitsOn: null));
    }

    private static TaskCoreRecord Published() => new()
    {
        Id = "agt-3369",
        TaskKey = "AGT-3369",
        Status = new TaskCoreText("ready", "done", 4, null, null),
        Prompt = new TaskCoreText("ready", "prompt", 6, null, null),
        Timeline = new TaskCoreTimeline("ready", [], 0, null, null),
        StatusStamp = Stamp,
        PromptStamp = Stamp,
        TimelineStamp = Stamp,
        Version = 1,
    };

    private TaskInfo Task() => new()
    {
        Id = "agt-3369",
        TaskKey = "AGT-3369",
        Key = "AGT-3369",
        Title = "Stall",
        State = TaskStates.Ready,
        WatchPath = Path.GetDirectoryName(_folder)!,
        FolderPath = _folder,
        ProjectName = "Demo",
    };
}
