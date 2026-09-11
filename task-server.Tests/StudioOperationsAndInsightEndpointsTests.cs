using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Contract tests for the P2 "operations and insight" bundle: durable
/// project settings, the shared studio_operations dispatch ledger, the
/// event-backed bus/token-usage/crash-recovery projections, and the
/// completion-time materialization hook.
/// </summary>
public sealed class StudioOperationsAndInsightEndpointsTests
{
    [Fact]
    public async Task Project_settings_mutate_durably_and_the_snapshot_reflects_them()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        var autoCommit = await client.PutAsJsonAsync($"/api/v1/studio/projects/{project.ProjectId}/auto-commit", new BooleanSettingRequest(true));
        autoCommit.EnsureSuccessStatusCode();
        var maxParallelism = await client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/max-parallelism", new IntegerSettingRequest(4));
        maxParallelism.EnsureSuccessStatusCode();

        var snapshot = await client.GetFromJsonAsync<StudioProjectSnapshotResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/snapshot");
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Settings.AutoCommit);
        Assert.Equal(4, snapshot.Settings.MaxParallelism);

        var renamed = await client.PutAsJsonAsync($"/api/v1/studio/projects/{project.ProjectId}", new UpdateProjectRequest("Renamed"));
        renamed.EnsureSuccessStatusCode();
        Assert.Equal("Renamed", (await renamed.Content.ReadFromJsonAsync<ProjectDto>())!.Name);
    }

    [Fact]
    public async Task Deleting_a_project_cleans_up_every_foreign_key_referencing_table()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        (await client.PutAsJsonAsync($"/api/v1/studio/projects/{project.ProjectId}/auto-commit", new BooleanSettingRequest(true)))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/v1/studio/projects/{project.ProjectId}/urls", new CreateProjectUrlRequest("https://example.test", null)))
            .EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/ownership-mappings/map-1", new UpdateOwnershipMappingRequest("frontend/*", "frontend-team")))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(
            $"/api/v1/studio/drift/{project.ProjectId}/architecture/model-1/elements/element-1/status",
            new UpdateArchitectureElementStatusRequest("confirmed"))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync(
            $"/api/v1/studio/analysis/{project.ProjectId}/schedule", new UpdateAnalysisScheduleRequest("0 0 * * *", true)))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/studio/watch-paths", new CreateWatchPathRequest("proj-watch", "src/**", project.ProjectId)))
            .EnsureSuccessStatusCode();

        // A dispatched studio operation creates a task, and DeleteProjectAsync
        // refuses to delete a project with tasks (same guard as task deletion),
        // so this test proves the other referencing tables are cleaned up
        // without dispatching one; the task-owned studio_operations cleanup
        // path is covered by Deleting_an_unclaimed_dispatched_task_removes_its_studio_operation.
        var deleted = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}");
        deleted.EnsureSuccessStatusCode();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RequireProjectAsync(project.ProjectId, default));
    }

    [Fact]
    public async Task Deleting_an_unclaimed_dispatched_task_removes_its_studio_operation()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        var dispatched = await client.PostAsJsonAsync(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports", new DispatchStudioOperationRequest(null, null));
        dispatched.EnsureSuccessStatusCode();
        var operation = (await dispatched.Content.ReadFromJsonAsync<StudioOperationDto>())!;

        var delete = await client.DeleteAsync($"/api/v1/projects/{project.ProjectId}/tasks/{operation.TaskId}");
        delete.EnsureSuccessStatusCode();

        var notFound = await client.GetAsync($"/api/v1/studio/analysis/{project.ProjectId}/reports/{operation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Project_urls_and_ownership_mappings_round_trip_and_feed_component_routing()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/urls", new CreateProjectUrlRequest("https://example.test", "Staging"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = (await created.Content.ReadFromJsonAsync<StudioProjectUrlDto>())!;

        var updated = await client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/urls/{url.Id}", new UpdateProjectUrlRequest("https://example.test/v2", "Staging v2"));
        updated.EnsureSuccessStatusCode();

        var deleted = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}/urls/{url.Id}");
        deleted.EnsureSuccessStatusCode();

        var mapping = await client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/ownership-mappings/map-1",
            new UpdateOwnershipMappingRequest("frontend/*", "frontend-team"));
        mapping.EnsureSuccessStatusCode();

        var resolved = await client.PostAsJsonAsync(
            "/api/v1/studio/component-routing/resolve", new ComponentRoutingResolveRequest("frontend/src/app/app.ts"));
        resolved.EnsureSuccessStatusCode();
        var resolution = (await resolved.Content.ReadFromJsonAsync<ComponentRoutingResolveResponse>())!;
        Assert.Equal("frontend-team", resolution.Owner);
    }

    [Fact]
    public async Task Analysis_report_dispatch_creates_a_ready_task_and_is_listable()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        var dispatched = await client.PostAsJsonAsync(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports", new DispatchStudioOperationRequest("Summarize recent activity", null));
        dispatched.EnsureSuccessStatusCode();
        var operation = (await dispatched.Content.ReadFromJsonAsync<StudioOperationDto>())!;
        Assert.Equal(StudioOperationStatuses.Dispatched, operation.Status);
        Assert.NotNull(operation.TaskId);

        var task = await store.GetTaskAsync(project.ProjectId, operation.TaskId!, default);
        Assert.Equal("2-ready", task!.State);

        var list = await client.GetFromJsonAsync<StudioOperationListResponse>($"/api/v1/studio/analysis/{project.ProjectId}/reports");
        Assert.Contains(list!.Operations, item => item.Id == operation.Id);

        var detail = await client.GetFromJsonAsync<StudioOperationDto>(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports/{operation.Id}");
        Assert.Equal(operation.Id, detail!.Id);
    }

    [Fact]
    public async Task Completing_a_dispatched_run_materializes_the_studio_operation()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        var dispatched = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/security/audit", new SecurityAuditRequest(null));
        dispatched.EnsureSuccessStatusCode();
        var operation = (await dispatched.Content.ReadFromJsonAsync<StudioOperationDto>())!;

        await store.RegisterRunnerAsync(
            "runner-sec", new RegisterRunnerRequest("runner-sec", "host-sec", "runner-sec:1", "1.0.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-sec", "runner-sec:1"), "test", default);
        Assert.Equal(operation.TaskId, claim.Task!.TaskId);

        var completion = await client.PostAsJsonAsync($"/api/v1/runs/{claim.Run!.RunId}/completion", new CompleteRunRequest(
            "runner-sec", "runner-sec:1", claim.Lease!.LeaseId, claim.Lease.Fence, "failed", "no findings tool available",
            IdempotencyKey: "complete-security-audit", Sequence: 1));
        completion.EnsureSuccessStatusCode();

        var materialized = await client.GetFromJsonAsync<StudioOperationDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/security/reviews/{operation.Id}");
        Assert.Equal(StudioOperationStatuses.Failed, materialized!.Status);
        Assert.Contains("runStatus", materialized.ResultJson);
    }

    [Fact]
    public async Task Code_pattern_drift_is_evaluated_deterministically_without_dispatch()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);

        var rules = await client.GetFromJsonAsync<CodePatternDriftRulesResponse>("/api/v1/studio/drift/actions/code-pattern-drift/rules");
        Assert.NotEmpty(rules!.Rules);

        var evaluated = await client.PostAsJsonAsync("/api/v1/studio/drift/actions/code-pattern-drift", new CodePatternDriftRequest(
            [new CodePatternDriftFile("src/app.ts", "console.log('debug');")]));
        evaluated.EnsureSuccessStatusCode();
        var response = (await evaluated.Content.ReadFromJsonAsync<CodePatternDriftResponse>())!;
        Assert.Contains(response.Findings, finding => finding.RuleId == "no-console-log");
    }

    [Fact]
    public async Task Token_pricing_calculates_without_a_project()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/studio/token-pricing/calculate", new TokenPricingCalculateRequest("claude-sonnet-5", 1000, 500));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<TokenPricingCalculateResponse>())!;
        Assert.True(result.CostUsd > 0);
    }

    [Fact]
    public async Task Watch_paths_round_trip()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);

        var created = await client.PostAsJsonAsync(
            "/api/v1/studio/watch-paths", new CreateWatchPathRequest("frontend-src", "frontend/src/**", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var deleted = await client.DeleteAsync("/api/v1/studio/watch-paths/frontend-src");
        deleted.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bus_messages_and_token_usage_read_back_runner_ingested_events()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Seed task", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync(
            "runner-bus", new RegisterRunnerRequest("runner-bus", "host-bus", "runner-bus:1", "1.0.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-bus", "runner-bus:1"), "test", default);

        await store.IngestEventAsync(claim.Run!.RunId, new EventIngestRequest(
            "evt-bus-1", StudioInsightEventKinds.BusMessage,
            """{"channel":"orchestrator","role":"agent","kind":"status","summary":"Working on it"}""",
            "bus-1", claim.Lease!.Fence), "test", default);
        await store.IngestEventAsync(claim.Run.RunId, new EventIngestRequest(
            "evt-token-1", StudioInsightEventKinds.TokenUsage,
            """{"stage":"coding","inputTokens":100,"outputTokens":50,"costUsd":0.01}""",
            "token-1", claim.Lease.Fence), "test", default);

        var messages = await client.GetFromJsonAsync<StudioBusMessageListResponse>($"/api/v1/studio/bus/{project.ProjectId}/messages");
        Assert.Contains(messages!.Messages, message => message.Summary == "Working on it");

        var summary = await client.GetFromJsonAsync<StudioTokenUsageSummaryResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/token-usage/summary");
        Assert.Equal(100, summary!.InputTokens);
        Assert.Equal(50, summary.OutputTokens);
    }

    [Fact]
    public async Task Crash_recovery_lists_an_expired_lease_and_commit_removes_it_from_pending()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var (_, project) = await SeedProjectAsync(store);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Crash task", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync(
            "runner-crash", new RegisterRunnerRequest("runner-crash", "host-crash", "runner-crash:1", "1.0.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-crash", "runner-crash:1"), "test", default);
        clock.Advance(TimeSpan.FromDays(1));

        var pending = await store.ListCrashRecoveryPendingAsync(default);
        Assert.Contains(pending.Pending, item => item.RunId == claim.Run!.RunId);

        await store.CommitCrashRecoveryAsync(claim.Run!.RunId, "test", default);

        var afterCommit = await store.ListCrashRecoveryPendingAsync(default);
        Assert.DoesNotContain(afterCommit.Pending, item => item.RunId == claim.Run!.RunId);
    }

    [Fact]
    public async Task Supervisor_pause_and_resume_pickup_toggle_the_observation_flag()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        var paused = await client.PostAsync($"/api/v1/studio/supervisor/{project.ProjectId}/intervene/pause-pickup", null);
        paused.EnsureSuccessStatusCode();
        var observationPaused = await client.GetFromJsonAsync<SupervisorObservationResponse>(
            $"/api/v1/studio/supervisor/{project.ProjectId}/observation");
        Assert.True(observationPaused!.PickupPaused);

        var resumed = await client.PostAsync($"/api/v1/studio/supervisor/{project.ProjectId}/intervene/resume", null);
        resumed.EnsureSuccessStatusCode();
        var observationResumed = await client.GetFromJsonAsync<SupervisorObservationResponse>(
            $"/api/v1/studio/supervisor/{project.ProjectId}/observation");
        Assert.False(observationResumed!.PickupPaused);
    }

    [Fact]
    public async Task Proposals_generate_and_decide_and_delete()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioOpsApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project) = await SeedProjectAsync(store);

        var generated = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/generate", new GenerateProposalsRequest(null));
        generated.EnsureSuccessStatusCode();
        var proposal = (await generated.Content.ReadFromJsonAsync<StudioOperationDto>())!;

        var decided = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/{proposal.Id}/decision",
            new ProposalDecisionRequest(StudioOperationStatuses.Accepted));
        decided.EnsureSuccessStatusCode();
        Assert.Equal(StudioOperationStatuses.Accepted, (await decided.Content.ReadFromJsonAsync<StudioOperationDto>())!.Status);

        var deleted = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}/proposals/{proposal.Id}");
        deleted.EnsureSuccessStatusCode();
    }

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project)> SeedProjectAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Ops WS " + Guid.NewGuid().ToString("N")), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Ops Project", "OPS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()),
            "test", default);
        return (workspace, project);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-ops-api-test");
        return client;
    }

    private sealed class StudioOpsApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
