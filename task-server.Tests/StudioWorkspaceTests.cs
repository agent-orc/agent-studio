using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Coverage for the Studio P1 "workspace" bundle (G9_Workspace):
/// <c>/api/v1/studio/workspace/...</c> (workspace-wide aggregates) and
/// <c>/api/v1/studio/workspaces/{id}/...</c> (per-row CRUD/extension
/// settings). Uses the shared, Program-independent <see cref="StudioWorkspaceTestApiFactory"/>
/// since these routes are not yet wired into <c>Program.cs</c>.
/// </summary>
public sealed class StudioWorkspaceTests
{
    [Fact]
    public async Task Rename_round_trips_and_rejects_a_stale_expected_version()
    {
        await using var harness = await StudioWorkspaceTestHarness.CreateAsync();
        var client = harness.Client;
        var store = harness.Store;

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Original Name"), "test", default);
        Assert.Equal(1, workspace.Version);

        var stale = await client.PutAsJsonAsync(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}",
            new UpdateWorkspaceRequest("Wrong Version Rename", ExpectedVersion: 99));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var rename = await client.PutAsJsonAsync(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}",
            new UpdateWorkspaceRequest("Renamed Workspace", ExpectedVersion: workspace.Version));
        rename.EnsureSuccessStatusCode();
        var renamed = await rename.Content.ReadFromJsonAsync<WorkspaceDto>();
        Assert.Equal("Renamed Workspace", renamed!.Name);
        Assert.Equal(2, renamed.Version);

        var persisted = await store.ListWorkspacesAsync(default);
        Assert.Contains(persisted, item => item.WorkspaceId == workspace.WorkspaceId && item.Name == "Renamed Workspace");

        var missing = await client.PutAsJsonAsync(
            "/api/v1/studio/workspaces/wsp_does_not_exist",
            new UpdateWorkspaceRequest("Anything", ExpectedVersion: 1));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Delete_is_blocked_while_the_workspace_has_projects_and_succeeds_once_empty()
    {
        await using var harness = await StudioWorkspaceTestHarness.CreateAsync();
        var client = harness.Client;
        var store = harness.Store;

        var withProject = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Has Projects"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(withProject.WorkspaceId, "Project", "PRJ"), "test", default);

        var blocked = await client.DeleteAsync($"/api/v1/studio/workspaces/{withProject.WorkspaceId}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("workspace-not-empty", (await blocked.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var empty = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Empty Workspace"), "test", default);
        var deleted = await client.DeleteAsync($"/api/v1/studio/workspaces/{empty.WorkspaceId}");
        deleted.EnsureSuccessStatusCode();
        var response = await deleted.Content.ReadFromJsonAsync<WorkspaceDeleteResponse>();
        Assert.True(response!.Deleted);

        var remaining = await store.ListWorkspacesAsync(default);
        Assert.DoesNotContain(remaining, item => item.WorkspaceId == empty.WorkspaceId);
    }

    [Fact]
    public async Task Reorder_assigns_dense_ascending_ranks_in_the_requested_order()
    {
        await using var harness = await StudioWorkspaceTestHarness.CreateAsync();
        var client = harness.Client;
        var store = harness.Store;

        var a = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("A"), "test", default);
        var b = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("B"), "test", default);
        var c = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("C"), "test", default);

        var requestedOrder = new[] { c.WorkspaceId, a.WorkspaceId, b.WorkspaceId };
        var reorder = await client.PostAsJsonAsync(
            $"/api/v1/studio/workspaces/{a.WorkspaceId}/reorder",
            new ReorderWorkspacesRequest(requestedOrder));
        reorder.EnsureSuccessStatusCode();
        var response = await reorder.Content.ReadFromJsonAsync<ReorderWorkspacesResponse>();
        Assert.Equal(requestedOrder, response!.Workspaces.Select(item => item.WorkspaceId));
        Assert.Equal([0L, 1L, 2L], response.Workspaces.Select(item => item.Rank));

        using var connection = harness.OpenRawConnection();
        Assert.Equal(1L, Scalar(connection, $"SELECT rank FROM workspaces WHERE id = '{a.WorkspaceId}';"));
        Assert.Equal(2L, Scalar(connection, $"SELECT rank FROM workspaces WHERE id = '{b.WorkspaceId}';"));
        Assert.Equal(0L, Scalar(connection, $"SELECT rank FROM workspaces WHERE id = '{c.WorkspaceId}';"));

        var withUnknownId = await client.PostAsJsonAsync(
            $"/api/v1/studio/workspaces/{a.WorkspaceId}/reorder",
            new ReorderWorkspacesRequest([a.WorkspaceId, "wsp_missing"]));
        Assert.Equal(HttpStatusCode.BadRequest, withUnknownId.StatusCode);
    }

    [Fact]
    public async Task Autonomy_and_orchestrator_model_puts_are_versioned_and_settings_reflects_both()
    {
        await using var harness = await StudioWorkspaceTestHarness.CreateAsync();
        var client = harness.Client;
        var store = harness.Store;

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Settings WS"), "test", default);

        var initialSettings = await client.GetFromJsonAsync<WorkspaceStudioSettingsDto>(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}/settings");
        Assert.Equal(0, initialSettings!.Version);
        Assert.Null(initialSettings.Autonomy);
        Assert.Null(initialSettings.OrchestratorModel);

        var autonomy = new WorkspaceAutonomySettings(AutoStartTasks: true, MaxConcurrentAutoRuns: 3);
        var autonomyPut = await client.PutAsJsonAsync(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}/autonomy",
            new UpdateWorkspaceAutonomyRequest(autonomy, ExpectedVersion: 0));
        autonomyPut.EnsureSuccessStatusCode();
        var afterAutonomy = await autonomyPut.Content.ReadFromJsonAsync<WorkspaceStudioSettingsDto>();
        Assert.Equal(1, afterAutonomy!.Version);
        Assert.True(afterAutonomy.Autonomy!.AutoStartTasks);
        Assert.Null(afterAutonomy.OrchestratorModel);

        var staleAutonomy = await client.PutAsJsonAsync(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}/autonomy",
            new UpdateWorkspaceAutonomyRequest(autonomy, ExpectedVersion: 0));
        Assert.Equal(HttpStatusCode.Conflict, staleAutonomy.StatusCode);
        Assert.Equal("resource-version-mismatch", (await staleAutonomy.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var staleModel = await client.PutAsJsonAsync(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}/orchestrator-model",
            new UpdateWorkspaceOrchestratorModelRequest("claude-test-model", ExpectedVersion: 0));
        Assert.Equal(HttpStatusCode.Conflict, staleModel.StatusCode);

        var modelPut = await client.PutAsJsonAsync(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}/orchestrator-model",
            new UpdateWorkspaceOrchestratorModelRequest("claude-test-model", ExpectedVersion: 1));
        modelPut.EnsureSuccessStatusCode();
        var afterModel = await modelPut.Content.ReadFromJsonAsync<WorkspaceStudioSettingsDto>();
        Assert.Equal(2, afterModel!.Version);
        Assert.Equal("claude-test-model", afterModel.OrchestratorModel);
        // The orchestrator-model PUT must not clobber the autonomy settings
        // written moments earlier - both live in the same lazily-upserted row.
        Assert.True(afterModel.Autonomy!.AutoStartTasks);

        var finalSettings = await client.GetFromJsonAsync<WorkspaceStudioSettingsDto>(
            $"/api/v1/studio/workspaces/{workspace.WorkspaceId}/settings");
        Assert.Equal(2, finalSettings!.Version);
        Assert.Equal("claude-test-model", finalSettings.OrchestratorModel);
        Assert.True(finalSettings.Autonomy!.AutoStartTasks);

        var unknownWorkspace = await client.GetAsync("/api/v1/studio/workspaces/wsp_does_not_exist/settings");
        Assert.Equal(HttpStatusCode.NotFound, unknownWorkspace.StatusCode);
    }

    [Fact]
    public async Task Summary_counts_match_rows_inserted_for_one_workspace()
    {
        await using var harness = await StudioWorkspaceTestHarness.CreateAsync();
        var client = harness.Client;
        var store = harness.Store;

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Summary WS"), "test", default);
        var otherWorkspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Other WS"), "test", default);
        var projectA = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "A", "SA"), "test", default);
        var projectB = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "B", "SB"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(otherWorkspace.WorkspaceId, "C", "SC"), "test", default);

        var backlog1 = await store.CreateTaskAsync(projectA.ProjectId, new CreateTaskRequest("T1", State: "0-backlog"), "test", default);
        await store.CreateTaskAsync(projectA.ProjectId, new CreateTaskRequest("T2", State: "0-backlog"), "test", default);
        var ready1 = await store.CreateTaskAsync(projectB.ProjectId, new CreateTaskRequest("T3", State: "2-ready"), "test", default);

        using (var connection = harness.OpenRawConnection())
        {
            InsertRun(connection, "run_summary_1", backlog1.TaskId, "queued");
            InsertRun(connection, "run_summary_2", ready1.TaskId, "running");
            InsertRunner(connection, "runner_summary_active", "active");
            InsertRunner(connection, "runner_summary_retired", "retired");
        }

        var summary = await client.GetFromJsonAsync<WorkspaceSummaryDto>(
            $"/api/v1/studio/workspace/summary?workspaceId={workspace.WorkspaceId}");
        Assert.Equal(workspace.WorkspaceId, summary!.WorkspaceId);
        Assert.Equal(2, summary.ProjectCount);
        Assert.Equal(2, summary.TaskCountsByState["0-backlog"]);
        Assert.Equal(1, summary.TaskCountsByState["2-ready"]);
        Assert.Equal(2, summary.RunCount);
        Assert.True(summary.ActiveRunnerCount >= 1);

        var aggregate = await client.GetFromJsonAsync<WorkspaceSummaryDto>("/api/v1/studio/workspace/summary");
        Assert.Null(aggregate!.WorkspaceId);
        Assert.True(aggregate.ProjectCount >= 3);
    }

    [Fact]
    public async Task Token_timeline_and_expensive_jobs_math_match_inserted_turns()
    {
        await using var harness = await StudioWorkspaceTestHarness.CreateAsync();
        var client = harness.Client;
        var store = harness.Store;

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Tokens WS"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Tokens Project", "TK"), "test", default);
        var today = DateTime.UtcNow.Date;

        using (var connection = harness.OpenRawConnection())
        {
            InsertOrchestratorContext(connection, "ctx_tokens_project", "project", project.ProjectId, taskId: null);
            InsertOrchestratorTurn(connection, "ctx_tokens_project", "turn_1", today.AddHours(1), input: 100, output: 50, cacheRead: 10, cacheCreation: 5);
            InsertOrchestratorTurn(connection, "ctx_tokens_project", "turn_2", today.AddHours(2), input: 200, output: 75, cacheRead: 0, cacheCreation: 0);
        }

        var timeline = await client.GetFromJsonAsync<WorkspaceTokenTimelineResponse>(
            $"/api/v1/studio/workspace/tokens/timeline?workspaceId={workspace.WorkspaceId}");
        var bucket = Assert.Single(timeline!.Buckets);
        Assert.Equal(today.ToString("yyyy-MM-dd"), bucket.Date);
        Assert.Equal(300, bucket.InputTokens);
        Assert.Equal(125, bucket.OutputTokens);
        Assert.Equal(10, bucket.CacheReadTokens);
        Assert.Equal(5, bucket.CacheCreationTokens);
        Assert.Equal(440, bucket.TotalTokens);

        var expensive = await client.GetFromJsonAsync<WorkspaceExpensiveJobsResponse>(
            $"/api/v1/studio/workspace/tokens/expensive-jobs?workspaceId={workspace.WorkspaceId}");
        var job = Assert.Single(expensive!.Jobs);
        Assert.Equal("ctx_tokens_project", job.ContextKey);
        Assert.Equal(440, job.TotalTokens);
    }

    [Fact]
    public async Task Cached_token_routes_serve_a_stable_payload_within_ttl_and_refresh_after_forced_expiry()
    {
        await using var harness = await StudioWorkspaceTestHarness.CreateAsync();
        var client = harness.Client;
        var store = harness.Store;

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Cached WS"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Cached Project", "CC"), "test", default);
        var today = DateTime.UtcNow.Date;

        using (var connection = harness.OpenRawConnection())
        {
            InsertOrchestratorContext(connection, "ctx_cached_project", "project", project.ProjectId, taskId: null);
            InsertOrchestratorTurn(connection, "ctx_cached_project", "turn_cached_1", today.AddHours(1), input: 10, output: 5, cacheRead: 0, cacheCreation: 0);
        }

        var firstCached = await client.GetFromJsonAsync<WorkspaceExpensiveJobsResponse>(
            $"/api/v1/studio/workspace/tokens/expensive-jobs/cached?workspaceId={workspace.WorkspaceId}");
        Assert.Equal(15, firstCached!.Jobs.Single().TotalTokens);

        var firstTimelineCached = await client.GetFromJsonAsync<WorkspaceTokenTimelineResponse>(
            $"/api/v1/studio/workspace/tokens/timeline/cached?workspaceId={workspace.WorkspaceId}");
        Assert.Equal(15, firstTimelineCached!.Buckets.Single().TotalTokens);

        // Insert more usage; within the TTL the cached routes must still serve
        // the stale, already-computed payload.
        using (var connection = harness.OpenRawConnection())
            InsertOrchestratorTurn(connection, "ctx_cached_project", "turn_cached_2", today.AddHours(2), input: 1000, output: 1000, cacheRead: 0, cacheCreation: 0);

        var stillCached = await client.GetFromJsonAsync<WorkspaceExpensiveJobsResponse>(
            $"/api/v1/studio/workspace/tokens/expensive-jobs/cached?workspaceId={workspace.WorkspaceId}");
        Assert.Equal(15, stillCached!.Jobs.Single().TotalTokens);

        var liveNotCached = await client.GetFromJsonAsync<WorkspaceExpensiveJobsResponse>(
            $"/api/v1/studio/workspace/tokens/expensive-jobs?workspaceId={workspace.WorkspaceId}");
        Assert.Equal(2015, liveNotCached!.Jobs.Single().TotalTokens);

        // Force-expire every cache row directly via SQL (mirrors the harness'
        // required verification: reaching past the 60s TTL without waiting).
        using (var connection = harness.OpenRawConnection())
            Execute(connection, "UPDATE studio_workspace_metrics_cache SET computed_at = '2000-01-01T00:00:00.0000000Z';");

        var refreshedJobs = await client.GetFromJsonAsync<WorkspaceExpensiveJobsResponse>(
            $"/api/v1/studio/workspace/tokens/expensive-jobs/cached?workspaceId={workspace.WorkspaceId}");
        Assert.Equal(2015, refreshedJobs!.Jobs.Single().TotalTokens);

        var refreshedTimeline = await client.GetFromJsonAsync<WorkspaceTokenTimelineResponse>(
            $"/api/v1/studio/workspace/tokens/timeline/cached?workspaceId={workspace.WorkspaceId}");
        Assert.Equal(2015, refreshedTimeline!.Buckets.Single().TotalTokens);
    }


    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void InsertRun(SqliteConnection connection, string runId, string taskId, string status)
        => Execute(connection, $"""
            INSERT INTO runs(id, task_id, status, created_at)
            VALUES ('{runId}', '{taskId}', '{status}', '{DateTime.UtcNow:O}');
            """);

    private static void InsertRunner(SqliteConnection connection, string runnerId, string status)
        => Execute(connection, $"""
            INSERT INTO runners(
                id, name, host_id, instance_id, runner_version, protocol_version,
                capabilities_json, status, registered_at, last_seen_at)
            VALUES (
                '{runnerId}', '{runnerId}', '{runnerId}-host', '{runnerId}-instance', '1.0.0', 1,
                '[]', '{status}', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}');
            """);

    private static void InsertOrchestratorContext(
        SqliteConnection connection, string contextKey, string kind, string projectId, string? taskId)
        => Execute(connection, $"""
            INSERT INTO orchestrator_contexts(context_key, kind, project_id, task_id, summary, created_at, updated_at, hidden_at)
            VALUES (
                '{contextKey}', '{kind}', '{projectId}', {(taskId is null ? "NULL" : $"'{taskId}'")},
                'summary', '{DateTime.UtcNow:O}', '{DateTime.UtcNow:O}', NULL);
            """);

    private static void InsertOrchestratorTurn(
        SqliteConnection connection, string contextKey, string turnId, DateTime createdAt,
        long input, long output, long cacheRead, long cacheCreation)
        => Execute(connection, $"""
            INSERT INTO orchestrator_context_turns(
                context_key, turn_id, created_at, role, body,
                input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, payload_sha256)
            VALUES (
                '{contextKey}', '{turnId}', '{createdAt:O}', 'user', 'body',
                {input}, {output}, {cacheRead}, {cacheCreation}, '{turnId}-sha');
            """);
}

/// <summary>
/// Bundles a <see cref="StudioWorkspaceTestApiFactory"/>-built app, its backing
/// <see cref="TempDirectory"/>, and an already-configured <see cref="HttpClient"/>
/// into one disposable so every test needs exactly one <c>await using</c>.
/// </summary>
internal sealed class StudioWorkspaceTestHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp;
    private readonly WebApplication _app;

    private StudioWorkspaceTestHarness(TempDirectory temp, WebApplication app)
    {
        _temp = temp;
        _app = app;
        Store = app.Services.GetRequiredService<TaskServerStore>();
        Client = StudioWorkspaceTestClient.Create(app);
    }

    public TaskServerStore Store { get; }
    public HttpClient Client { get; }

    public static async Task<StudioWorkspaceTestHarness> CreateAsync()
    {
        var temp = new TempDirectory();
        var app = await StudioWorkspaceTestApiFactory.CreateAsync(
            temp.Path,
            webApp => webApp.MapStudioWorkspaceEndpoints(),
            (store, connection, ct) => store.ApplyStudioWorkspaceMigrationAsync(connection, ct));
        return new StudioWorkspaceTestHarness(temp, app);
    }

    public SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        _temp.Dispose();
    }
}
