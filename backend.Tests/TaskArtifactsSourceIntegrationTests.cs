extern alias TaskServerAssembly;

using System.Text.Json;
using AgentStudio.Deploy;
using AgentStudio.Pipeline;
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
using WorkspaceExportService = TaskServerAssembly::AgentStudio.TaskServer.WorkspaceExportService;

namespace AgentStudio.Tests;

/// <summary>
/// Acceptance test of the task-folder artefacts card (pipeline delivery
/// target, section 05 "Task-folder artefacts" and "Release card attribution",
/// migration M3 and M6, decision D9 A, AGT-3320). Both hosts run in this
/// process: task folder files reach the Task Server with content hashes and
/// large content in the object store, release attribution answers the same
/// cards from the Task Server as from the folders, and a full export of the
/// Task Server is read back by today's task scanner and integration store.
/// </summary>
public sealed class TaskArtifactsSourceIntegrationTests : IDisposable
{
    private const string ProjectName = "artefact-test";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _taskServerData;

    public TaskArtifactsSourceIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "task-artifacts-tests-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "workspace");
        _taskServerData = Path.Combine(_root, "task-server");
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        WriteCard("TAS-1", "Verified card", TaskStates.Backlog, new
        {
            version = 1, state = IntegrationVerificationStates.Verified, sha = "aaa111",
            integrationBranch = "develop", deliveryShas = new[] { "bbb222" },
        });
        WriteCard("TAS-2", "Archived card", TaskStates.Archive, new
        {
            version = 1, state = IntegrationVerificationStates.Verified, sha = "ccc333", integrationBranch = "develop",
        });
        WriteCard("TAS-3", "Unverified card", TaskStates.Ready, new
        {
            version = 1, state = IntegrationVerificationStates.Unverified, sha = "ddd444", integrationBranch = "develop",
        });
        var folder = CardFolder("TAS-1");
        Directory.CreateDirectory(Path.Combine(folder, "results"));
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "results", "report.html"), new string('R', 4096));
        File.WriteAllText(Path.Combine(folder, "logs", "cli-output.log"), "runtime log");
        File.WriteAllText(Path.Combine(folder, "scratch.tmp"), "scratch");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a retained temp folder must not fail a run.
            Console.Error.WriteLine($"Task artefacts test cleanup left {_root}: {exception.Message}");
        }
    }

    [Fact]
    public async Task Task_files_reach_the_task_server_attribution_matches_and_the_export_reads_back_with_todays_tools()
    {
        await using var taskServer = new TaskServerFactory(_taskServerData);
        var store = taskServer.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new AgentStudio.TaskServer.Contracts.CreateWorkspaceRequest("Artefacts"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, ProjectName, "TAS"), "test", default);

        // The mirror starts off, so its hosted first cycle is a no-op and every cycle below is explicit.
        await using var backend = BuildBackend(taskServer, _workspace, _watchPath, BoardSourceSetting.TaskServerShadow);
        var configuration = backend.Services.GetRequiredService<IConfiguration>();
        await backend.Services.GetRequiredService<BoardShadowWorker>().RunCycleAsync(default);
        var mirror = backend.Services.GetRequiredService<TaskFileMirrorWorker>();
        configuration[TaskArtifactsSourceSetting.SectionKey] = TaskArtifactsSourceSetting.TaskServerShadow;

        await mirror.RunCycleAsync(default);

        var cycle = mirror.LastCycle!;
        Assert.True(cycle.Error is null && cycle.Rejected.Count == 0, cycle.Error ?? string.Join(",", cycle.Rejected));
        var report = await store.GetTaskFileContentAsync(ProjectName, "TAS-1", "results/report.html", default);
        Assert.Equal(4096, report!.Value.Content.Length);
        Assert.True(File.Exists(store.ContentObjects.PathFor(report.Value.Meta.Sha256!)));
        Assert.NotNull(await store.GetTaskFileContentAsync(ProjectName, "TAS-1", "task.json", default));
        Assert.NotNull(await store.GetTaskFileContentAsync(ProjectName, "TAS-2", "integration-verification.json", default));
        Assert.Null(await store.GetTaskFileContentAsync(ProjectName, "TAS-1", "logs/cli-output.log", default));
        Assert.Null(await store.GetTaskFileContentAsync(ProjectName, "TAS-1", "scratch.tmp", default));

        // A change writes one revision of the changed file; unchanged content is not written again.
        File.WriteAllText(Path.Combine(CardFolder("TAS-1"), "prompt.md"), "Changed brief");
        await mirror.RunCycleAsync(default);
        Assert.True(mirror.LastCycle!.FilesSent >= 1);
        Assert.Equal(2, (await store.GetTaskFileHistoryAsync(ProjectName, "TAS-1", "prompt.md", default)).Revisions.Count);
        Assert.Single((await store.GetTaskFileHistoryAsync(ProjectName, "TAS-1", "results/report.html", default)).Revisions);

        // Release attribution: the Task Server answers the same cards the folder scan finds.
        var attribution = backend.Services.GetRequiredService<ReleaseCardAttributionSource>();
        Assert.False(attribution.ReadsTaskServer);
        var fromFolders = Describe(attribution.IntegratedCards(ProjectName));
        configuration[TaskArtifactsSourceSetting.SectionKey] = TaskArtifactsSourceSetting.TaskServer;
        Assert.True(attribution.ReadsTaskServer);
        var fromTaskServer = Describe(attribution.IntegratedCards(ProjectName));
        Assert.Equal(["TAS-1|Verified card|aaa111,bbb222|develop", "TAS-2|Archived card|ccc333|develop"], fromFolders);
        Assert.Equal(fromFolders, fromTaskServer);

        // A full export is the task-folder layout today's scanner and integration store read.
        var exportRoot = Path.Combine(_root, "export");
        var export = await new WorkspaceExportService(store).ExportAsync(exportRoot, default);
        Assert.Equal(3, export.Tasks);
        // The reader has its own task repository, so it sees only the exported folders.
        await using var reader = BuildBackend(taskServer, Path.Combine(_root, "reader-workspace"),
            Path.Combine(exportRoot, "projects", ProjectName), BoardSourceSetting.Workspace);
        var exported = reader.Services.GetRequiredService<TaskScannerService>().ScanAllJobsRaw();
        var original = backend.Services.GetRequiredService<TaskScannerService>().ScanAllJobsRaw();
        Assert.Equal(Cards(original), Cards(exported));
        Assert.Equal(fromFolders, Describe(reader.Services.GetRequiredService<ReleaseCardAttributionSource>().IntegratedCards(ProjectName)));
        Assert.Equal("Changed brief", File.ReadAllText(Path.Combine(exported.Single(task => task.Key == "TAS-1").FolderPath, "prompt.md")));
    }

    private static IReadOnlyList<string> Cards(IEnumerable<TaskInfo> tasks)
        => tasks.Select(task => $"{task.Key}|{task.Title}|{task.State}").Order(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> Describe(IEnumerable<IntegratedCardRecord> cards)
        => cards.Select(card => $"{card.Key}|{card.Title}|{string.Join(',', card.Shas)}|{card.IntegrationBranch}")
            .Order(StringComparer.Ordinal).ToList();

    private string CardFolder(string key) => Path.Combine(_watchPath, "tasks", "000", key);

    private void WriteCard(string key, string title, string state, object integration)
    {
        var folder = CardFolder(key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key.ToLowerInvariant(), key, title, state, order = 1, agent = "claude",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Brief of " + key);
        File.WriteAllText(Path.Combine(folder, IntegrationVerificationStore.FileName), JsonSerializer.Serialize(integration));
    }

    private static WebApplicationFactory<Program> BuildBackend(
        TaskServerFactory taskServer, string taskRepository, string watchPath, string boardSource)
        => new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = taskRepository,
                    ["WatchPaths:0:Name"] = ProjectName,
                    ["WatchPaths:0:Path"] = watchPath,
                    ["WatchPaths:0:RootPath"] = watchPath,
                    ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
                    ["GitStateIndex:SweepIntervalSeconds"] = "3600",
                    ["GitStateIndex:MaxRetries"] = "0",
                    [BoardSourceSetting.SectionKey] = boardSource,
                    [TaskArtifactsSourceSetting.SectionKey] = TaskArtifactsSourceSetting.Workspace,
                    ["Board:TaskServer:BaseUrl"] = "http://localhost/",
                    ["Board:Shadow:SyncIntervalSeconds"] = "3600",
                    ["TaskArtifacts:Mirror:IntervalSeconds"] = "3600",
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
                    ["TaskServer:ContentInlineLimitBytes"] = "1024",
                }));
        }
    }
}
