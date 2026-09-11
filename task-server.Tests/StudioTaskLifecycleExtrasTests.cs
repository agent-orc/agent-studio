using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioTaskLifecycleExtrasTests
{
    [Fact]
    public async Task Create_task_resolves_project_from_body_and_delegates_to_CreateTaskAsync()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Create");

        var response = await client.PostAsJsonAsync(
            "/api/v1/studio/tasks",
            new StudioCreateTaskRequest(project.ProjectId, "Studio-created task", Body: "body text"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TaskDto>();
        Assert.NotNull(created);
        Assert.Equal(project.ProjectId, created!.ProjectId);
        Assert.Equal("0-backlog", created.State);

        var fetched = await store.GetTaskAsync(project.ProjectId, created.TaskId, default);
        Assert.NotNull(fetched);
        Assert.Equal("Studio-created task", fetched!.Title);
    }

    [Fact]
    public async Task Dependents_reflect_a_reference_row_written_directly_into_task_references()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Dependents");
        var target = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Target"), "test", default);
        var referencer = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Referencer"), "test", default);

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO task_references(task_id, reference_task_id, created_at) VALUES ($task, $reference, $now);";
            command.Parameters.AddWithValue("$task", referencer.TaskId);
            command.Parameters.AddWithValue("$reference", target.TaskId);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var dependents = await client.GetFromJsonAsync<List<TaskDependentDto>>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{target.TaskId}/dependents");
        Assert.NotNull(dependents);
        var dependent = Assert.Single(dependents!);
        Assert.Equal(referencer.TaskId, dependent.TaskId);

        // Unscoped-project-token ("-") resolution for a task-scoped route.
        var unscopedDependents = await client.GetFromJsonAsync<List<TaskDependentDto>>(
            $"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{target.TaskId}/dependents");
        Assert.NotNull(unscopedDependents);
        Assert.Single(unscopedDependents!);
    }

    [Fact]
    public async Task Reference_status_reports_live_and_ghost_keys()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras RefStatus");
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Real task"), "test", default);

        var response = await client.PostAsJsonAsync(
            "/api/v1/studio/tasks/reference-status",
            new ReferenceStatusRequest([task.TaskKey, "GHOST-999"]));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ReferenceStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Items.Count);
        var live = body.Items.Single(item => item.Key == task.TaskKey);
        Assert.True(live.Exists);
        Assert.Equal(task.TaskId, live.TaskId);
        var ghost = body.Items.Single(item => item.Key == "GHOST-999");
        Assert.False(ghost.Exists);
    }

    [Fact]
    public async Task Reorder_assigns_dense_ascending_ranks_within_the_lane()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Reorder");
        var first = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("First"), "test", default);
        var second = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Second"), "test", default);
        var third = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Third"), "test", default);

        var response = await client.PostAsJsonAsync(
            "/api/v1/studio/tasks/reorder",
            new ReorderTasksRequest([third.TaskId, first.TaskId, second.TaskId]));
        response.EnsureSuccessStatusCode();

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        Assert.Equal(0L, await RankAsync(connection, third.TaskId));
        Assert.Equal(1L, await RankAsync(connection, first.TaskId));
        Assert.Equal(2L, await RankAsync(connection, second.TaskId));
    }

    [Fact]
    public async Task Archive_lists_only_archived_tasks_and_honors_the_project_filter()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Archive");
        var archived = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Archived task"), "test", default);
        var active = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Active task"), "test", default);

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET archive_state = 'archived', archived_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", archived.TaskId);
            await command.ExecuteNonQueryAsync();
        }

        var response = await client.GetFromJsonAsync<ArchivedTasksResponse>(
            $"/api/v1/studio/tasks/archive?project={project.ProjectId}");
        Assert.NotNull(response);
        Assert.Equal(1L, response!.Total);
        var item = Assert.Single(response.Items);
        Assert.Equal(archived.TaskId, item.TaskId);
        Assert.DoesNotContain(response.Items, entry => entry.TaskId == active.TaskId);
    }

    [Fact]
    public async Task Batch_move_moves_multiple_real_tasks_and_the_get_reflects_it()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras BatchMove");
        var a = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("A"), "test", default);
        var b = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("B"), "test", default);

        var startResponse = await client.PostAsJsonAsync(
            "/api/v1/studio/tasks/batch-move",
            new BatchMoveRequest(
            [
                new BatchMoveItemRequest(a.TaskId, "2-ready"),
                new BatchMoveItemRequest(b.TaskId, "2-ready"),
                new BatchMoveItemRequest("does-not-exist", "2-ready"),
            ]));
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await startResponse.Content.ReadFromJsonAsync<BatchMoveJobResponse>();
        Assert.NotNull(started);
        Assert.Equal("completed", started!.Status);
        Assert.Equal(3, started.Items.Count);
        Assert.Equal(2, started.Items.Count(entry => entry.Success));
        Assert.Contains(started.Items, entry => !entry.Success && entry.TaskId == "does-not-exist");

        var afterA = await store.GetTaskAsync(project.ProjectId, a.TaskId, default);
        Assert.Equal("2-ready", afterA!.State);

        var getResponse = await client.GetFromJsonAsync<BatchMoveJobResponse>(
            $"/api/v1/studio/tasks/batch-move/{started.BatchId}");
        Assert.NotNull(getResponse);
        Assert.Equal(started.BatchId, getResponse!.BatchId);
        Assert.Equal(3, getResponse.Items.Count);

        var missing = await client.GetAsync("/api/v1/studio/tasks/batch-move/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Planning_closure_completes_synchronously_while_other_actions_stay_requested()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Lifecycle");
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Lifecycle task"), "test", default);
        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        var closure = await client.PostAsJsonAsync(
            $"{basePath}/planning-closure", new PlanningClosureRequest(true, "no follow-up"));
        closure.EnsureSuccessStatusCode();
        var closureStatus = await closure.Content.ReadFromJsonAsync<TaskLifecycleActionStatusDto>();
        Assert.Equal("completed", closureStatus!.Status);
        Assert.NotNull(closureStatus.CompletedAt);

        var dossier = await client.PostAsJsonAsync($"{basePath}/concept-dossier", new ConceptDossierRequest(Reason: "not needed"));
        dossier.EnsureSuccessStatusCode();
        var dossierStatus = await dossier.Content.ReadFromJsonAsync<TaskLifecycleActionStatusDto>();
        Assert.Equal("requested", dossierStatus!.Status);
        Assert.Null(dossierStatus.CompletedAt);

        var refresh = await client.PostAsync($"{basePath}/context-usage/refresh", null);
        refresh.EnsureSuccessStatusCode();
        var refreshStatus = await refresh.Content.ReadFromJsonAsync<TaskLifecycleActionStatusDto>();
        Assert.Equal("requested", refreshStatus!.Status);

        var rebase = await client.PostAsync($"{basePath}/integration/rebase", null);
        rebase.EnsureSuccessStatusCode();
        var rebaseStatus = await rebase.Content.ReadFromJsonAsync<TaskLifecycleActionStatusDto>();
        Assert.Equal("requested", rebaseStatus!.Status);

        var promoteConceptBefore = await client.GetFromJsonAsync<TaskLifecycleActionStatusDto>($"{basePath}/promote-concept");
        Assert.Equal("not-requested", promoteConceptBefore!.Status);

        var promoteConceptPost = await client.PostAsJsonAsync($"{basePath}/promote-concept", new PromoteConceptRequest([0, 1]));
        promoteConceptPost.EnsureSuccessStatusCode();
        var promoteConceptPosted = await promoteConceptPost.Content.ReadFromJsonAsync<TaskLifecycleActionStatusDto>();
        Assert.Equal("requested", promoteConceptPosted!.Status);

        var promoteConceptAfter = await client.GetFromJsonAsync<TaskLifecycleActionStatusDto>($"{basePath}/promote-concept");
        Assert.Equal("requested", promoteConceptAfter!.Status);

        var promoteToCoding = await client.GetFromJsonAsync<TaskLifecycleActionStatusDto>($"{basePath}/promote-to-coding");
        Assert.Equal("not-requested", promoteToCoding!.Status);
    }

    [Fact]
    public async Task Lifecycle_action_routes_resolve_through_the_unscoped_project_token()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Unscoped");
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Unscoped task"), "test", default);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{task.TaskId}/planning-closure",
            new PlanningClosureRequest(true));
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<TaskLifecycleActionStatusDto>();
        Assert.Equal(task.TaskId, status!.TaskId);
        Assert.Equal("completed", status.Status);
    }

    private static async Task<ProjectDto> SeedProjectAsync(TaskServerStore store, string name)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest($"{name} WS"), "test", default);
        var compact = name.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var prefix = compact[..Math.Min(6, compact.Length)];
        return await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, name, prefix), "test", default);
    }

    private static async Task<long> RankAsync(SqliteConnection connection, string taskId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rank FROM tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", taskId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
