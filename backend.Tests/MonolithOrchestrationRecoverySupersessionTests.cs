using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MonolithOrchestrationRecoverySupersessionTests : IDisposable
{
    private const string Project = "recovery-project";
    private const string Card = "RECOVERY-CARD";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "orchestration-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(
        new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private string ProjectPath => Path.Combine(_workspace, "projects", Project);

    [Fact]
    public async Task Non_card_engine_decisions_survive_recovery_and_complete()
    {
        var applier = new CountingApplier();
        using var factory = Factory(applier);
        _ = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var batch = store.Create(Project, "batch-plan:docs", OrchestrationStage.BatchPlan,
            "{}", "batch-plan:docs:1", "batch-plan", "batch-plan:docs", 0, 0);
        var breaker = store.CreateOperatorDecision(Project, "cause-breaker:fingerprint",
            "CauseBreaker", "{}", "cause-breaker:question", "none",
            "cause-breaker:fingerprint", 0, 0, period: null);
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(factory.Services);

        recovery.RecoverOnce();

        Assert.Equal("pending", store.Get(batch.RunId)!.Status);
        Assert.Equal("pending", store.Get(breaker.RunId)!.Status);
        foreach (var run in new[] { batch, breaker })
        {
            var claim = store.Claim(new OrchestrationClaimRequest("engine_one", "instance_one",
                [run.CurrentStage]));
            Assert.Equal(run.RunId, claim.Run!.RunId);
            var completed = await store.CompleteAsync(run.RunId,
                new CompleteOrchestrationStageRequest("engine_one", "instance_one",
                    claim.Lease!.LeaseId, claim.Lease.Fence, run.CurrentStage,
                    OrchestrationAction.Continue, "{}", "complete:" + run.RunId), default);
            Assert.Equal("completed", completed.Status);
        }
        Assert.Equal(2, applier.Count);
        Assert.Equal("completed", new MonolithOrchestrationStore(
            Path.Combine(_workspace, ".orchestration-occurrences"), _clock, applier)
            .Get(batch.RunId)!.Status);
    }

    [Fact]
    public void Occurrence_created_after_task_snapshot_uses_the_current_card_lane()
    {
        var ready = SeedCard(TaskStates.Ready);
        using var factory = Factory();
        _ = factory.CreateClient();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        Assert.Equal(TaskStates.Ready, scanner.FindJob(Card, ProjectPath)!.State);
        OrchestrationRunDto? created = null;
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(factory.Services);

        recovery.RecoverOnce(() =>
        {
            var review = Path.Combine(ProjectPath, TaskStates.AutoReview, Card);
            Directory.Move(ready, review);
            WriteTask(review, TaskStates.AutoReview);
            scanner.MoveCoreFromFolder(ready, review, ProjectPath, Project, TaskStates.AutoReview);
            created = store.Create(Project, Card, OrchestrationStage.ReviewDecision,
                "{}", "review:new-lane", TaskStates.AutoReview, "review-attempt", 1, 1);
        });

        Assert.NotNull(created);
        Assert.Equal("pending", store.Get(created.RunId)!.Status);
        Assert.Equal(TaskStates.AutoReview, scanner.FindPublishedJob(Card, ProjectPath)!.State);
    }

    [Fact]
    public void Supersession_audit_publishes_one_card_without_a_workspace_scan()
    {
        var folder = SeedCard(TaskStates.AutoReview);
        using var factory = Factory();
        _ = factory.CreateClient();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        Assert.NotNull(scanner.FindJob(Card, ProjectPath));
        var run = store.Create(Project, Card, OrchestrationStage.ReviewDecision,
            "{}", "review:audit", TaskStates.AutoReview, "review-attempt", 1, 1);
        EngineDecisionWaitMarker.Write(folder, run.RunId);
        var scans = index.Misses;
        var invalidations = index.MutationInvalidations;

        Assert.True(store.Supersede(run.RunId, TaskStates.Ready));

        Assert.Equal(scans, index.Misses);
        Assert.Equal(invalidations, index.MutationInvalidations);
        Assert.Null(EngineDecisionWaitMarker.Read(folder));
        Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(folder),
            item => item.Kind == TimelineEventKinds.OrchestrationDecisionSuperseded);
    }

    private string SeedCard(string state)
    {
        var folder = Path.Combine(ProjectPath, state, Card);
        Directory.CreateDirectory(folder);
        WriteTask(folder, state);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Recover this card.");
        return folder;
    }

    private static void WriteTask(string folder, string state)
        => File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = Card, title = "Recovery card", state, order = 1,
            agent = "codex", kind = TaskKinds.Task,
        }));

    private WebApplicationFactory<Program> Factory(IMonolithOrchestrationApplier? applier = null)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(ProjectPath, state));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = ProjectPath,
                    ["WatchPaths:0:RootPath"] = ProjectPath,
                    ["WatchPaths:0:RepositoryPath"] = ProjectPath,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                if (applier is not null)
                {
                    services.RemoveAll<IMonolithOrchestrationApplier>();
                    services.AddSingleton(applier);
                }
            });
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    private sealed class CountingApplier : IMonolithOrchestrationApplier
    {
        public int Count { get; private set; }
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
