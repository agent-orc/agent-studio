extern alias TaskServerAssembly;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using TaskServerProgram = TaskServerAssembly::Program;
using TaskServerStore = TaskServerAssembly::AgentStudio.TaskServer.TaskServerStore;

namespace AgentStudio.Tests;

/// <summary>
/// Acceptance test of the board read model card (pipeline delivery target,
/// M1 and M2, decision D7 A-B): a lane move through the monolith API appears
/// in the workspace index and in the Task Server read model within one
/// workspace generation, the shadow comparison records no difference, a
/// Task Server-only change is recorded with card id and field, and
/// <c>Board:Source = task-server</c> answers the board from the Task Server.
/// Both hosts run in this process; the backend reaches the Task Server
/// through its test handler.
/// </summary>
public sealed class BoardSourceIntegrationTests : IDisposable
{
    private const string ProjectName = "board-source-test";
    private const string Slug = "bst-1-move-me";
    private const string Key = "BST-1";

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _taskServerData;

    public BoardSourceIntegrationTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "board-source-tests-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(root, "workspace");
        _taskServerData = Path.Combine(root, "task-server");
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        var folder = Path.Combine(_watchPath, TaskStates.Backlog, Slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = Slug, key = Key, title = "Move me", state = TaskStates.Backlog, order = 1, agent = "claude",
        }));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_workspace)!, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a retained temp folder must not fail a run.
            Console.Error.WriteLine($"Board source test cleanup left {_workspace}: {exception.Message}");
        }
    }

    [Fact]
    public async Task Lane_move_through_the_api_appears_in_both_sources_within_one_generation()
    {
        await using var taskServer = new TaskServerFactory(_taskServerData);
        var store = taskServer.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new AgentStudio.TaskServer.Contracts.CreateWorkspaceRequest("Board source"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, ProjectName, "BST"), "test", default);

        await using var backend = BuildBackend(taskServer, BoardSourceSetting.TaskServerShadow);
        using var client = backend.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var worker = backend.Services.GetRequiredService<BoardShadowWorker>();
        var scanner = backend.Services.GetRequiredService<TaskScannerService>();
        var ledger = backend.Services.GetRequiredService<BoardShadowLedger>();
        var shadow = BoardSourceSetting.Resolve(BoardSourceSetting.TaskServerShadow);

        var initial = await client.GetAsync("/api/tasks/grouped");
        initial.EnsureSuccessStatusCode();
        Assert.Equal(BoardSourceSetting.TaskServerShadow, initial.Headers.GetValues("X-Board-Source").Single());
        await worker.RunCycleAsync(default);
        var sync = ledger.Snapshot(shadow).Sync;
        Assert.True(sync.SyncedGeneration is not null && sync.MissingProjects.Count == 0, sync.LastError);
        Assert.Equal(TaskStates.Backlog, (await TaskServerCardAsync(store)).State);
        var baseline = ledger.Snapshot(shadow);
        Assert.Equal((1L, 0), (baseline.Comparisons, baseline.UnexplainedDifferences));

        var move = await client.PostAsJsonAsync(
            $"/api/tasks/{Slug}/move?watchPath={Uri.EscapeDataString(_watchPath)}",
            new { targetState = TaskStates.Ready });
        move.EnsureSuccessStatusCode();

        // The first workspace generation that holds the move is the one the
        // Task Server is given: no later generation is needed to see it there.
        var publication = (await scanner.AcquireBoardReadAsync("local-default", default))!;
        Assert.Contains(publication.Live, task => task.Key == Key && task.State == TaskStates.Ready);
        var read = await client.GetAsync("/api/tasks/grouped");
        read.EnsureSuccessStatusCode();
        await worker.RunCycleAsync(default);
        Assert.True(worker.SyncedGeneration >= publication.Generation);
        var model = await store.GetBoardReadModelAsync(ProjectName, default);
        Assert.Contains(model.Lanes.Single(lane => lane.State == TaskStates.Ready).Cards, card => card.TaskKey == Key);
        Assert.Equal(worker.SyncedGeneration, model.WorkspaceSourceGeneration);
        var afterMove = ledger.Snapshot(shadow);
        Assert.Equal(0, afterMove.UnexplainedDifferences);
        Assert.True(afterMove.Comparisons >= 2);
        Assert.Equal(worker.SyncedGeneration, afterMove.LastComparedGeneration);

        // A change the workspace never made is recorded by card id and field.
        var card = await TaskServerCardAsync(store);
        await store.UpdateTaskAsync(card.ProjectId, card.TaskId,
            new UpdateTaskRequest("Renamed on the Task Server", null, null, card.Version), "test", default);
        (await client.GetAsync("/api/tasks/grouped")).EnsureSuccessStatusCode();
        await worker.RunCycleAsync(default);
        var status = await client.GetFromJsonAsync<BoardSourceStatusResponse>("/api/admin/board-source");
        var difference = Assert.Single(status!.Differences);
        Assert.Equal((Key, BoardShadowComparison.TitleField, "Move me", "Renamed on the Task Server"),
            (difference.TaskKey, difference.Field, difference.WorkspaceValue, difference.TaskServerValue));
        Assert.Equal(1, status.UnexplainedDifferences);

        var explain = await client.PostAsJsonAsync("/api/admin/board-source/differences/explain",
            new BoardSourceEndpoints.ExplainBoardDifferenceBody(difference.Fingerprint, "Seeded by the test"));
        explain.EnsureSuccessStatusCode();
        Assert.Equal(0, (await explain.Content.ReadFromJsonAsync<BoardSourceStatusResponse>())!.UnexplainedDifferences);
    }

    [Fact]
    public async Task Task_server_source_answers_the_board_from_the_task_server_and_sees_its_own_write()
    {
        await using var taskServer = new TaskServerFactory(_taskServerData);
        var store = taskServer.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new AgentStudio.TaskServer.Contracts.CreateWorkspaceRequest("Board source"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, ProjectName, "BST"), "test", default);

        await using var backend = BuildBackend(taskServer, BoardSourceSetting.TaskServer);
        using var client = backend.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var first = await ReadBoardAsync(client);
        Assert.Equal(BoardSourceSetting.TaskServer, first.Source);
        Assert.Contains(first.Lanes.GetProperty("backlog").EnumerateArray(), item => item.GetProperty("key").GetString() == Key);

        var move = await client.PostAsJsonAsync(
            $"/api/tasks/{Slug}/move?watchPath={Uri.EscapeDataString(_watchPath)}",
            new { targetState = TaskStates.Ready });
        move.EnsureSuccessStatusCode();
        var moved = await ReadBoardAsync(client);
        Assert.Contains(moved.Lanes.GetProperty("ready").EnumerateArray(), item => item.GetProperty("key").GetString() == Key);
        Assert.Equal(TaskStates.Ready, (await TaskServerCardAsync(store)).State);

        // A Task Server-only change reaches the board: the Task Server answers.
        var card = await TaskServerCardAsync(store);
        await store.UpdateTaskAsync(card.ProjectId, card.TaskId,
            new UpdateTaskRequest("Task Server title", null, null, card.Version), "test", default);
        var renamed = await ReadBoardAsync(client);
        Assert.Contains(renamed.Lanes.GetProperty("ready").EnumerateArray(),
            item => item.GetProperty("title").GetString() == "Task Server title");
        Assert.NotEqual(moved.ETag, renamed.ETag);

        // Rollback is the setting: the workspace answers again.
        backend.Services.GetRequiredService<IConfiguration>()[BoardSourceSetting.SectionKey] = BoardSourceSetting.Workspace;
        var rolledBack = await ReadBoardAsync(client);
        Assert.Equal(BoardSourceSetting.Workspace, rolledBack.Source);
        Assert.Contains(rolledBack.Lanes.GetProperty("ready").EnumerateArray(),
            item => item.GetProperty("title").GetString() == "Move me");
    }

    private static async Task<(string Source, string? ETag, JsonElement Lanes)> ReadBoardAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/tasks/grouped");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("X-Board-Git-Spawns").Single());
        var lanes = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.Headers.GetValues("X-Board-Source").Single(), response.Headers.ETag?.ToString(), lanes);
    }

    private static async Task<TaskDto> TaskServerCardAsync(TaskServerStore store)
        => (await store.GetTaskAsync(ProjectName, Key, default))!;

    private WebApplicationFactory<Program> BuildBackend(TaskServerFactory taskServer, string source)
        => new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = ProjectName,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _watchPath,
                    ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
                    ["GitStateIndex:SweepIntervalSeconds"] = "3600",
                    ["GitStateIndex:MaxRetries"] = "0",
                    [BoardSourceSetting.SectionKey] = source,
                    ["Board:TaskServer:BaseUrl"] = "http://localhost/",
                    ["Board:Shadow:SyncIntervalSeconds"] = "3600",
                }));
            builder.ConfigureTestServices(services => services
                .AddHttpClient(TaskServerBoardClient.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => taskServer.Server.CreateHandler()));
        });

    private sealed class TaskServerFactory(string dataDirectory) : WebApplicationFactory<TaskServerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                }));
        }
    }
}
