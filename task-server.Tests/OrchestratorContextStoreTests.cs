using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class OrchestratorContextStoreTests
{
    [Theory]
    [InlineData("project", null, false)]
    [InlineData("project", "7-archive", false)]
    [InlineData("task", "6-completed", false)]
    [InlineData("task", "7-archive", true)]
    public void Visibility_policy_only_hides_archived_task_contexts(
        string kind,
        string? taskState,
        bool expected)
        => Assert.Equal(expected, OrchestratorContextVisibilityPolicy.IsHidden(kind, taskState));

    [Fact]
    public async Task Project_contexts_are_permanent_and_task_contexts_follow_archive_visibility_without_deletion()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Context foundation", State: "2-ready"),
            "test",
            default);

        var initial = await store.ListOrchestratorContextsAsync(false, default);
        var projectContext = Assert.Single(initial);
        Assert.Equal("project:Agent Studio", projectContext.ContextKey);
        Assert.Null(projectContext.HiddenAt);

        var taskContext = await store.EnsureOrchestratorContextAsync(
            project.ProjectId, task.TaskKey, "test", default);
        Assert.Equal($"task:Agent Studio/{task.TaskKey}", taskContext.ContextKey);
        Assert.Equal("Context foundation", taskContext.Summary);
        Assert.Equal(2, (await store.ListOrchestratorContextsAsync(false, default)).Count);

        var archived = await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "7-archive", task.Version),
            "test",
            default);
        Assert.NotNull(archived);

        var visible = await store.ListOrchestratorContextsAsync(false, default);
        Assert.Single(visible);
        Assert.Equal("project:Agent Studio", visible[0].ContextKey);
        var all = await store.ListOrchestratorContextsAsync(true, default);
        var retainedTask = Assert.Single(all, item => item.Kind == OrchestratorContextKinds.Task);
        Assert.NotNull(retainedTask.HiddenAt);

        var restored = await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "6-completed", archived!.Version),
            "test",
            default);
        Assert.NotNull(restored);
        Assert.Equal(2, (await store.ListOrchestratorContextsAsync(false, default)).Count);
    }

    [Fact]
    public async Task Turn_and_source_receipt_are_persisted_with_user_turn_link_and_compact_summary()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var user = new OrchestratorContextTurnDto(
            "usr_1",
            DateTime.UtcNow,
            "user",
            "Explain the foundation context and the receipt persistence contract.");
        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId,
            null,
            new AppendOrchestratorContextTurnRequest(user),
            "test",
            default);

        var receipt = new OrchestratorContextReceiptDto(
            "rcp_1",
            user.TurnId,
            "project:will-be-canonicalized",
            DateTime.UtcNow,
            new OrchestratorContextBudgetReceiptDto(4000, 6000, 8000, 312),
            [new OrchestratorContextSourceReceiptDto(
                "project:Agent Studio/pulse",
                "project-base",
                "v7",
                new string('a', 64),
                "current",
                1200,
                300,
                "included")]);
        var reply = new OrchestratorContextTurnDto(
            "orch_1",
            DateTime.UtcNow.AddSeconds(1),
            "orchestrator",
            "The central receipt is persisted.",
            "gpt-5.5",
            new OrchestratorContextTokenUsageDto("gpt-5.5", 700, 90, 10, 0),
            Receipt: receipt,
            Metadata: new OrchestratorChatMetadataDto(
                "gpt-5.5", "medium", "thread-1", "runner-01",
                user.CreatedAt, user.CreatedAt.AddSeconds(2), user.CreatedAt.AddSeconds(9),
                700, 10, 90, 20, 0.01m, "USD", "TokenEconomy/0.3.5"));
        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId,
            null,
            new AppendOrchestratorContextTurnRequest(reply),
            "test",
            default);

        var transcript = await store.ReadOrchestratorContextAsync(
            project.ProjectId, null, 20, "test", default);
        Assert.Equal(2, transcript.Turns.Count);
        var persistedReply = Assert.Single(transcript.Turns, item => item.Role == "orchestrator");
        Assert.NotNull(persistedReply.Receipt);
        Assert.Equal(user.TurnId, persistedReply.Receipt!.UserTurnId);
        Assert.Equal("project:Agent Studio", persistedReply.Receipt.ContextKey);
        Assert.Equal(new string('a', 64), Assert.Single(persistedReply.Receipt.Sources).Sha256);
        Assert.Equal(reply.Metadata, persistedReply.Metadata);
        Assert.DoesNotContain("The central receipt", JsonSerializer.Serialize(persistedReply.Receipt));

        var context = Assert.Single(await store.ListOrchestratorContextsAsync(false, default));
        Assert.Equal(user.Body, context.Summary);
        Assert.Equal(2, context.TurnCount);
        Assert.Equal(700, context.CumulativeInputTokens);
        Assert.Equal(90, context.CumulativeOutputTokens);
    }

    [Fact]
    public async Task Legacy_import_is_idempotent_and_keeps_the_source_outside_the_central_store()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var request = new ImportLegacyOrchestratorChatRequest(
            new string('b', 64),
            [
                new OrchestratorContextTurnDto("legacy_1", DateTime.UtcNow, "user", "Legacy question"),
                new OrchestratorContextTurnDto("legacy_2", DateTime.UtcNow.AddSeconds(1), "orchestrator", "Legacy answer"),
            ]);

        var first = await store.ImportLegacyOrchestratorChatAsync(
            project.ProjectId, request, "migration", default);
        var second = await store.ImportLegacyOrchestratorChatAsync(
            project.ProjectId, request, "migration", default);

        Assert.Equal(2, first.Imported);
        Assert.Equal(0, first.AlreadyPresent);
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.AlreadyPresent);
        Assert.Equal(2, (await store.ReadOrchestratorContextAsync(
            project.ProjectId, null, 20, "test", default)).Turns.Count);
    }

    [Fact]
    public async Task Http_contract_round_trips_context_turn_and_receipt_through_the_Task_Server_store()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Workspace"));
        workspaceResponse.EnsureSuccessStatusCode();
        var workspace = await workspaceResponse.Content.ReadFromJsonAsync<WorkspaceDto>();
        var projectResponse = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest(workspace!.WorkspaceId, "Agent Studio", "AGT"));
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>();
        var taskResponse = await client.PostAsJsonAsync(
            $"/api/v1/projects/{project!.ProjectId}/tasks",
            new CreateTaskRequest("Open task context", State: "2-ready"));
        taskResponse.EnsureSuccessStatusCode();
        var task = await taskResponse.Content.ReadFromJsonAsync<TaskDto>();

        var openedTaskContext = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            $"/api/v1/orchestrator-contexts/projects/Agent%20Studio/tasks/{task!.TaskKey}/turns");
        Assert.Equal($"task:Agent Studio/{task.TaskKey}", openedTaskContext!.Context.ContextKey);
        Assert.Empty(openedTaskContext.Turns);

        var user = new OrchestratorContextTurnDto(
            "http_user", DateTime.UtcNow, "user", "What context was used?");
        var userResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/turns",
            new AppendOrchestratorContextTurnRequest(user));
        userResponse.EnsureSuccessStatusCode();
        var receipt = new OrchestratorContextReceiptDto(
            "http_receipt",
            user.TurnId,
            "project:Agent Studio",
            DateTime.UtcNow,
            new OrchestratorContextBudgetReceiptDto(4000, 6000, 8000, 10),
            [new OrchestratorContextSourceReceiptDto(
                "project:Agent Studio/state", "project-base", "v1", new string('c', 64),
                "current", 40, 10, "included")]);
        var replyResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/turns",
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "http_reply", DateTime.UtcNow, "orchestrator", "The receipt is central.",
                Receipt: receipt)));
        replyResponse.EnsureSuccessStatusCode();

        var transcript = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/turns");
        Assert.Equal(2, transcript!.Turns.Count);
        Assert.Equal(
            user.TurnId,
            Assert.Single(transcript.Turns, turn => turn.Receipt is not null).Receipt!.UserTurnId);
        var contexts = await client.GetFromJsonAsync<OrchestratorContextListResponse>(
            "/api/v1/orchestrator-contexts");
        Assert.Equal(2, contexts!.Contexts.Count);
        Assert.Equal(
            user.Body,
            Assert.Single(contexts.Contexts, context => context.Kind == OrchestratorContextKinds.Project).Summary);
    }

    [Fact]
    public async Task Workbench_context_is_permanent_isolated_from_the_project_thread_and_never_hidden()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);

        var workbenchContext = await store.EnsureOrchestratorWorkbenchContextAsync(
            project.ProjectId, "AGT-W43", "test", default);
        Assert.Equal("workbench:Agent Studio/AGT-W43", workbenchContext.ContextKey);
        Assert.Equal(OrchestratorContextKinds.Workbench, workbenchContext.Kind);
        Assert.Null(workbenchContext.HiddenAt);
        Assert.Equal(2, (await store.ListOrchestratorContextsAsync(false, default)).Count);

        await store.AppendOrchestratorWorkbenchContextTurnAsync(
            project.ProjectId,
            "AGT-W43",
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "wb_user", DateTime.UtcNow, "user", "What does this Dossier decide?")),
            "test",
            default);

        var workbenchTranscript = await store.ReadOrchestratorWorkbenchContextAsync(
            project.ProjectId, "AGT-W43", 20, "test", default);
        Assert.Single(workbenchTranscript.Turns);

        // The project's own thread must stay empty - a Dossier turn is not a
        // project-chat turn under any read path.
        var projectTranscript = await store.ReadOrchestratorContextAsync(
            project.ProjectId, null, 20, "test", default);
        Assert.Empty(projectTranscript.Turns);

        var listed = await store.ListOrchestratorContextsAsync(false, default);
        var workbenchRow = Assert.Single(listed, item => item.Kind == OrchestratorContextKinds.Workbench);
        Assert.Equal("AGT-W43", workbenchRow.WorkbenchKey);
        Assert.Equal(1, workbenchRow.TurnCount);
    }

    [Fact]
    public async Task Http_contract_ensures_reads_and_appends_a_workbench_context_turn()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Workspace"));
        workspaceResponse.EnsureSuccessStatusCode();
        var workspace = await workspaceResponse.Content.ReadFromJsonAsync<WorkspaceDto>();
        var projectResponse = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest(workspace!.WorkspaceId, "Agent Studio", "AGT"));
        projectResponse.EnsureSuccessStatusCode();

        var openedWorkbenchContext = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/workbenches/AGT-W43/turns");
        Assert.Equal("workbench:Agent Studio/AGT-W43", openedWorkbenchContext!.Context.ContextKey);
        Assert.Empty(openedWorkbenchContext.Turns);

        var userResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/workbenches/AGT-W43/turns",
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "wb_http_user", DateTime.UtcNow, "user", "What is the Dossier decision state?")));
        userResponse.EnsureSuccessStatusCode();

        var transcript = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/workbenches/AGT-W43/turns");
        Assert.Single(transcript!.Turns);

        var projectTranscript = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            "/api/v1/orchestrator-contexts/projects/Agent%20Studio/turns");
        Assert.Empty(projectTranscript!.Turns);

        var contexts = await client.GetFromJsonAsync<OrchestratorContextListResponse>(
            "/api/v1/orchestrator-contexts");
        Assert.Equal(2, contexts!.Contexts.Count);
        Assert.Equal(
            "AGT-W43",
            Assert.Single(contexts.Contexts, context => context.Kind == OrchestratorContextKinds.Workbench).WorkbenchKey);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("idle", true)]
    [InlineData("parked", true)]
    [InlineData("completed", true)]
    [InlineData("failed", true)]
    [InlineData("active", false)]
    [InlineData("queued", false)]
    public void Archive_policy_blocks_only_active_and_queued_contexts(string? runtimeStatus, bool expected)
    {
        Assert.Equal(expected, OrchestratorContextArchivePolicy.CanArchive(runtimeStatus));
        Assert.Equal(expected, OrchestratorContextArchivePolicy.CanSetArchived(true, runtimeStatus));
        // Unarchiving is never blocked by runtime state.
        Assert.True(OrchestratorContextArchivePolicy.CanSetArchived(false, runtimeStatus));
    }

    [Theory]
    [InlineData("project", true)]
    [InlineData("task", true)]
    [InlineData("workbench", true)]
    [InlineData("global", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Archive_policy_accepts_only_project_task_and_workbench_kinds(string? kind, bool expected)
        => Assert.Equal(expected, OrchestratorContextArchivePolicy.IsArchivableKind(kind));

    [Fact]
    public void Archive_policy_next_marker_is_idempotent_and_clears_on_unarchive()
    {
        // clock-independent: pure policy input; the marker never meets "now".
        var earlier = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = earlier.AddDays(1);
        Assert.Equal(now, OrchestratorContextArchivePolicy.NextArchivedAt(null, true, now));
        Assert.Equal(earlier, OrchestratorContextArchivePolicy.NextArchivedAt(earlier, true, now));
        Assert.Null(OrchestratorContextArchivePolicy.NextArchivedAt(earlier, false, now));
        Assert.Null(OrchestratorContextArchivePolicy.NextArchivedAt(null, false, now));
    }

    [Fact]
    public async Task Operator_archive_round_trips_survives_reopen_and_a_new_turn_unarchives()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Archive me", State: "2-ready"),
            "test",
            default);
        var taskContext = await store.EnsureOrchestratorContextAsync(
            project.ProjectId, task.TaskKey, "test", default);
        Assert.Null(taskContext.ArchivedAt);

        var archived = await store.SetOrchestratorContextArchivedAsync(
            taskContext.ContextKey, true, "operator", default);
        Assert.NotNull(archived.ArchivedAt);
        var again = await store.SetOrchestratorContextArchivedAsync(
            taskContext.ContextKey, true, "operator", default);
        Assert.Equal(archived.ArchivedAt, again.ArchivedAt);

        // Archived contexts stay listed (default visibility) with ArchivedAt set.
        var listed = await store.ListOrchestratorContextsAsync(false, default);
        Assert.Equal(2, listed.Count);
        Assert.Equal(archived.ArchivedAt, Assert.Single(listed, item => item.ContextKey == taskContext.ContextKey).ArchivedAt);
        Assert.Null(Assert.Single(listed, item => item.Kind == OrchestratorContextKinds.Project).ArchivedAt);

        var reopened = Store(temp.Path, clock);
        await reopened.InitializeAsync();
        var afterReopen = Assert.Single(
            await reopened.ListOrchestratorContextsAsync(false, default),
            item => item.ContextKey == taskContext.ContextKey);
        Assert.Equal(archived.ArchivedAt, afterReopen.ArchivedAt);

        // Ensuring (opening) the context does not unarchive it; a new turn does.
        Assert.NotNull((await reopened.EnsureOrchestratorContextAsync(
            project.ProjectId, task.TaskKey, "test", default)).ArchivedAt);
        await reopened.AppendOrchestratorContextTurnAsync(
            project.ProjectId,
            task.TaskKey,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "arch_user", clock.GetUtcNow().UtcDateTime, "user", "Back to work")),
            "test",
            default);
        Assert.Null(Assert.Single(
            await reopened.ListOrchestratorContextsAsync(false, default),
            item => item.ContextKey == taskContext.ContextKey).ArchivedAt);

        var rearchived = await reopened.SetOrchestratorContextArchivedAsync(
            taskContext.ContextKey, true, "operator", default);
        Assert.NotNull(rearchived.ArchivedAt);
        Assert.Equal(1, rearchived.TurnCount);
        var unarchived = await reopened.SetOrchestratorContextArchivedAsync(
            taskContext.ContextKey, false, "operator", default);
        Assert.Null(unarchived.ArchivedAt);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => reopened.SetOrchestratorContextArchivedAsync(
            "task:Agent Studio/AGT-999", true, "operator", default));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.SetOrchestratorContextArchivedAsync(
            " ", true, "operator", default));
    }

    [Fact]
    public async Task Operator_archive_is_independent_of_task_archive_hiding()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Hidden later", State: "2-ready"),
            "test",
            default);
        var taskContext = await store.EnsureOrchestratorContextAsync(
            project.ProjectId, task.TaskKey, "test", default);
        await store.SetOrchestratorContextArchivedAsync(taskContext.ContextKey, true, "operator", default);
        await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "7-archive", task.Version),
            "test",
            default);

        // Task-archive hiding still applies to the default list.
        Assert.DoesNotContain(
            await store.ListOrchestratorContextsAsync(false, default),
            item => item.ContextKey == taskContext.ContextKey);
        var hidden = Assert.Single(
            await store.ListOrchestratorContextsAsync(true, default),
            item => item.ContextKey == taskContext.ContextKey);
        Assert.NotNull(hidden.HiddenAt);
        Assert.NotNull(hidden.ArchivedAt);

        // The Studio session list includes hidden and archived contexts.
        var sessions = await store.ListStudioOrchestratorSessionsAsync(default);
        var session = Assert.Single(sessions.Sessions, item => item.ContextKey == taskContext.ContextKey);
        Assert.NotNull(session.HiddenAt);
        Assert.Equal(hidden.ArchivedAt, session.ArchivedAt);
        Assert.Equal(0, session.TurnCount);
        // Runner session reads keep their pre-archive visibility.
        Assert.DoesNotContain(
            (await store.GetGlobalRunnerOrchestratorSessionAsync(default)).Sessions,
            item => item.ContextKey == taskContext.ContextKey);
    }

    [Fact]
    public async Task Migration_adds_archived_at_to_a_pre_workbench_contexts_table()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var workspace = await first.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await first.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Agent Studio", "AGT"),
            "test",
            default);

        // Rewind orchestrator_contexts to the schema-12 shape: no workbench
        // kind, no workbench_key, and no archived_at.
        await using (var connection = new SqliteConnection($"Data Source={first.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                PRAGMA legacy_alter_table = ON;
                ALTER TABLE orchestrator_contexts RENAME TO orchestrator_contexts_current;
                CREATE TABLE orchestrator_contexts(
                    context_key TEXT PRIMARY KEY,
                    kind TEXT NOT NULL CHECK(kind IN ('project', 'task')),
                    project_id TEXT NOT NULL REFERENCES projects(id),
                    task_id TEXT REFERENCES tasks(id),
                    summary TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    hidden_at TEXT,
                    CHECK((kind = 'project' AND task_id IS NULL) OR (kind = 'task' AND task_id IS NOT NULL))
                );
                INSERT INTO orchestrator_contexts(
                    context_key, kind, project_id, task_id, summary, created_at, updated_at, hidden_at)
                SELECT context_key, kind, project_id, task_id, summary, created_at, updated_at, hidden_at
                  FROM orchestrator_contexts_current;
                DROP TABLE orchestrator_contexts_current;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = Store(temp.Path, clock);
        await upgraded.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={upgraded.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var column = connection.CreateCommand();
            column.CommandText = """
                SELECT count(*) FROM pragma_table_info('orchestrator_contexts')
                 WHERE name IN ('workbench_key', 'archived_at');
                """;
            Assert.Equal(2L, (long)(await column.ExecuteScalarAsync())!);
        }
        var context = Assert.Single(await upgraded.ListOrchestratorContextsAsync(false, default));
        Assert.Null(context.ArchivedAt);
        var archived = await upgraded.SetOrchestratorContextArchivedAsync(
            context.ContextKey, true, "operator", default);
        Assert.NotNull(archived.ArchivedAt);
        Assert.Equal(project.ProjectId, archived.ProjectId);
        await upgraded.AppendOrchestratorContextTurnAsync(
            project.ProjectId,
            null,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "migrated_user", clock.GetUtcNow().UtcDateTime, "user", "After the upgrade")),
            "test",
            default);
        Assert.Null(Assert.Single(await upgraded.ListOrchestratorContextsAsync(false, default)).ArchivedAt);
    }

    [Fact]
    public async Task Http_archive_state_and_studio_archive_routes_set_the_marker_and_publish_an_event()
    {
        using var temp = new TempDirectory();
        await using var factory = new TaskServerFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var workspaceResponse = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest("Workspace"));
        workspaceResponse.EnsureSuccessStatusCode();
        var workspace = await workspaceResponse.Content.ReadFromJsonAsync<WorkspaceDto>();
        var projectResponse = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new CreateProjectRequest(workspace!.WorkspaceId, "Agent Studio", "AGT"));
        projectResponse.EnsureSuccessStatusCode();

        var archiveResponse = await client.PostAsJsonAsync(
            "/api/v1/orchestrator-contexts/archive-state",
            new SetOrchestratorContextArchivedRequest("project:Agent Studio", true));
        archiveResponse.EnsureSuccessStatusCode();
        using (var document = JsonDocument.Parse(await archiveResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("project:Agent Studio", document.RootElement.GetProperty("contextKey").GetString());
            Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("archivedAt").ValueKind);
        }
        var contexts = await client.GetFromJsonAsync<OrchestratorContextListResponse>(
            "/api/v1/orchestrator-contexts");
        Assert.NotNull(Assert.Single(contexts!.Contexts).ArchivedAt);

        var missing = await client.PostAsJsonAsync(
            "/api/v1/orchestrator-contexts/archive-state",
            new SetOrchestratorContextArchivedRequest("project:Nope", true));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var cursor = (await store.ListStudioStreamEventsSinceAsync(0, default)).Select(item => item.Cursor).DefaultIfEmpty(0).Max();
        var studioResponse = await client.PostAsJsonAsync(
            "/api/v1/studio/orchestrator/sessions/archive",
            new { contextKey = "project:Agent Studio", archived = false });
        studioResponse.EnsureSuccessStatusCode();
        var session = await studioResponse.Content.ReadFromJsonAsync<OrchestratorSessionDto>();
        Assert.Equal("project:Agent Studio", session!.ContextKey);
        Assert.Null(session.ArchivedAt);
        var published = Assert.Single(
            await store.ListStudioStreamEventsSinceAsync(cursor, default),
            item => item.Kind == StudioStreamEventKinds.OrchestratorContextArchiveChanged);
        Assert.Contains("project:Agent Studio", published.PayloadJson);

        var sessions = await client.GetFromJsonAsync<OrchestratorSessionListResponse>(
            "/api/v1/studio/orchestrator/sessions");
        Assert.Null(Assert.Single(sessions!.Sessions).ArchivedAt);
    }

    private static TaskServerStore Store(string dataDirectory, TimeProvider? clock = null)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            clock ?? TimeProvider.System);

    private sealed class TaskServerFactory(string dataDirectory)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                }));
        }
    }
}
