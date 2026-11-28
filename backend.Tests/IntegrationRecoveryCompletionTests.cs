extern alias Runner;

using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class IntegrationRecoveryCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recovery-completion-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Monolith", true, "MechanicalFallback", false)]
    [InlineData("Engine", true, "MechanicalFallback", false)]
    [InlineData("Engine", false, "MechanicalFallback", false)]
    [InlineData("Monolith", true, "NeedsInput", false)]
    [InlineData("Engine", true, "NeedsInput", false)]
    [InlineData("Engine", false, "MechanicalFallback", true)]
    public async Task RecoveryCustodyIsSavedBeforeEitherAuthorityCanReleaseTheAttempt(string mode, bool pinned, string outcome, bool failSave)
    {
        const string project = "recovery";
        const string card = "AGT-4353-fixture";
        const string runnerId = "recovery-runner";
        var watch = Path.Combine(_root, "projects", project);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watch, state));
        var folder = Path.Combine(watch, TaskStates.Ready, card);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
        { id = card, title = "Conflict recovery", state = TaskStates.Ready, agent = "codex", kind = TaskKinds.Task }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Resolve the prepared conflict.");
        var deliverySha = new string('a', 40);
        var pin = new ContinuationBaseRecord("refs/heads/results/delivery", deliverySha, "continue-round-result",
            "reviewed-run", DateTime.UnixEpoch, RetainUntilIntegrated: true);
        if (pinned) Assert.True(ContinuationBaseStore.Save(folder, pin));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root, ["WatchPaths:0:Name"] = project, ["WatchPaths:0:Path"] = watch,
                ["WatchPaths:0:RootPath"] = watch, ["WatchPaths:0:RepositoryPath"] = watch,
                ["ReviewDecisionOrchestrator:Enabled"] = "false", ["Runner:RunLiveness:Enabled"] = "false",
                ["DeliveryChain:Guarded"] = "false", ["Orchestration:Transitions:CodingCompletion"] = mode,
            }));
        });
        using var http = factory.CreateClient();
        using var client = new Runner::AgentRunner.TaskServerClient(http, runnerId);
        await client.RegisterAsync(project, "service", default);
        var lease = (await client.AcquireLeaseAsync(new Runner::AgentRunner.RunLeaseAcquireRequest(
            card, runnerId, project, "host", 42, "codex"), default)).Lease!;
        // Startup recovery must not see an orphan Progress card. Start its
        // lane only after the real lease exists and unrelated workers are off.
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        if (scanner.FindJob(card, watch)!.State == TaskStates.Ready)
        {
            var move = factory.Services.GetRequiredService<TaskStateMachine>().MoveJob(
                card, TaskStates.Progress, watch, expectedSourceState: TaskStates.Ready);
            Assert.Equal(MoveJobStatus.Success, move.Status);
        }
        Assert.Equal(TaskStates.Progress, scanner.FindJob(card, watch)!.State);
        var resultSha = outcome == "NeedsInput" ? deliverySha : new string('b', 40);
        var resultRef = FencedGitRefs.ImmutableResult(lease.AttemptId!, lease.FencingToken, resultSha);
        folder = factory.Services.GetRequiredService<TaskScannerService>().FindJob(card, watch)!.FolderPath;
        if (failSave)
        {
            Assert.Null(ContinuationBaseStore.Read(folder));
            Directory.CreateDirectory(Path.Combine(folder, ContinuationBaseStore.FileName));
            Assert.False(ContinuationBaseStore.SaveAttemptResult(folder,
                new ContinuationBaseRecord(resultRef, resultSha, "mechanical-fallback", lease.AttemptId!, DateTime.UnixEpoch)));
        }
        var request = new Runner::AgentRunner.RemoteRunCompletionRequest(
            card, lease.LeaseId, lease.FencingToken, runnerId, outcome, ResultSha: resultSha,
            AttemptChainId: lease.LeaseId, Repository: "https://example.invalid/recovery.git",
            AttemptId: lease.AttemptId, AuthorityEpoch: lease.AuthorityEpoch, IdempotencyKey: "recovery-completion",
            BaseSha: deliverySha, ImmutableResultRef: resultRef, ArtifactManifestDigest: new string('c', 64),
            Reason: "resume-duration-ceiling", NeedsInputMessage: outcome == "NeedsInput" ? "Missing preparation." : null);
        if (failSave)
        {
            var error = await Assert.ThrowsAsync<Runner::AgentRunner.TaskServerException>(
                () => client.CompleteRunAsync(request, default));
            Assert.Equal(503, error.StatusCode);
            Assert.Equal(TaskStates.Progress, factory.Services.GetRequiredService<TaskScannerService>().FindJob(card, watch)!.State);
            Assert.Empty(factory.Services.GetRequiredService<MonolithOrchestrationStore>().List(project, null));
            Directory.Delete(Path.Combine(folder, ContinuationBaseStore.FileName));
            // The identical fenced completion can finish custody after repair.
        }
        var completion = await client.CompleteRunAsync(request, default);
        Assert.NotNull(completion);
        var current = factory.Services.GetRequiredService<TaskScannerService>().FindJob(card, watch)!;
        if (mode == "Engine" && outcome == "MechanicalFallback")
        {
            Assert.Equal(TaskStates.Progress, current.State);
            Assert.Single(factory.Services.GetRequiredService<MonolithOrchestrationStore>().List(project, null));
        }
        var saved = ContinuationBaseStore.Read(current.FolderPath);
        Assert.NotNull(saved);
        if (pinned) Assert.Equal(pin, saved);
        else Assert.Equal(resultRef, saved.Ref);
        if (outcome == "MechanicalFallback")
            Assert.Equal("resumed-round-failed", SessionContinuationLedgerStore.PeekFreshReason(current.FolderPath));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
