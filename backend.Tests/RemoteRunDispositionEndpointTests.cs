using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteRunDispositionEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "run-disposition-" + Guid.NewGuid().ToString("N"));
    private string WatchPath => Path.Combine(_root, "projects", "disposition");

    [Fact]
    public async Task Operator_move_revokes_fenced_completion_at_http_boundary()
    {
        await using var factory = BuildFactory();
        using var client = Client(factory);
        var (taskKey, attempt) = Claim(factory, "AGT-REVOKE");

        var response = await client.PostAsJsonAsync(MoveRoute("AGT-REVOKE"),
            new { targetState = TaskStates.Backlog, runIntent = "revoke", reason = "Sharpen brief" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TaskStates.Backlog, Current(factory, "AGT-REVOKE").State);

        var completion = await client.PostAsJsonAsync("/api/runner/completion", Completion(taskKey, attempt));
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(TaskStates.Backlog, Current(factory, "AGT-REVOKE").State);
    }

    [Fact]
    public async Task Batch_move_requires_intent_and_revokes_when_explicit()
    {
        await using var factory = BuildFactory();
        using var client = Client(factory);
        var (taskKey, attempt) = Claim(factory, "AGT-BATCH-REVOKE");
        var executor = factory.Services.GetRequiredService<IBatchMoveItemExecutor>();
        var item = new BatchMoveItem
        {
            JobId = "AGT-BATCH-REVOKE", WatchPath = WatchPath, TargetState = TaskStates.Backlog,
        };

        var missing = await executor.ExecuteAsync(item, default, "human:owner");
        Assert.Equal("rejected", missing.Status);
        Assert.Equal("run-intent-required", missing.Message);
        Assert.Equal(TaskStates.Progress, Current(factory, item.JobId).State);

        var moved = await executor.ExecuteAsync(item with { RunIntent = "revoke" }, default, "human:owner");
        Assert.Equal("moved", moved.Status);
        var completion = await client.PostAsJsonAsync("/api/runner/completion", Completion(taskKey, attempt));
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(TaskStates.Backlog, Current(factory, item.JobId).State);
    }

    [Fact]
    public async Task Steer_then_operator_move_revokes_the_still_live_attempt()
    {
        await using var factory = BuildFactory();
        using var client = Client(factory);
        var (taskKey, attempt) = Claim(factory, "AGT-STEER-REVOKE");
        var mutations = factory.Services.GetRequiredService<TaskMutationService>();
        Assert.NotNull(mutations.SavePendingIntent("AGT-STEER-REVOKE", "continue", "Use the correction",
            "operator-continue", null, WatchPath));

        var steer = await client.PostAsJsonAsync(MoveRoute("AGT-STEER-REVOKE"),
            new { targetState = TaskStates.Ready, runIntent = "steer" });
        Assert.Equal(HttpStatusCode.OK, steer.StatusCode);
        Assert.Equal(TaskStates.Ready, Current(factory, "AGT-STEER-REVOKE").State);

        var revoke = await client.PostAsJsonAsync(MoveRoute("AGT-STEER-REVOKE"),
            new { targetState = TaskStates.Backlog, runIntent = "revoke" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var completion = await client.PostAsJsonAsync("/api/runner/completion", Completion(taskKey, attempt));
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(TaskStates.Backlog, Current(factory, "AGT-STEER-REVOKE").State);
    }

    [Fact]
    public async Task Explicit_steer_keeps_completion_authorized_at_http_boundary()
    {
        await using var factory = BuildFactory();
        using var client = Client(factory);
        var (taskKey, attempt) = Claim(factory, "AGT-STEER-DELIVER");
        var mutations = factory.Services.GetRequiredService<TaskMutationService>();
        Assert.NotNull(mutations.SavePendingIntent("AGT-STEER-DELIVER", "continue", "Next round",
            "operator-continue", null, WatchPath));

        var steer = await client.PostAsJsonAsync(MoveRoute("AGT-STEER-DELIVER"),
            new { targetState = TaskStates.Ready, runIntent = "steer" });
        Assert.Equal(HttpStatusCode.OK, steer.StatusCode);

        var completion = await client.PostAsJsonAsync("/api/runner/completion", Completion(taskKey, attempt));
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        // The queued continuation keeps the card Ready after this attempt
        // settles; the completion itself remains authorized.
        Assert.Equal(TaskStates.Ready, Current(factory, "AGT-STEER-DELIVER").State);
        Assert.Equal(AttemptLifecycleState.Failed,
            factory.Services.GetRequiredService<AttemptAuthorityService>().GetRun(attempt.AttemptId)!.State);
    }

    [Fact]
    public async Task Edited_prompt_offers_completion_and_decision_endpoint_discards_it()
    {
        await using var factory = BuildFactory();
        using var client = Client(factory);
        var (taskKey, attempt) = Claim(factory, "AGT-OLDER-BRIEF");
        var original = attempt.BriefVersion;
        var promptEdit = await client.PutAsJsonAsync(
            $"/api/tasks/AGT-OLDER-BRIEF/files/prompt.md?watchPath={Uri.EscapeDataString(WatchPath)}",
            new { content = "Sharpened brief" });
        Assert.Equal(HttpStatusCode.OK, promptEdit.StatusCode);
        Assert.NotEqual(original, BriefVersionStore.ReadOrCreate(Current(factory, "AGT-OLDER-BRIEF").FolderPath));

        var completion = await client.PostAsJsonAsync("/api/runner/completion", DeliveredCompletion(taskKey, attempt));
        Assert.True(completion.StatusCode == HttpStatusCode.OK,
            await completion.Content.ReadAsStringAsync());
        var parked = Current(factory, "AGT-OLDER-BRIEF");
        Assert.Equal(TaskStates.Escalated, parked.State);
        Assert.Equal("pending", OlderBriefDeliveryStore.Read(parked.FolderPath)?.Status);

        var decision = await client.PostAsJsonAsync(
            $"/api/tasks/AGT-OLDER-BRIEF/older-brief-delivery/decision?watchPath={Uri.EscapeDataString(WatchPath)}",
            new { decision = "discard" });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        Assert.Equal(TaskStates.Ready, Current(factory, "AGT-OLDER-BRIEF").State);
    }

    [Fact]
    public async Task Saving_an_unrelated_file_does_not_change_the_claimed_brief_version()
    {
        await using var factory = BuildFactory();
        using var client = Client(factory);
        var (_, attempt) = Claim(factory, "AGT-UNCHANGED-BRIEF");
        var save = await client.PutAsJsonAsync(
            $"/api/tasks/AGT-UNCHANGED-BRIEF/files/status.md?watchPath={Uri.EscapeDataString(WatchPath)}",
            new { content = "Progress note" });

        Assert.NotEqual(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal(attempt.BriefVersion,
            BriefVersionStore.ReadOrCreate(Current(factory, "AGT-UNCHANGED-BRIEF").FolderPath));
    }

    private (string TaskKey, RunAttemptDto Attempt) Claim(WebApplicationFactory<Program> factory, string id)
    {
        var folder = Path.Combine(WatchPath, TaskStates.Progress, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, title = "Disposition fixture", state = TaskStates.Progress, order = 1, agent = "codex",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Original brief");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        var taskKey = Current(factory, id).TaskKey;
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var claim = authority.AcquireRun(taskKey, "repo", null, "runner-a", "host-a", 120,
            "claim-" + id, briefVersion: BriefVersionStore.ReadOrCreate(folder));
        Assert.Equal(AttemptWriteStatus.Accepted, claim.Status);
        return (taskKey, claim.RunAttempt!);
    }

    private static RemoteRunCompletionRequest Completion(string taskKey, RunAttemptDto attempt) => new(
        taskKey, attempt.Lease!.LeaseId, attempt.LastFence, "runner-a", "blocked",
        AttemptId: attempt.AttemptId, AuthorityEpoch: attempt.AuthorityEpoch,
        IdempotencyKey: "complete-" + attempt.AttemptId);

    private static RemoteRunCompletionRequest DeliveredCompletion(string taskKey, RunAttemptDto attempt)
    {
        var resultSha = new string('a', 40);
        return Completion(taskKey, attempt) with
        {
            Outcome = "done",
            ResultSha = resultSha,
            BaseSha = new string('b', 40),
            ArtifactManifestDigest = new string('c', 64),
            ImmutableResultRef = AgentStudio.TaskServer.Contracts.FencedGitRefs.ImmutableResult(
                attempt.AttemptId, attempt.LastFence, resultSha),
            AttemptChainId = attempt.Lease!.LeaseId,
        };
    }

    private TaskInfo Current(WebApplicationFactory<Program> factory, string id)
    {
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        return factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs().Single(task => task.Id == id);
    }

    private string MoveRoute(string id)
        => $"/api/tasks/{id}/move?watchPath={Uri.EscapeDataString(WatchPath)}";

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(WatchPath, state));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = "disposition",
                    ["WatchPaths:0:Path"] = WatchPath,
                    ["WatchPaths:0:RootPath"] = WatchPath,
                }));
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        return client;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
