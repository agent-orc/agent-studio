using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioTaskLifecycleExtrasTests(StudioClassFixture fixture) : IClassFixture<StudioClassFixture>
{
    [Fact]
    public async Task Create_task_resolves_project_from_body_and_delegates_to_CreateTaskAsync()
    {
        var factory = fixture.Factory;
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
        Assert.Equal(created.TaskId, created.Id);
        Assert.Equal("0-backlog", created.State);

        var fetched = await store.GetTaskAsync(project.ProjectId, created.TaskId, default);
        Assert.NotNull(fetched);
        Assert.Equal("Studio-created task", fetched!.Title);
    }

    [Fact]
    public async Task Magazine_commission_persists_through_the_normal_create_and_detail_routes()
    {
        using var client = StudioTestClient.Create(fixture.Factory);
        var store = fixture.Factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Magazine");
        // clock-independent: reporting dates are serialized data and are never compared with now.
        var audience = new MagazineDeliverableRequest("workspace:" + project.WorkspaceId.ToUpperInvariant(),
            PersonaId: "security-officer", RoleId: "security-officer",
            ReportingWindow: new(new DateOnly(2026, 11, 21), new DateOnly(2026, 11, 27)));

        using var createdResponse = await client.PostAsJsonAsync("/api/v1/studio/tasks", new
        {
            project = project.ProjectId, title = "Research the week", mode = "research", promptMarkdown = "Review changes.",
            magazineDeliverable = audience,
        });
        Assert.True(createdResponse.StatusCode == HttpStatusCode.Created, await createdResponse.Content.ReadAsStringAsync());
        var created = (await createdResponse.Content.ReadFromJsonAsync<TaskDto>())!;
        var reloaded = await client.GetFromJsonAsync<TaskDto>($"/api/v1/projects/-/tasks/{created.TaskKey}");
        Assert.Equal(created.MagazineDeliverable, reloaded!.MagazineDeliverable);
        var declaration = Assert.IsType<MagazineDeliverableDeclaration>(reloaded.MagazineDeliverable);
        Assert.Equal("workspace:" + project.WorkspaceId, declaration.Scope);
        Assert.Equal(created.TaskKey.ToLowerInvariant(), declaration.Slug);
        Assert.Equal(audience.ReportingWindow, declaration.ReportingWindow);
        Assert.Equal("security-officer", declaration.PersonaId);
        Assert.Equal("local-operator", declaration.AuthorizedBy);
        Assert.Equal("awaiting-publication", declaration.PublicationStatus);
        Assert.Null(RunnerRouteAuthorization.LifecycleOnly(reloaded).MagazineDeliverable);
        Assert.Contains("Acceptance authorizes publication", reloaded.Body, StringComparison.Ordinal);
        Assert.All(MagazineDeliverableContract.ResultFiles,
            name => Assert.Contains(name, reloaded.Body, StringComparison.Ordinal));

        using var coding = await client.PostAsJsonAsync("/api/v1/studio/tasks", new
        {
            project = project.ProjectId, title = "Coding card", mode = "coding", magazineDeliverable = audience,
        });
        Assert.Equal(HttpStatusCode.BadRequest, coding.StatusCode);
    }

    [Fact]
    public async Task Magazine_attachment_by_key_keeps_the_original_body_and_normal_legacy_cards()
    {
        using var client = StudioTestClient.Create(fixture.Factory);
        var store = fixture.Factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Editorial");
        var task = await store.CreateStudioTaskAsync(new StudioCreateTaskRequest(project.ProjectId, "Weekly report", "Original brief")
            { Mode = "research" }, "test", default);
        var path = $"/api/v1/projects/-/tasks/{task.TaskKey}";
        Assert.Null((await client.GetFromJsonAsync<TaskDto>(path))!.MagazineDeliverable);
        var audience = new MagazineDeliverableRequest("project:" + project.ProjectId.ToUpperInvariant(), "weekly-report");
        using var attached = await client.PutAsJsonAsync(path + "/magazine-deliverable", new AttachMagazineDeliverableRequest(audience));
        Assert.True(attached.IsSuccessStatusCode, await attached.Content.ReadAsStringAsync());
        var reloaded = (await client.GetFromJsonAsync<TaskDto>(path))!;
        Assert.Equal("Original brief", reloaded.Body);
        Assert.Equal("project:" + project.ProjectId, reloaded.MagazineDeliverable!.Scope);
        using var conflict = await client.PutAsJsonAsync(path + "/magazine-deliverable",
            new AttachMagazineDeliverableRequest(audience with { Slug = "another-report" }));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Magazine_declaration_requires_more_than_a_task_write_credential()
    {
        using var temp = new TempDirectory();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var factory = new StudioTestApiFactory(temp.Path).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:AuthMode"] = "bearer",
                ["TaskServer:AuthToken"] = token,
            })));
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras Rights");
        var writer = await store.CreatePrincipalAsync(new CreatePrincipalRequest("card-writer", TaskServerPrincipalKinds.Studio,
            [TaskServerScopes.TasksRead, TaskServerScopes.TasksWrite]), "test", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", writer.Credential);
        var request = new StudioCreateTaskRequest(project.ProjectId, "Restricted research")
        {
            Mode = "research", MagazineDeliverable = new("workspace:" + project.WorkspaceId),
        };
        using var undeclared = await client.PostAsJsonAsync("/api/v1/studio/tasks", request with { MagazineDeliverable = null });
        Assert.Equal(HttpStatusCode.Created, undeclared.StatusCode);
        using var declared = await client.PostAsJsonAsync("/api/v1/studio/tasks", request);
        Assert.Equal(HttpStatusCode.Forbidden, declared.StatusCode);
        var card = (await undeclared.Content.ReadFromJsonAsync<TaskDto>())!;
        using var attached = await client.PutAsJsonAsync($"/api/v1/projects/-/tasks/{card.TaskKey}/magazine-deliverable",
            new AttachMagazineDeliverableRequest(request.MagazineDeliverable!));
        Assert.Equal(HttpStatusCode.Forbidden, attached.StatusCode);
    }

    [Fact]
    public async Task Historical_magazine_attachment_previews_the_accepted_run_before_confirmation()
    {
        using var client = StudioTestClient.Create(fixture.Factory);
        var store = fixture.Factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Extras History");
        var task = await store.CreateStudioTaskAsync(new StudioCreateTaskRequest(project.ProjectId, "Accepted report",
            State: StudioTaskLanes.HumanReview) { Mode = "research" }, "test", default);
        const string run = "historical-research-run";
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO runs(id, task_id, status, created_at, finished_at)
                VALUES ($run, $task, 'completed', $at, $at);
                INSERT INTO run_completions(run_id, outcome, sequence, idempotency_key, completed_at)
                VALUES ($run, 'success', 4, $run, $at);
                INSERT INTO artifacts(id, run_id, name, media_type, sha256, size_bytes, idempotency_key, fence, created_at)
                VALUES ('historical-article-md', $run, 'results/article.md', 'text/markdown', 'fixture', 1, 'historical-md', 1, $at),
                       ('historical-article-json', $run, 'results/article.json', 'application/json', 'fixture', 1, 'historical-json', 1, $at),
                       ('historical-items-json', $run, 'results/items.json', 'application/json', 'fixture', 1, 'historical-items', 1, $at);
                """;
            command.Parameters.AddWithValue("$task", task.TaskId);
            command.Parameters.AddWithValue("$run", run);
            command.Parameters.AddWithValue("$at", task.CreatedAt.AddMinutes(-1).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest(StudioTaskLanes.Completed), "reviewer", default);
        var path = $"/api/v1/projects/-/tasks/{task.TaskKey}/magazine-deliverable";
        var request = new AttachMagazineDeliverableRequest(new("workspace:" + project.WorkspaceId), "run-" + run);
        using var unconfirmed = await client.PutAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Conflict, unconfirmed.StatusCode);
        using var unknown = await client.PostAsJsonAsync(path + "/preview", request with { AcceptedResultVersion = "run-other" });
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        using var proposed = await client.PostAsJsonAsync(path + "/preview", request);
        Assert.True(proposed.IsSuccessStatusCode, await proposed.Content.ReadAsStringAsync());
        var preview = (await proposed.Content.ReadFromJsonAsync<MagazineDeliverablePreview>())!;
        Assert.Equal(request.AcceptedResultVersion, preview.AcceptedResultVersion);
        Assert.Equal(request.MagazineDeliverable.Scope, preview.Scope);
        using var confirmed = await client.PutAsJsonAsync(path, request with { Confirmation = preview.Confirmation });
        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());
        var reloaded = await client.GetFromJsonAsync<TaskDto>($"/api/v1/projects/-/tasks/{task.TaskKey}");
        Assert.Equal(request.AcceptedResultVersion, reloaded!.MagazineDeliverable!.AcceptedResultVersion);
    }

    [Fact]
    public async Task Dependents_reflect_a_reference_row_written_directly_into_task_references()
    {
        var factory = fixture.Factory;
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
        var factory = fixture.Factory;
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
        var factory = fixture.Factory;
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
        var factory = fixture.Factory;
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
        var factory = fixture.Factory;
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
        var factory = fixture.Factory;
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
        var factory = fixture.Factory;
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
        var compact = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1].ToUpperInvariant();
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
