using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Phase 2a of the API cleanup (ASS-1760): the create path resolves a
/// path-free project handle — a short code / Kürzel (<c>ASS</c>) or a stable
/// id (<c>PROJ-NNN</c>) — to the project's watchPath server-side, so the
/// filesystem layout never has to travel over the wire. A raw absolute path
/// still works for legacy callers during the migration.
/// </summary>
public class CreateTaskByProjectHandleTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public CreateTaskByProjectHandleTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "create-by-handle-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FindByShortCode_MatchesCaseInsensitively()
    {
        var registry = new ProjectRegistry(BuildConfig(), NullLogger<ProjectRegistry>.Instance);
        var record = registry.EnsureProjectForStorage(_watchPath, "Demo Project", "default");
        registry.SetShortCode(record.Id, "ASS");

        Assert.Equal(record.Id, registry.FindByShortCode("ASS")?.Id);
        Assert.Equal(record.Id, registry.FindByShortCode("ass")?.Id);
        Assert.Null(registry.FindByShortCode("NOPE"));
        Assert.Null(registry.FindByShortCode(""));
    }

    [Fact]
    public void CreateTask_ByShortCode_LandsInResolvedProject()
    {
        var (machine, scanner, mutations, registry) = Build();
        machine.EnsureStateFoldersAndMigrate();
        var record = registry.EnsureProjectForStorage(_watchPath, "Demo Project", "default");
        registry.SetShortCode(record.Id, "ASS");

        var id = mutations.CreateJob(new CreateTaskRequest { Title = "Handle Task", Project = "ASS" });

        Assert.Equal("handle-task", id);
        var info = scanner.FindJob("handle-task", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskStates.Backlog, info!.State);
    }

    [Fact]
    public void CreateTask_ByProjectId_LandsInResolvedProject()
    {
        var (machine, scanner, mutations, registry) = Build();
        machine.EnsureStateFoldersAndMigrate();
        var record = registry.EnsureProjectForStorage(_watchPath, "Demo Project", "default");

        var id = mutations.CreateJob(new CreateTaskRequest { Title = "Id Task", Project = record.Id });

        Assert.Equal("id-task", id);
        Assert.NotNull(scanner.FindJob("id-task", _watchPath));
    }

    [Fact]
    public void CreateTask_HonorsExplicitTargetStateBeyondIntakeLanes()
    {
        var (machine, scanner, mutations, registry) = Build();
        machine.EnsureStateFoldersAndMigrate();
        var record = registry.EnsureProjectForStorage(_watchPath, "Demo Project", "default");

        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Operator Review Task",
            Project = record.Id,
            TargetState = TaskStates.HumanReview,
        });

        Assert.Equal("operator-review-task", id);
        var info = scanner.FindJob(id!, _watchPath);
        Assert.NotNull(info);
        Assert.Equal(TaskStates.HumanReview, info!.State);
    }

    [Fact]
    public async Task CreateTaskEndpoint_RejectsDirectHumanReviewForCodeDelivery()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = Project,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                    }));
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var response = await client.PostAsJsonAsync("/api/tasks", new
        {
            title = "Operator Review Through API",
            watchPath = _watchPath,
            targetState = TaskStates.HumanReview,
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs(),
            task => task.Title == "Operator Review Through API");
    }

    [Fact]
    public void CreateTask_ProjectHandle_TakesPrecedenceOverWatchPath()
    {
        var (machine, scanner, mutations, registry) = Build();
        machine.EnsureStateFoldersAndMigrate();
        var record = registry.EnsureProjectForStorage(_watchPath, "Demo Project", "default");
        registry.SetShortCode(record.Id, "ASS");

        // WatchPath points at a bogus directory; the Project handle must win.
        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Precedence Task",
            Project = "ASS",
            WatchPath = Path.Combine(_workspace, "does-not-exist"),
        });

        Assert.Equal("precedence-task", id);
        Assert.NotNull(scanner.FindJob("precedence-task", _watchPath));
    }

    [Fact]
    public void CreateTask_ByRawWatchPath_StillWorksForLegacyCallers()
    {
        var (machine, scanner, mutations, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var id = mutations.CreateJob(new CreateTaskRequest { Title = "Legacy Task", WatchPath = _watchPath });

        Assert.Equal("legacy-task", id);
        Assert.NotNull(scanner.FindJob("legacy-task", _watchPath));
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations, ProjectRegistry registry) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var laneMutex = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, laneMutex);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline: null,
            laneMutex: laneMutex);
        return (machine, scanner, mutations, registry);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
