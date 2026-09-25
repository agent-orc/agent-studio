using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Acceptance contract for the batch-move endpoint. The 2026-05-08 manual
/// <c>mv</c> incident that produced the 2026-05-09 zombie folder happened
/// because there was no atomic batch path for "restore N jobs from
/// archive". This test pins the per-item-atomic contract: a conflict on
/// one item must not roll back items that already moved, and every item
/// gets a typed status string in the response.
/// </summary>
public class TaskBatchMoveTests : IDisposable
{
    private readonly string _watchPath;

    public TaskBatchMoveTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-batchmove-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task BatchMoveAsync_FiveMovesAcrossThreeLanes_LandsEveryItemInTargetLane()
    {
        // Five archived jobs that we want to restore into three different
        // target lanes - the canonical "manual restore" gesture that used
        // to drop to shell mv.
        WriteJob(TaskStates.Archive, "alpha");
        WriteJob(TaskStates.Archive, "beta");
        WriteJob(TaskStates.Archive, "gamma");
        WriteJob(TaskStates.Archive, "delta");
        WriteJob(TaskStates.Archive, "epsilon");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "beta",    WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
            new() { JobId = "delta",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
            new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = TaskStates.Preparation },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal("moved", r.Status));

        var laneByJob = ReadLaneByJob();
        Assert.Equal(TaskStates.Ready,       laneByJob["alpha"]);
        Assert.Equal(TaskStates.Ready,       laneByJob["beta"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["gamma"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["delta"]);
        Assert.Equal(TaskStates.Preparation, laneByJob["epsilon"]);
    }

    // MachineBound 20.07.: WebApplicationFactory-Batch-Move flaked unter Gate-Parallellast (AGT-2192 Gate-11), solo gruen.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task JobsBatchMoveEndpoint_FiveMovesAcrossThreeLanes_ReturnsHandleThenOrderedPerItemResults()
    {
        WriteJob(TaskStates.Archive, "alpha");
        WriteJob(TaskStates.Archive, "beta");
        WriteJob(TaskStates.Archive, "gamma");
        WriteJob(TaskStates.Archive, "delta");
        WriteJob(TaskStates.Archive, "epsilon");

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WatchPaths:0:Name"] = "batchmove-test",
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["TaskRepository"] = _watchPath
                    });
                });
            });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/batch-move")
        {
            Content = JsonContent.Create(new BatchMoveRequest
            {
                Items =
                [
                    new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
                    new() { JobId = "beta",    WatchPath = _watchPath, TargetState = TaskStates.Ready },
                    new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
                    new() { JobId = "delta",   WatchPath = _watchPath, TargetState = TaskStates.Backlog },
                    new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = TaskStates.Preparation },
                ]
            })
        };
        request.Headers.Add("X-Client-Id", "local-default");

        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<BatchMoveJobResponse>();

        Assert.NotNull(accepted);
        Assert.Equal(5, accepted!.Total);
        Assert.Equal($"/api/tasks/batch-move/{accepted.Id}", response.Headers.Location?.ToString());
        Assert.True(accepted.Status is BatchMoveJobStates.Queued or BatchMoveJobStates.Running);

        BatchMoveJobResponse? completed = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            completed = await client.GetFromJsonAsync<BatchMoveJobResponse>(
                $"/api/tasks/batch-move/{accepted.Id}");
            if (completed is not null && BatchMoveJobStates.IsTerminal(completed.Status)) break;
            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(BatchMoveJobStates.Completed, completed!.Status);
        Assert.Equal(5, completed.Completed);
        Assert.Equal(["alpha", "beta", "gamma", "delta", "epsilon"], completed.Results.Select(r => r.JobId).ToArray());
        Assert.All(completed.Results, r => Assert.Equal("moved", r.Status));

        var laneByJob = ReadLaneByJob();
        Assert.Equal(TaskStates.Ready,       laneByJob["alpha"]);
        Assert.Equal(TaskStates.Ready,       laneByJob["beta"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["gamma"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["delta"]);
        Assert.Equal(TaskStates.Preparation, laneByJob["epsilon"]);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task ProtectedMoves_RejectMissingCodeDeliveryOnSingleDragAndBatchPaths()
    {
        WriteJob(TaskStates.AutoReview, "admission");
        WriteJob(TaskStates.HumanReview, "acceptance");
        WriteJob(TaskStates.Completed, "archive");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "batchmove-test",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _watchPath,
                    ["TaskRepository"] = _watchPath,
                    ["DeliveryChain:Guarded"] = "true",
                }));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        foreach (var protectedLane in new[] { TaskStates.HumanReview, TaskStates.Completed, TaskStates.Archive })
        {
            using var created = await client.PostAsJsonAsync("/api/tasks", new
            {
                id = "new-protected", title = "Documentation only",
                watchPath = _watchPath, targetState = protectedLane,
            });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, created.StatusCode);
        }

        async Task AssertBlockedAsync(HttpMethod method, string task, string target, int? index = null)
        {
            var route = method == HttpMethod.Put ? "state" : "move";
            using var request = new HttpRequestMessage(method, $"/api/tasks/{task}/{route}")
            {
                Content = JsonContent.Create(new MoveJobRequest { TargetState = target, TargetIndex = index }),
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        }

        await AssertBlockedAsync(HttpMethod.Post, "admission", TaskStates.HumanReview);
        await AssertBlockedAsync(HttpMethod.Post, "acceptance", TaskStates.Completed);
        await AssertBlockedAsync(HttpMethod.Post, "archive", TaskStates.Archive);
        await AssertBlockedAsync(HttpMethod.Put, "acceptance", TaskStates.Completed, 0);
        await AssertBlockedAsync(HttpMethod.Put, "archive", TaskStates.Archive, 0);

        using var batch = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/batch-move")
        {
            Content = JsonContent.Create(new BatchMoveRequest
            {
                Items =
                [
                    new() { JobId = "admission", WatchPath = _watchPath, TargetState = TaskStates.HumanReview },
                    new() { JobId = "acceptance", WatchPath = _watchPath, TargetState = TaskStates.Completed },
                    new() { JobId = "archive", WatchPath = _watchPath, TargetState = TaskStates.Archive },
                ],
            }),
        };
        using var submitted = await client.SendAsync(batch);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submitted.StatusCode);
        var accepted = await submitted.Content.ReadFromJsonAsync<BatchMoveJobResponse>();
        Assert.NotNull(accepted);
        BatchMoveJobResponse? finished = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            finished = await client.GetFromJsonAsync<BatchMoveJobResponse>(
                $"/api/tasks/batch-move/{accepted!.Id}");
            if (finished is not null && BatchMoveJobStates.IsTerminal(finished.Status)) break;
            await Task.Delay(20);
        }
        Assert.NotNull(finished);
        Assert.All(finished!.Results, item => Assert.Equal("integration-failed", item.Status));
        Assert.NotEqual(TaskStates.HumanReview, ReadLaneByJob()["admission"]);
        Assert.NotEqual(TaskStates.Completed, ReadLaneByJob()["acceptance"]);
        Assert.NotEqual(TaskStates.Archive, ReadLaneByJob()["archive"]);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task PerCardArchiveOverride_PersistsTimelineAndAuditFactsBeforeMove()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "batchmove-test",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _watchPath,
                    ["TaskRepository"] = _watchPath,
                    ["DeliveryChain:Guarded"] = "true",
                }));
        });
        using var client = factory.CreateClient();
        WriteJob(TaskStates.Completed, "override-one");
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
        var transition = factory.Services.GetRequiredService<TaskTransitionService>();
        var outcome = await transition.MoveAsync("override-one", TaskStates.Archive, _watchPath,
            cause: TimelineActors.Human("owner"),
            reason: "Operator accepted archival with an unrecovered delivery.",
            archiveOverride: true);
        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var archived = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob("override-one", _watchPath);
        Assert.Equal(TaskStates.Archive, archived?.State);
        var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(archived!.FolderPath);
        Assert.Contains(timeline, row => row.Kind == "archive_override");
        var audit = Path.Combine(_watchPath, ".audit", "archive-overrides.jsonl");
        var row = Assert.Single(File.ReadAllLines(audit));
        Assert.Contains("Operator accepted archival", row);
        Assert.Contains("integrationStatus", row);
        Assert.Contains("deliveryEpoch", row);
        Assert.Contains("targetBranch", row);
    }

    [Fact]
    public async Task BatchMoveAsync_TargetSlugCollision_AutoSuffixesAndStillMovesEveryItem()
    {
        // Items 1, 2, 4, 5 come from archive and should move into 2-ready.
        // Item 3 (gamma) starts in 1-preparation but a stale folder with the
        // same slug already exists in 2-ready - the case that used to surface
        // a 409 conflict on the single-item endpoint and strand the batch.
        // With the collision-safe move (Layer 2) every item moves: gamma is
        // auto-suffixed into 2-ready and the pre-existing namesake is left
        // untouched.
        WriteJob(TaskStates.Archive,     "alpha");
        WriteJob(TaskStates.Archive,     "beta");
        WriteJob(TaskStates.Preparation, "gamma");
        WriteJob(TaskStates.Ready,       "gamma");   // pre-existing stale duplicate
        WriteJob(TaskStates.Archive,     "delta");
        WriteJob(TaskStates.Archive,     "epsilon");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "beta",    WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "delta",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = TaskStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal("moved", r.Status));

        // alpha/beta/delta/epsilon land under their own slug; gamma's move
        // collided on the namesake so it landed as gamma-2. The 1-preparation
        // source is drained and the stale namesake in 2-ready is preserved.
        var folders = ReadFoldersByLane();
        Assert.Contains("alpha",   folders[TaskStates.Ready]);
        Assert.Contains("beta",    folders[TaskStates.Ready]);
        Assert.Contains("delta",   folders[TaskStates.Ready]);
        Assert.Contains("epsilon", folders[TaskStates.Ready]);
        Assert.Contains("gamma",   folders[TaskStates.Ready]);
        Assert.Contains("gamma-2", folders[TaskStates.Ready]);
        Assert.DoesNotContain("gamma", folders[TaskStates.Preparation]);
    }

    [Fact]
    public async Task BatchMoveAsync_ItemConflict_DoesNotBlockRemainingMoves()
    {
        WriteJob(TaskStates.Archive, "alpha");
        WriteJob(TaskStates.Archive, "beta");
        WriteJob(TaskStates.Archive, "gamma");
        WriteJob(TaskStates.Archive, "delta");
        WriteJob(TaskStates.Archive, "epsilon");
        File.WriteAllText(Path.Combine(_watchPath, TaskStates.Ready, "gamma"), "not a task folder");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "beta",    WatchPath = _watchPath, TargetState = TaskStates.Backlog },
            new() { JobId = "gamma",   WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "delta",   WatchPath = _watchPath, TargetState = TaskStates.Preparation },
            new() { JobId = "epsilon", WatchPath = _watchPath, TargetState = TaskStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal("moved",    results[0].Status);
        Assert.Equal("moved",    results[1].Status);
        Assert.Equal("conflict", results[2].Status);
        Assert.Equal("moved",    results[3].Status);
        Assert.Equal("moved",    results[4].Status);

        var laneByJob = ReadLaneByJob();
        Assert.Equal(TaskStates.Ready,       laneByJob["alpha"]);
        Assert.Equal(TaskStates.Backlog,     laneByJob["beta"]);
        Assert.Equal(TaskStates.Archive,     laneByJob["gamma"]);
        Assert.Equal(TaskStates.Preparation, laneByJob["delta"]);
        Assert.Equal(TaskStates.Ready,       laneByJob["epsilon"]);
    }

    [Fact]
    public async Task BatchMoveAsync_InvalidLane_ReportsRejectedWithoutBlockingOtherItems()
    {
        WriteJob(TaskStates.Archive, "alpha");
        WriteJob(TaskStates.Archive, "beta");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "alpha", WatchPath = _watchPath, TargetState = "not-a-real-lane" },
            new() { JobId = "beta",  WatchPath = _watchPath, TargetState = TaskStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal("rejected", results[0].Status);
        Assert.Equal("moved",    results[1].Status);
    }

    [Fact]
    public async Task BatchMoveAsync_UnknownJob_ReportsNotFoundWithoutBlockingOtherItems()
    {
        WriteJob(TaskStates.Archive, "alpha");

        var transitions = BuildTransitionService();

        var items = new List<BatchMoveItem>
        {
            new() { JobId = "ghost", WatchPath = _watchPath, TargetState = TaskStates.Ready },
            new() { JobId = "alpha", WatchPath = _watchPath, TargetState = TaskStates.Ready },
        };

        var results = await transitions.BatchMoveAsync(items, CancellationToken.None);

        Assert.Equal("not-found", results[0].Status);
        Assert.Equal("moved",     results[1].Status);
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":10,\"agent\":\"copilot\"}}");
    }

    private Dictionary<string, string> ReadLaneByJob()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return scanner.ScanAllJobs().ToDictionary(j => j.Id, j => j.State);
    }

    private Dictionary<string, HashSet<string>> ReadFoldersByLane()
    {
        var byLane = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var state in TaskStates.All)
        {
            var laneDir = Path.Combine(_watchPath, state);
            byLane[state] = new HashSet<string>(
                Directory.EnumerateDirectories(laneDir).Select(Path.GetFileName)!,
                StringComparer.Ordinal);
        }
        return byLane;
    }

    private TaskTransitionService BuildTransitionService()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        return new TaskTransitionService(scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance);
    }

    private IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "batchmove-test",
            ["WatchPaths:0:Path"] = _watchPath
        })
        .Build();
}
