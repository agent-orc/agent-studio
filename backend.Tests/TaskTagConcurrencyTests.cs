using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskTagConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-tag-race-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MergeAdds_StartingAtSameGate_PreserveBothAdditions()
    {
        var (folder, mutations) = Build();
        TaskMutationRace.AtFileGate(folder,
            () => mutations.AddJobTag("tags", "first", _root),
            () => mutations.AddJobTag("tags", "second", _root));
        Assert.Equal(["first", "original", "second"], ReadTags(folder).Order().ToArray());
    }

    [Fact]
    public void ReplacementBeforeWaitingMergeAdd_IsPreserved()
    {
        var (folder, mutations) = Build();
        TaskMutationRace.AfterGateWrite(folder,
            () => mutations.AddJobTag("tags", "runner", _root),
            () => mutations.SetJobTags("tags", ["operator"], _root));
        Assert.Equal(["operator", "runner"], ReadTags(folder));
    }

    [Fact]
    public void ReplacementAfterMergeAdd_StillReplacesAllTags()
    {
        var (folder, mutations) = Build();
        Assert.True(mutations.AddJobTag("tags", "runner", _root));
        Assert.True(mutations.SetJobTags("tags", ["operator"], _root));
        Assert.Equal(["operator"], ReadTags(folder));
    }

    [Fact]
    public void MergeAdd_WriteFailure_ReturnsFalseAndKeepsDurableTags()
    {
        var writer = new ControllableAtomicJsonFileWriter { ShouldFail = (_, _) => true };
        var (folder, mutations) = Build(writer);
        Assert.False(mutations.AddJobTag("tags", "runner", _root));
        Assert.Equal(["original"], ReadTags(folder));
    }

    private (string Folder, TaskMutationService Mutations) Build(IAtomicJsonFileWriter? writer = null)
    {
        var folder = Path.Combine(_root, "tasks", "000", "tags");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new { id = "tags", title = "Tags", state = TaskStates.Ready, tags = new[] { "original" } }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "tags-project",
            ["WatchPaths:0:Path"] = _root,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        Assert.NotNull(scanner.FindJob("tags", _root));
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, fileWriter: writer);
        return (folder, mutations);
    }

    private static string[] ReadTags(string folder)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json")));
        return document.RootElement.GetProperty("tags").Deserialize<string[]>()!;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

// Each worker is parked at the existing file gate before it is released.
// No sleeps or workload-dependent race probability are needed. The old code
// has already derived a stale collection here; the fixed code has not read it.
internal static class TaskMutationRace
{
    internal static void AtFileGate(string folder, Func<bool> first, Func<bool> second)
        => Run(folder, [first, second], null);

    internal static void AfterGateWrite(string folder, Func<bool> waiting, Func<bool> holding)
        => Run(folder, [waiting], holding);

    private static void Run(string folder, Func<bool>[] actions, Func<bool>? holding)
    {
        var results = new bool[actions.Length];
        var errors = new Exception?[actions.Length];
        var workers = actions.Select((action, index) => new Thread(() =>
        {
            try { results[index] = action(); }
            catch (Exception ex) { errors[index] = ex; }
        }) { IsBackground = true }).ToArray();
        try
        {
            lock (TaskJsonFile.SyncRoot(folder))
            {
                foreach (var worker in workers) worker.Start();
                foreach (var worker in workers)
                    Assert.True(SpinWait.SpinUntil(() =>
                        (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(15)), "Mutation did not reach the task-file gate.");
                if (holding is not null) Assert.True(holding());
            }
        }
        finally
        {
            foreach (var worker in workers)
                if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                    Assert.True(worker.Join(TimeSpan.FromSeconds(15)), "Mutation did not leave the task-file gate.");
        }
        Assert.All(errors, error => Assert.Null(error));
        Assert.All(results, value => Assert.True(value));
    }
}
