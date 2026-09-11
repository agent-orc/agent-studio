using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Covers the G2 "project meta" P1 bundle: epics, tags, project autonomy
/// and execution-runner settings, and the pipeline projections layered on
/// the existing orchestration flow definition.
///
/// There is no shared <c>StudioTestSupport.cs</c> fixture in this repo
/// snapshot (the task brief names <c>StudioTestApiFactory</c> /
/// <c>StudioTestClient.Create</c>, but no such file exists, and creating one
/// here would collide with the other eight parallel groups also expected to
/// use it). This file also cannot reuse <c>StudioEndpointsTests.cs</c>'s own
/// <c>WebApplicationFactory&lt;Program&gt;</c> pattern: this slice's
/// migration and endpoint map are - per the task's hard rules - wired into
/// the shared <c>TaskServerStore.cs</c> migration sequence and
/// <c>Program.cs</c> by the orchestrator later, and <c>WebApplicationFactory</c>
/// replays Program.cs's top-level statements verbatim (its fixed set of
/// <c>Map*</c> calls), with no supported hook to append an extra endpoint
/// map to that exact composition root (an <c>IStartupFilter</c> was tried
/// first and confirmed, empirically, not to reach a minimal-hosting
/// <c>WebApplication</c>'s own endpoint data sources).
///
/// Instead, each test builds its own small real <see cref="WebApplication"/>
/// wired directly to <see cref="TestServer"/>: the same <see cref="TaskServerStore"/>
/// class, driven through its normal public <c>InitializeAsync</c> (which
/// creates the base schema) plus this slice's own
/// <c>ApplyStudioProjectMetaMigrationAsync</c>, with only
/// <c>MapStudioProjectMetaEndpoints()</c> mapped - exercising the real
/// route handlers, model binding, and error mapping over real HTTP.
/// </summary>
public sealed class StudioProjectMetaTests
{
    [Fact]
    public async Task Epic_list_detail_and_completed_count_reflect_project_and_status_filters()
    {
        await using var host = await ProjectMetaTestHost.CreateAsync();
        var store = host.Store;
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Epic WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Epic Project", "EPC"), "test", default);
        var otherProject = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Other Epic Project", "OEP"), "test", default);

        var openEpic = await store.CreateEpicAsync(project.ProjectId, new CreateEpicRequest("Open epic"), "test", default);
        var doneEpic = await store.CreateEpicAsync(
            project.ProjectId, new CreateEpicRequest("Done epic", State: EpicStates.Done), "test", default);
        await store.CreateEpicAsync(
            otherProject.ProjectId, new CreateEpicRequest("Other project epic", State: EpicStates.Done), "test", default);

        var all = await host.Client.GetFromJsonAsync<EpicListResponse>("/api/v1/studio/epics");
        Assert.Equal(3, all!.Epics.Count);

        var scoped = await host.Client.GetFromJsonAsync<EpicListResponse>($"/api/v1/studio/epics?project={project.ProjectId}");
        Assert.Equal(2, scoped!.Epics.Count);

        var scopedAndStatus = await host.Client.GetFromJsonAsync<EpicListResponse>(
            $"/api/v1/studio/epics?project={project.ProjectId}&status={EpicStates.Done}");
        Assert.Equal(doneEpic.EpicId, Assert.Single(scopedAndStatus!.Epics).EpicId);

        var unresolvableProject = await host.Client.GetFromJsonAsync<EpicListResponse>("/api/v1/studio/epics?project=does-not-exist");
        Assert.Empty(unresolvableProject!.Epics);

        var detail = await host.Client.GetFromJsonAsync<EpicDetailDto>($"/api/v1/studio/epics/{openEpic.EpicId}");
        Assert.Equal(openEpic.EpicId, detail!.Epic.EpicId);
        Assert.Equal(0, detail.TaskCount);

        var missing = await host.Client.GetAsync("/api/v1/studio/epics/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var completedAll = await host.Client.GetFromJsonAsync<EpicCompletedCountResponse>("/api/v1/studio/epics/completed/count");
        Assert.Equal(2, completedAll!.Count);

        var completedScoped = await host.Client.GetFromJsonAsync<EpicCompletedCountResponse>(
            $"/api/v1/studio/epics/completed/count?project={project.ProjectId}&includeFixtures=true");
        Assert.Equal(1, completedScoped!.Count);
    }

    [Fact]
    public async Task Epic_sub_task_creation_creates_a_real_task_visible_through_the_epic_and_the_task_store()
    {
        await using var host = await ProjectMetaTestHost.CreateAsync();
        var store = host.Store;
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("SubTask WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "SubTask Project", "SUB"), "test", default);
        var epic = await store.CreateEpicAsync(project.ProjectId, new CreateEpicRequest("Parent epic"), "test", default);

        var response = await host.Client.PostAsJsonAsync(
            $"/api/v1/studio/epics/{epic.EpicId}/sub-tasks", new CreateTaskRequest("Epic sub-task"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = (await response.Content.ReadFromJsonAsync<EpicSubTaskResponse>())!;
        Assert.Equal("Epic sub-task", payload.Task.Title);
        Assert.Equal(project.ProjectId, payload.Task.ProjectId);

        // The task is real and independently visible through the plain task store API.
        var storedTask = await store.GetTaskAsync(project.ProjectId, payload.Task.TaskId, default);
        Assert.NotNull(storedTask);
        Assert.Equal("Epic sub-task", storedTask!.Title);

        var detail = await host.Client.GetFromJsonAsync<EpicDetailDto>($"/api/v1/studio/epics/{epic.EpicId}");
        Assert.Equal(1, detail!.TaskCount);
        Assert.Contains(detail.Tasks, task => task.TaskId == payload.Task.TaskId);

        var missingEpic = await host.Client.PostAsJsonAsync(
            "/api/v1/studio/epics/does-not-exist/sub-tasks", new CreateTaskRequest("Orphan"));
        Assert.Equal(HttpStatusCode.NotFound, missingEpic.StatusCode);
    }

    [Fact]
    public async Task Tags_create_list_reject_duplicate_names_and_delete()
    {
        await using var host = await ProjectMetaTestHost.CreateAsync();

        var create = await host.Client.PostAsJsonAsync("/api/v1/studio/tags", new CreateTagRequest("bug", "#ff0000"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var tag = (await create.Content.ReadFromJsonAsync<TagDto>())!;
        Assert.Equal("bug", tag.Name);

        var duplicate = await host.Client.PostAsJsonAsync("/api/v1/studio/tags", new CreateTagRequest("BUG"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("tag-name-conflict", (await duplicate.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var list = await host.Client.GetFromJsonAsync<TagListResponse>("/api/v1/studio/tags");
        Assert.Single(list!.Tags);

        var delete = await host.Client.DeleteAsync($"/api/v1/studio/tags/{tag.TagId}");
        delete.EnsureSuccessStatusCode();
        Assert.True((await delete.Content.ReadFromJsonAsync<DeleteTagResponse>())!.Deleted);

        var listAfterDelete = await host.Client.GetFromJsonAsync<TagListResponse>("/api/v1/studio/tags");
        Assert.Empty(listAfterDelete!.Tags);

        var deleteMissing = await host.Client.DeleteAsync($"/api/v1/studio/tags/{tag.TagId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteMissing.StatusCode);
    }

    [Fact]
    public async Task SetTaskTagsAsync_helper_links_and_replaces_a_task_tag_set_for_the_task_metadata_group()
    {
        await using var host = await ProjectMetaTestHost.CreateAsync();
        var store = host.Store;
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("TagJoin WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "TagJoin Project", "TGJ"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Tagged task"), "test", default);
        var tagA = await store.CreateTagAsync(new CreateTagRequest("alpha"), "test", default);
        var tagB = await store.CreateTagAsync(new CreateTagRequest("beta"), "test", default);

        // Exercises the exact internal helper G4_TaskMetadata is expected to call
        // from its own write transaction: SetTaskTagsAsync(connection, transaction, taskId, tagIds, ct).
        await store.InWriteTransactionForTests(
            async (connection, transaction)
                => await store.SetTaskTagsAsync(connection, transaction, task.TaskId, [tagA.TagId, tagB.TagId], default),
            default);

        var firstTagIds = await store.ReadTaskTagIdsForTests(task.TaskId, default);
        Assert.Equal(new[] { tagA.TagId, tagB.TagId }.OrderBy(id => id), firstTagIds.OrderBy(id => id));

        // Replacing the set (not accumulating) is the documented contract.
        await store.InWriteTransactionForTests(
            async (connection, transaction)
                => await store.SetTaskTagsAsync(connection, transaction, task.TaskId, [tagB.TagId], default),
            default);

        var secondTagIds = await store.ReadTaskTagIdsForTests(task.TaskId, default);
        Assert.Equal([tagB.TagId], secondTagIds);
    }

    [Fact]
    public async Task Project_autonomy_round_trips_and_rejects_a_stale_expected_version()
    {
        await using var host = await ProjectMetaTestHost.CreateAsync();
        var store = host.Store;
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Autonomy WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Autonomy Project", "AUT"), "test", default);

        var initial = await host.Client.GetFromJsonAsync<ProjectAutonomyDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/autonomy");
        Assert.Equal(0, initial!.Version);

        var update = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/autonomy",
            new UpdateProjectAutonomyRequest("""{"level":"supervised"}""", 0));
        update.EnsureSuccessStatusCode();
        var updated = (await update.Content.ReadFromJsonAsync<ProjectAutonomyDto>())!;
        Assert.Equal(1, updated.Version);
        Assert.Contains("supervised", updated.AutonomyJson);

        var stale = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/autonomy",
            new UpdateProjectAutonomyRequest("""{"level":"autonomous"}""", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var invalidJson = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/autonomy",
            new UpdateProjectAutonomyRequest("not json", 1));
        Assert.Equal(HttpStatusCode.BadRequest, invalidJson.StatusCode);
    }

    [Fact]
    public async Task Project_execution_runner_round_trips_and_rejects_a_stale_expected_version()
    {
        await using var host = await ProjectMetaTestHost.CreateAsync();
        var store = host.Store;
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Runner WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Runner Assign Project", "RAP"), "test", default);

        var assign = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/execution-runner",
            new UpdateProjectExecutionRunnerRequest("runner-alpha", 0));
        assign.EnsureSuccessStatusCode();
        var assigned = (await assign.Content.ReadFromJsonAsync<ProjectExecutionRunnerDto>())!;
        Assert.Equal("runner-alpha", assigned.ExecutionRunnerId);
        Assert.Equal(1, assigned.Version);

        var stale = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/execution-runner",
            new UpdateProjectExecutionRunnerRequest("runner-beta", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var reassign = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/execution-runner",
            new UpdateProjectExecutionRunnerRequest("runner-beta", 1));
        reassign.EnsureSuccessStatusCode();
        Assert.Equal("runner-beta", (await reassign.Content.ReadFromJsonAsync<ProjectExecutionRunnerDto>())!.ExecutionRunnerId);
    }

    [Fact]
    public async Task Pipeline_health_step_and_step_order_round_trip_through_the_underlying_flow_definition()
    {
        await using var host = await ProjectMetaTestHost.CreateAsync();
        var store = host.Store;
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Pipeline WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Pipeline Project", "PLN"), "test", default);

        // A newly created project already has a default flow definition (all five stages).
        var health = await host.Client.GetFromJsonAsync<PipelineHealthResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-health");
        Assert.Equal(5, health!.StepCount);
        Assert.True(health.Healthy);
        Assert.Equal(0, health.Version);

        // Confirms this is a thin projection over the real orchestration flow definition,
        // not a second pipeline model: read it directly through the existing store method.
        var underlying = await store.GetFlowDefinitionAsync(project.ProjectId, default);
        Assert.Equal(underlying!.Stages.Count, health.StepCount);

        var moveStep = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-step",
            new UpsertPipelineStepRequest("Council", 0, health.Version));
        moveStep.EnsureSuccessStatusCode();
        var afterMove = (await moveStep.Content.ReadFromJsonAsync<PipelineDefinitionResponse>())!;
        Assert.Equal("Council", afterMove.Steps[0].StepId);
        Assert.Equal(5, afterMove.Steps.Count);
        Assert.Equal(1, afterMove.Version);

        var staleStep = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-step",
            new UpsertPipelineStepRequest("GateDispatch", null, health.Version));
        Assert.Equal(HttpStatusCode.Conflict, staleStep.StatusCode);
        Assert.Equal("resource-version-mismatch", (await staleStep.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var invalidStep = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-step",
            new UpsertPipelineStepRequest("NotAStage", null, afterMove.Version));
        Assert.Equal(HttpStatusCode.BadRequest, invalidStep.StatusCode);

        var reversedOrder = afterMove.Steps.Select(step => step.StepId).Reverse().ToList();
        var reorder = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-step-order",
            new UpdatePipelineStepOrderRequest(reversedOrder, afterMove.Version));
        reorder.EnsureSuccessStatusCode();
        var reordered = (await reorder.Content.ReadFromJsonAsync<PipelineDefinitionResponse>())!;
        Assert.Equal(reversedOrder, reordered.Steps.Select(step => step.StepId).ToList());
        Assert.Equal(2, reordered.Version);

        var incompleteOrder = await host.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-step-order",
            new UpdatePipelineStepOrderRequest([reversedOrder[0]], reordered.Version));
        Assert.Equal(HttpStatusCode.BadRequest, incompleteOrder.StatusCode);

        var okProbe = await host.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-steps/{reversedOrder[0]}/probe", null);
        okProbe.EnsureSuccessStatusCode();
        Assert.True((await okProbe.Content.ReadFromJsonAsync<PipelineStepProbeResultDto>())!.Ok);

        var badProbe = await host.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/pipeline-steps/NotAStage/probe", null);
        badProbe.EnsureSuccessStatusCode();
        Assert.False((await badProbe.Content.ReadFromJsonAsync<PipelineStepProbeResultDto>())!.Ok);
    }

    /// <summary>
    /// A minimal, real <see cref="WebApplication"/> backed by <see cref="TestServer"/>,
    /// hosting only this slice's <c>TaskServerStore</c> instance and its
    /// <c>MapStudioProjectMetaEndpoints()</c> route group - no auth/protocol
    /// middleware, since <c>RequireTaskServerScope</c> is inert metadata
    /// without <c>TaskServerAuthenticationMiddleware</c> in the pipeline
    /// (which this deliberately omits, exactly as the P0
    /// <c>StudioEndpointsTests.cs</c> host runs without a bearer token too).
    /// </summary>
    private sealed class ProjectMetaTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TempDirectory _tempDirectory;

        private ProjectMetaTestHost(WebApplication app, TempDirectory tempDirectory, TaskServerStore store, HttpClient client)
        {
            _app = app;
            _tempDirectory = tempDirectory;
            Store = store;
            Client = client;
        }

        public TaskServerStore Store { get; }
        public HttpClient Client { get; }

        public static async Task<ProjectMetaTestHost> CreateAsync()
        {
            var tempDirectory = new TempDirectory();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IOptions<TaskServerOptions>>(Options.Create(new TaskServerOptions
            {
                DataDirectory = tempDirectory.Path,
                ListenUrl = string.Empty,
                RetentionSchedulerEnabled = false,
            }));
            builder.Services.AddSingleton<TaskServerStore>();

            var app = builder.Build();
            var store = app.Services.GetRequiredService<TaskServerStore>();
            await store.InitializeAsync();

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = store.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
            }.ToString()))
            {
                await connection.OpenAsync();
                await store.ApplyStudioProjectMetaMigrationAsync(connection, default);
            }

            app.UseRouting();
            app.MapStudioProjectMetaEndpoints();
            await app.StartAsync();

            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            client.DefaultRequestHeaders.Add("X-Client-Id", "studio-project-meta-test");

            return new ProjectMetaTestHost(app, tempDirectory, store, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _tempDirectory.Dispose();
        }
    }
}
