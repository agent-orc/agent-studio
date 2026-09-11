using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioTaskHistoryTests
{
    [Fact]
    public async Task Runs_matches_list_attempts_and_commits_resolve_by_one_based_run_index()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var (project, task) = await SeedProjectAndReadyTaskAsync(store, "Runs WS", "Runs Project", "RNS");
        var (run1, lease1) = await ClaimRunAsync(store, "runner-runs", "instance-runs-1");
        await CompleteRunAsync(store, run1, lease1);
        await MoveTaskBackToReadyAsync(store, project, task);
        var (run2, _) = await ClaimRunAsync(store, "runner-runs", "instance-runs-2");

        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        var expected = await store.ListAttemptsAsync(project.ProjectId, task.TaskId, default);
        Assert.Equal(2, expected.Count);

        var response = await client.GetAsync($"{basePath}/runs");
        response.EnsureSuccessStatusCode();
        var attempts = await response.Content.ReadFromJsonAsync<List<ExecutionAttemptTimelineDto>>();
        Assert.NotNull(attempts);
        Assert.Equal(expected.Select(item => item.Run.RunId), attempts.Select(item => item.Run.RunId));
        Assert.Equal(run1.RunId, attempts[0].Run.RunId);
        Assert.Equal(run2.RunId, attempts[1].Run.RunId);

        var commits1 = await client.GetFromJsonAsync<RunCommitsDto>($"{basePath}/runs/1/commits");
        Assert.Equal(run1.RunId, commits1!.RunId);
        Assert.Equal(1, commits1.RunIndex);

        var commits2 = await client.GetFromJsonAsync<RunCommitsDto>($"{basePath}/runs/2/commits");
        Assert.Equal(run2.RunId, commits2!.RunId);
        Assert.Equal(2, commits2.RunIndex);

        var outOfRange = await client.GetAsync($"{basePath}/runs/3/commits");
        Assert.Equal(HttpStatusCode.NotFound, outOfRange.StatusCode);

        var invalidIndex = await client.GetAsync($"{basePath}/runs/0/commits");
        Assert.Equal(HttpStatusCode.NotFound, invalidIndex.StatusCode);

        // The connector's reserved unscoped-project literal must resolve the same task transparently.
        var unscopedPath = $"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{task.TaskId}";
        var unscopedRuns = await client.GetFromJsonAsync<List<ExecutionAttemptTimelineDto>>($"{unscopedPath}/runs");
        Assert.Equal(2, unscopedRuns!.Count);
    }

    [Fact]
    public async Task Timeline_includes_entries_for_a_run_and_an_event_sorted_by_time()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var (project, task) = await SeedProjectAndReadyTaskAsync(store, "Timeline WS", "Timeline Project", "TLN");
        var (run, _) = await ClaimRunAsync(store, "runner-timeline", "instance-timeline");
        await IngestEventAsync(store, run, LifecycleEventKinds.AgentMessage, """{"text":"hello"}""");

        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";
        var timeline = await client.GetFromJsonAsync<StudioTaskTimelineResponse>($"{basePath}/timeline");

        Assert.Contains(
            timeline!.Entries,
            entry => entry.Kind == StudioTaskTimelineEntryKinds.RunStarted && entry.RunId == run.RunId);
        Assert.Contains(
            timeline.Entries,
            entry => entry.Kind == StudioTaskTimelineEntryKinds.Event && entry.EventKind == LifecycleEventKinds.AgentMessage);
        Assert.Equal(
            timeline.Entries.OrderBy(entry => entry.OccurredAt).Select(entry => entry.Summary),
            timeline.Entries.Select(entry => entry.Summary));
    }

    [Fact]
    public async Task Session_events_and_agent_work_projections_filter_to_session_oriented_kinds_on_the_latest_run()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var (project, task) = await SeedProjectAndReadyTaskAsync(store, "Agent Work WS", "Agent Work Project", "AWK");
        var (run, _) = await ClaimRunAsync(store, "runner-agent-work", "instance-agent-work");
        await IngestEventAsync(store, run, LifecycleEventKinds.AgentMessage, """{"text":"turn one"}""");
        await IngestEventAsync(store, run, LifecycleEventKinds.ToolTrace, """{"tool":"bash"}""");
        await IngestEventAsync(store, run, LifecycleEventKinds.RunnerTrace, """{"note":"heartbeat"}""");
        await IngestEventAsync(store, run, "execution.outcome.classified", """{"outcome":"success"}""");

        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        var sessionEvents = await client.GetFromJsonAsync<List<EventDto>>($"{basePath}/session-events");
        Assert.Equal(3, sessionEvents!.Count);
        Assert.DoesNotContain(sessionEvents, evt => evt.Kind == "execution.outcome.classified");

        var detail = await client.GetFromJsonAsync<StudioAgentWorkDetailResponse>($"{basePath}/agent-work-detail");
        Assert.Equal(run.RunId, detail!.RunId);
        Assert.Equal(2, detail.AgentEvents.Count);
        Assert.DoesNotContain(detail.AgentEvents, evt => evt.Kind == LifecycleEventKinds.RunnerTrace);

        var summary = await client.GetFromJsonAsync<StudioAgentWorkSummaryResponse>($"{basePath}/agent-work-summary");
        Assert.Equal(run.RunId, summary!.RunId);
        Assert.Equal(1, summary.AgentMessageCount);
        Assert.Equal(1, summary.ToolTraceCount);
        Assert.Equal(1, summary.RunnerTraceCount);
        Assert.NotNull(summary.FirstEventAt);
        Assert.NotNull(summary.LastEventAt);
    }

    [Fact]
    public async Task Diff_and_files_report_unavailable_until_a_matching_artifact_exists()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var (project, task) = await SeedProjectAndReadyTaskAsync(store, "Diff WS", "Diff Project", "DFF");
        var (run, _) = await ClaimRunAsync(store, "runner-diff", "instance-diff");
        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        var diffBefore = await client.GetFromJsonAsync<RunDiffResponse>($"{basePath}/runs/1/diff");
        Assert.False(diffBefore!.Available);
        var filesBefore = await client.GetFromJsonAsync<RunFilesResponse>($"{basePath}/runs/1/files");
        Assert.False(filesBefore!.Available);

        var diffText = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        await IngestArtifactAsync(store, run, "changes.patch", "text/x-diff", diffText);
        var filesJson = """["a.txt","b.txt"]""";
        await IngestArtifactAsync(store, run, "files-changed.json", "application/json", filesJson);

        var diffAfter = await client.GetFromJsonAsync<RunDiffResponse>($"{basePath}/runs/1/diff");
        Assert.True(diffAfter!.Available);
        Assert.Equal(diffText, Encoding.UTF8.GetString(Convert.FromBase64String(diffAfter.ContentBase64!)));

        var filesAfter = await client.GetFromJsonAsync<RunFilesResponse>($"{basePath}/runs/1/files");
        Assert.True(filesAfter!.Available);
        Assert.Equal(filesJson, Encoding.UTF8.GetString(Convert.FromBase64String(filesAfter.ContentBase64!)));
    }

    [Fact]
    public async Task Context_reports_unavailable_without_a_task_orchestrator_context_and_real_turns_once_present()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var (project, task) = await SeedProjectAndReadyTaskAsync(store, "Context WS", "Context Project", "CTX");
        var (run, _) = await ClaimRunAsync(store, "runner-context", "instance-context");
        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        var before = await client.GetFromJsonAsync<RunContextResponse>($"{basePath}/runs/1/context");
        Assert.False(before!.Available);

        var turn = new OrchestratorContextTurnDto(
            $"turn_{Guid.NewGuid():N}", DateTime.UtcNow, "user", "What should the run do next?");
        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId, task.TaskId, new AppendOrchestratorContextTurnRequest(turn), "test", default);

        var after = await client.GetFromJsonAsync<RunContextResponse>($"{basePath}/runs/1/context");
        Assert.True(after!.Available);
        Assert.Contains(after.Turns!, item => item.Body == "What should the run do next?");
    }

    [Fact]
    public async Task Claude_session_info_and_plan_round_trip_a_row_inserted_directly_via_the_store()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Claude WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Claude Project", "CLD"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Claude task"), "test", default);
        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        // A missing row is a legitimate empty state (200 OK, empty/null body), never a 404 or 500.
        var emptySession = await client.GetAsync($"{basePath}/claude/session-info");
        emptySession.EnsureSuccessStatusCode();
        var emptySessionBody = (await emptySession.Content.ReadAsStringAsync()).Trim();
        Assert.True(emptySessionBody is "" or "null", $"Unexpected empty-state body: '{emptySessionBody}'");

        var emptyPlan = await client.GetAsync($"{basePath}/plan");
        emptyPlan.EnsureSuccessStatusCode();
        var emptyPlanBody = (await emptyPlan.Content.ReadAsStringAsync()).Trim();
        Assert.True(emptyPlanBody is "" or "null", $"Unexpected empty-state body: '{emptyPlanBody}'");

        await store.SetClaudeSessionInfoAsync(task.TaskId, "sess-abc123", "claude-sonnet-5", default);
        var session = await client.GetFromJsonAsync<ClaudeSessionInfoDto>($"{basePath}/claude/session-info");
        Assert.Equal("sess-abc123", session!.SessionId);
        Assert.Equal("claude-sonnet-5", session.Model);

        await store.SetTaskPlanAsync(task.TaskId, "# Plan\n\n- Step one\n", 1, default);
        var plan = await client.GetFromJsonAsync<TaskPlanDto>($"{basePath}/plan");
        Assert.Equal("# Plan\n\n- Step one\n", plan!.PlanMarkdown);
        Assert.Equal(1, plan.Version);
    }

    [Fact]
    public async Task Step_prompts_returns_one_entry_per_flow_stage_with_an_empty_prompt_list()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Steps WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Steps Project", "STP"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Steps task"), "test", default);
        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        var flow = await store.GetFlowDefinitionAsync(project.ProjectId, default);
        var steps = await client.GetFromJsonAsync<StepPromptsResponse>($"{basePath}/step-prompts");

        Assert.Equal(flow!.Stages.Count, steps!.Steps.Count);
        Assert.All(steps.Steps, step => Assert.Empty(step.Prompts));
    }

    [Fact]
    public async Task Unknown_task_returns_not_found_for_every_route_in_the_bundle()
    {
        await using var host = await TaskHistoryTestHost.CreateAsync();
        var (client, store) = (host.Client, host.Store);

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Missing WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Missing Project", "MIS"), "test", default);
        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/does-not-exist";

        var response = await client.GetAsync($"{basePath}/timeline");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<(ProjectDto Project, TaskDto Task)> SeedProjectAndReadyTaskAsync(
        TaskServerStore store, string workspaceName, string projectName, string prefix)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest(workspaceName), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, projectName, prefix), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Seeded task", State: "2-ready"), "test", default);
        return (project, task);
    }

    private static async Task<(RunDto Run, LeaseDto Lease)> ClaimRunAsync(
        TaskServerStore store, string runnerId, string instanceId)
    {
        await store.RegisterRunnerAsync(
            runnerId,
            new RegisterRunnerRequest(
                "runner", $"host-{runnerId}", instanceId, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test",
            default);
        var claim = await store.ClaimAsync(new ClaimRequest(runnerId, instanceId), "test", default);
        Assert.Equal("claimed", claim.Status);
        return (claim.Run!, claim.Lease!);
    }

    private static async Task CompleteRunAsync(TaskServerStore store, RunDto run, LeaseDto lease)
    {
        await store.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                lease.RunnerId,
                lease.InstanceId,
                lease.LeaseId,
                lease.Fence,
                "blocked",
                "Test completion",
                IdempotencyKey: $"completion:{run.RunId}",
                Sequence: 1),
            "test",
            default);
    }

    private static async Task MoveTaskBackToReadyAsync(TaskServerStore store, ProjectDto project, TaskDto task)
    {
        var current = await store.GetTaskAsync(project.ProjectId, task.TaskId, default);
        await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "2-ready", current!.Version),
            "test",
            default);
    }

    private static async Task IngestEventAsync(TaskServerStore store, RunDto run, string kind, string payloadJson)
    {
        await store.IngestEventAsync(
            run.RunId,
            new EventIngestRequest(
                $"evt_{Guid.NewGuid():N}", kind, payloadJson, $"event:{run.RunId}:{Guid.NewGuid():N}", run.Fence!.Value),
            "test",
            default);
    }

    private static async Task IngestArtifactAsync(
        TaskServerStore store, RunDto run, string name, string mediaType, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await store.IngestArtifactAsync(
            run.RunId,
            new ArtifactIngestRequest(
                $"art_{Guid.NewGuid():N}",
                name,
                mediaType,
                Convert.ToBase64String(bytes),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                $"artifact:{run.RunId}:{Guid.NewGuid():N}",
                run.Fence!.Value),
            "test",
            default);
    }

    /// <summary>
    /// Test-only host for this bundle's routes. <c>Program.cs</c> does not
    /// (yet) call <c>MapStudioTaskHistoryEndpoints</c> itself -- per the
    /// group's hard rules, pre-existing files including <c>Program.cs</c>
    /// are not edited here, so the actual one-line wiring into the
    /// production pipeline is left for the integrator once all P1 groups
    /// land. Rather than fighting minimal-hosting internals to retrofit that
    /// single line onto the full production <c>Program</c> pipeline (its
    /// endpoints are mapped directly against the already-built
    /// <c>WebApplication</c>, which an <see cref="Microsoft.AspNetCore.Hosting.IStartupFilter"/>
    /// registered via <c>WebApplicationFactory</c> cannot reach -- it only
    /// sees a fresh <c>ApplicationBuilder</c>, confirmed empirically), this
    /// builds a minimal real <c>WebApplication</c> that maps only this
    /// bundle's route group over a real in-memory <c>TestServer</c>, against
    /// a real <see cref="TaskServerStore"/> instance (same construction
    /// pattern as <c>ResultFinalizationStoreTests.Store</c>). This exercises
    /// the endpoints, routing, and store logic end to end over real HTTP;
    /// it intentionally does not exercise the production authentication or
    /// protocol middleware, which are that other bundle's concern and are
    /// already covered by <c>StudioEndpointsTests</c>. <c>RequireTaskServerScope</c>
    /// only attaches endpoint metadata (see <c>TaskServerScopeMetadata.cs</c>)
    /// and is inert without that middleware, so omitting it here does not
    /// mask any authorization behavior under test.
    /// </summary>
    private sealed class TaskHistoryTestHost : IAsyncDisposable
    {
        private readonly TempDirectory _temp;
        private readonly WebApplication _app;

        public TaskServerStore Store { get; }
        public HttpClient Client { get; }

        private TaskHistoryTestHost(TempDirectory temp, WebApplication app, TaskServerStore store, HttpClient client)
        {
            _temp = temp;
            _app = app;
            Store = store;
            Client = client;
        }

        public static async Task<TaskHistoryTestHost> CreateAsync()
        {
            var temp = new TempDirectory();
            var store = new TaskServerStore(
                Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
                TimeProvider.System);
            await store.InitializeAsync();
            await store.EnsureStudioTaskHistorySchemaForTestingAsync(default);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(store);
            var app = builder.Build();
            app.MapStudioTaskHistoryEndpoints();
            await app.StartAsync();

            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            client.DefaultRequestHeaders.Add("X-Client-Id", "studio-api-test");

            return new TaskHistoryTestHost(temp, app, store, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            _temp.Dispose();
        }
    }
}
