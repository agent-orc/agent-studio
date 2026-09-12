using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioTaskMetadataTests
{
    [Fact]
    public async Task Title_updates_the_shared_task_row_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "Title");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var response = await client.PutAsJsonAsync($"{basePath}/title", new SetTaskTitleRequest("Renamed", task.Version));
        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<TaskDto>())!;
        Assert.Equal("Renamed", updated.Title);
        Assert.Equal(task.Version + 1, updated.Version);

        var stale = await client.PutAsJsonAsync($"{basePath}/title", new SetTaskTitleRequest("Stale rename", task.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Change_project_moves_the_task_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "ChangeProject");
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("CP WS2"), "test", default);
        var otherProject = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "CP Project 2", "CP2"), "test", default);
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var response = await client.PostAsJsonAsync(
            $"{basePath}/change-project", new ChangeTaskProjectRequest(otherProject.ProjectId, task.Version));
        response.EnsureSuccessStatusCode();
        var moved = (await response.Content.ReadFromJsonAsync<TaskDto>())!;
        Assert.Equal(otherProject.ProjectId, moved.ProjectId);
        Assert.Equal(task.Version + 1, moved.Version);

        var stale = await client.PostAsJsonAsync(
            $"{TaskPath(otherProject.ProjectId, task.TaskId)}/change-project",
            new ChangeTaskProjectRequest(project.ProjectId, task.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var missingProject = await client.PostAsJsonAsync(
            $"{TaskPath(otherProject.ProjectId, task.TaskId)}/change-project",
            new ChangeTaskProjectRequest("no-such-project", moved.Version));
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
    }

    [Fact]
    public async Task Cli_type_upserts_the_studio_fields_row_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "CliType");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var response = await client.PutAsJsonAsync($"{basePath}/cli-type", new SetTaskCliTypeRequest("claude", 0));
        response.EnsureSuccessStatusCode();
        var fields = (await response.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!;
        Assert.Equal("claude", fields.CliType);
        Assert.Equal(1, fields.Version);

        var stale = await client.PutAsJsonAsync($"{basePath}/cli-type", new SetTaskCliTypeRequest("codex", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var second = await client.PutAsJsonAsync($"{basePath}/cli-type", new SetTaskCliTypeRequest("codex", 1));
        second.EnsureSuccessStatusCode();
        Assert.Equal("codex", (await second.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!.CliType);
    }

    [Fact]
    public async Task Model_upserts_the_studio_fields_row_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "Model");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var response = await client.PutAsJsonAsync($"{basePath}/model", new SetTaskModelRequest("opus", 0));
        response.EnsureSuccessStatusCode();
        var fields = (await response.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!;
        Assert.Equal("opus", fields.Model);
        Assert.Equal(1, fields.Version);

        var stale = await client.PutAsJsonAsync($"{basePath}/model", new SetTaskModelRequest("sonnet", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Thinking_level_upserts_the_studio_fields_row_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "ThinkingLevel");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var response = await client.PutAsJsonAsync(
            $"{basePath}/thinking-level", new SetTaskThinkingLevelRequest("high", 0));
        response.EnsureSuccessStatusCode();
        var fields = (await response.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!;
        Assert.Equal("high", fields.ThinkingLevel);
        Assert.Equal(1, fields.Version);

        var stale = await client.PutAsJsonAsync(
            $"{basePath}/thinking-level", new SetTaskThinkingLevelRequest("low", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Task_type_upserts_the_studio_fields_row_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "TaskType");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var response = await client.PutAsJsonAsync($"{basePath}/task-type", new SetTaskTaskTypeRequest("bug", 0));
        response.EnsureSuccessStatusCode();
        var fields = (await response.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!;
        Assert.Equal("bug", fields.TaskType);
        Assert.Equal(1, fields.Version);

        var stale = await client.PutAsJsonAsync($"{basePath}/task-type", new SetTaskTaskTypeRequest("feature", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Release_upserts_the_studio_fields_row_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "Release");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var response = await client.PutAsJsonAsync($"{basePath}/release", new SetTaskReleaseRequest("2026.09", 0));
        response.EnsureSuccessStatusCode();
        var fields = (await response.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!;
        Assert.Equal("2026.09", fields.Release);
        Assert.Equal(1, fields.Version);

        var stale = await client.PutAsJsonAsync($"{basePath}/release", new SetTaskReleaseRequest("2026.10", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Epic_assigns_clears_validates_existence_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "Epic");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var missingEpic = await client.PutAsJsonAsync($"{basePath}/epic", new SetTaskEpicRequest("epic-missing", 0));
        Assert.Equal(HttpStatusCode.NotFound, missingEpic.StatusCode);

        await InsertEpicAsync(store, "epic-1", project.ProjectId, "Epic One");
        var assign = await client.PutAsJsonAsync($"{basePath}/epic", new SetTaskEpicRequest("epic-1", 0));
        assign.EnsureSuccessStatusCode();
        var assigned = (await assign.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!;
        Assert.Equal("epic-1", assigned.EpicId);
        Assert.Equal(1, assigned.Version);
        Assert.Equal(1, await CountEpicTaskRowsAsync(store, task.TaskId));

        var stale = await client.PutAsJsonAsync($"{basePath}/epic", new SetTaskEpicRequest("epic-1", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var clear = await client.PutAsJsonAsync($"{basePath}/epic", new SetTaskEpicRequest(null, 1));
        clear.EnsureSuccessStatusCode();
        var cleared = (await clear.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!;
        Assert.Null(cleared.EpicId);
        Assert.Equal(0, await CountEpicTaskRowsAsync(store, task.TaskId));
    }

    [Fact]
    public async Task References_replaces_the_set_validates_existence_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "References");
        var other1 = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Ref target 1"), "test", default);
        var other2 = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Ref target 2"), "test", default);
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var missing = await client.PutAsJsonAsync(
            $"{basePath}/references", new SetTaskReferencesRequest(["no-such-task"], 0));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var response = await client.PutAsJsonAsync(
            $"{basePath}/references", new SetTaskReferencesRequest([other1.TaskId, other2.TaskId], 0));
        response.EnsureSuccessStatusCode();
        var refs = (await response.Content.ReadFromJsonAsync<TaskReferencesDto>())!;
        Assert.Equal(1, refs.Version);
        Assert.Equal(
            new[] { other1.TaskId, other2.TaskId }.OrderBy(id => id, StringComparer.Ordinal),
            refs.ReferenceTaskIds.OrderBy(id => id, StringComparer.Ordinal));

        var stale = await client.PutAsJsonAsync(
            $"{basePath}/references", new SetTaskReferencesRequest([other1.TaskId], 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var replace = await client.PutAsJsonAsync(
            $"{basePath}/references", new SetTaskReferencesRequest([other1.TaskId], 1));
        replace.EnsureSuccessStatusCode();
        var replaced = (await replace.Content.ReadFromJsonAsync<TaskReferencesDto>())!;
        Assert.Equal([other1.TaskId], replaced.ReferenceTaskIds);
    }

    [Fact]
    public async Task Tags_replaces_the_set_validates_existence_and_rejects_a_stale_expected_version()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "Tags");
        var basePath = TaskPath(project.ProjectId, task.TaskId);

        var missing = await client.PutAsJsonAsync($"{basePath}/tags", new SetTaskTagsRequest(["no-such-tag"], 0));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await InsertTagAsync(store, "tag-1", "urgent");
        await InsertTagAsync(store, "tag-2", "frontend");
        var response = await client.PutAsJsonAsync(
            $"{basePath}/tags", new SetTaskTagsRequest(["tag-1", "tag-2"], 0));
        response.EnsureSuccessStatusCode();
        var tags = (await response.Content.ReadFromJsonAsync<TaskTagsDto>())!;
        Assert.Equal(1, tags.Version);
        Assert.Equal(["tag-1", "tag-2"], tags.TagIds.OrderBy(id => id, StringComparer.Ordinal));

        var stale = await client.PutAsJsonAsync($"{basePath}/tags", new SetTaskTagsRequest(["tag-1"], 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("resource-version-mismatch", (await stale.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var replace = await client.PutAsJsonAsync($"{basePath}/tags", new SetTaskTagsRequest(["tag-1"], 1));
        replace.EnsureSuccessStatusCode();
        Assert.Equal(["tag-1"], (await replace.Content.ReadFromJsonAsync<TaskTagsDto>())!.TagIds);
    }

    [Fact]
    public async Task Task_metadata_routes_resolve_by_task_id_alone_for_the_connector_unscoped_project_token()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (project, task) = await CreateProjectAndTaskAsync(store, "Unscoped");
        // The connector substitutes this literal for {projectId} when a legacy
        // single-parameter frontend path (e.g. /api/tasks/{taskId}/title) has no
        // project id to forward.
        var basePath = TaskPath(TaskServerStore.UnscopedProjectToken, task.TaskId);

        var title = await client.PutAsJsonAsync($"{basePath}/title", new SetTaskTitleRequest("Unscoped rename", task.Version));
        title.EnsureSuccessStatusCode();
        var titled = (await title.Content.ReadFromJsonAsync<TaskDto>())!;
        Assert.Equal(project.ProjectId, titled.ProjectId);
        Assert.Equal("Unscoped rename", titled.Title);

        var cliType = await client.PutAsJsonAsync($"{basePath}/cli-type", new SetTaskCliTypeRequest("claude", 0));
        cliType.EnsureSuccessStatusCode();
        Assert.Equal("claude", (await cliType.Content.ReadFromJsonAsync<TaskStudioFieldsDto>())!.CliType);

        var scoped = await store.GetTaskAsync(project.ProjectId, task.TaskId, default);
        Assert.Equal("Unscoped rename", scoped!.Title);
    }

    private static string TaskPath(string projectId, string taskId) => $"/api/v1/projects/{projectId}/tasks/{taskId}";

    private static async Task<(ProjectDto Project, TaskDto Task)> CreateProjectAndTaskAsync(TaskServerStore store, string label)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest($"{label} WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, $"{label} Project", label.ToUpperInvariant()[..Math.Min(3, label.Length)]),
            "test",
            default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest($"{label} task"), "test", default);
        return (project, task);
    }

    private static async Task InsertEpicAsync(TaskServerStore store, string epicId, string projectId, string name)
        => await store.CreateEpicAsync(projectId, new CreateEpicRequest(name, epicId), "test", default);

    private static async Task InsertTagAsync(TaskServerStore store, string tagId, string name)
        => await store.CreateTagAsync(new CreateTagRequest(name, TagId: tagId), "test", default);

    private static async Task<long> CountEpicTaskRowsAsync(TaskServerStore store, string taskId)
    {
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM epic_tasks WHERE task_id = $task;";
        command.Parameters.AddWithValue("$task", taskId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
