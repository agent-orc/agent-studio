using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioEndpointsTests
{
    [Fact]
    public async Task Bootstrap_login_change_password_and_logout_form_the_studio_auth_lifecycle()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);

        var initialStatus = await client.GetFromJsonAsync<StudioAuthStatusDto>("/api/v1/studio/auth/status");
        Assert.True(initialStatus!.BootstrapRequired);
        Assert.False(initialStatus.Authenticated);

        var bootstrap = await client.PostAsJsonAsync(
            "/api/v1/studio/auth/bootstrap", new StudioBootstrapRequest("owner", "correct horse battery staple"));
        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        var session = await bootstrap.Content.ReadFromJsonAsync<StudioAuthSessionDto>();
        Assert.NotNull(session);
        Assert.Equal(StudioUserRoles.Owner, session!.Status.User!.Role);
        Assert.True(session.Status.Authenticated);

        var secondBootstrap = await client.PostAsJsonAsync(
            "/api/v1/studio/auth/bootstrap", new StudioBootstrapRequest("owner2", "another long enough password"));
        Assert.Equal(HttpStatusCode.Conflict, secondBootstrap.StatusCode);
        Assert.Equal("studio-already-bootstrapped", (await secondBootstrap.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var badLogin = await client.PostAsJsonAsync(
            "/api/v1/studio/auth/login", new StudioLoginRequest("owner", "wrong password"));
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
        Assert.Equal("invalid-credentials", (await badLogin.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var login = await client.PostAsJsonAsync(
            "/api/v1/studio/auth/login", new StudioLoginRequest("owner", "correct horse battery staple"));
        login.EnsureSuccessStatusCode();
        var loginSession = (await login.Content.ReadFromJsonAsync<StudioAuthSessionDto>())!;

        using var authed = Client(factory);
        authed.DefaultRequestHeaders.Add("X-Studio-Session-Token", loginSession.SessionToken);
        var statusWithSession = await authed.GetFromJsonAsync<StudioAuthStatusDto>("/api/v1/studio/auth/status");
        Assert.True(statusWithSession!.Authenticated);
        Assert.Equal("owner", statusWithSession.User!.Username);

        var changePassword = await authed.PostAsJsonAsync(
            "/api/v1/studio/auth/change-password",
            new StudioChangePasswordRequest("correct horse battery staple", "brand new password 2"));
        changePassword.EnsureSuccessStatusCode();
        Assert.False((await changePassword.Content.ReadFromJsonAsync<StudioAuthUserDto>())!.MustChangePassword);

        var loginWithOldPassword = await client.PostAsJsonAsync(
            "/api/v1/studio/auth/login", new StudioLoginRequest("owner", "correct horse battery staple"));
        Assert.Equal(HttpStatusCode.Unauthorized, loginWithOldPassword.StatusCode);

        var logout = await authed.PostAsync("/api/v1/studio/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var statusAfterLogout = await authed.GetFromJsonAsync<StudioAuthStatusDto>("/api/v1/studio/auth/status");
        Assert.False(statusAfterLogout!.Authenticated);
    }

    [Fact]
    public async Task Board_reflects_lane_grouped_tasks_across_projects()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Board WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Board Project", "BRD"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Backlog task"), "test", default);
        await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Ready task", State: "2-ready"), "test", default);

        var board = await client.GetFromJsonAsync<StudioBoardResponse>("/api/v1/studio/board");

        Assert.Contains(board!.Backlog, task => task.Title == "Backlog task");
        Assert.Contains(board.Ready, task => task.Title == "Ready task");
    }

    [Fact]
    public async Task Task_lifecycle_covers_start_move_move_to_top_continue_stop_and_delete()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Lifecycle WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Lifecycle Project", "LFC"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Lifecycle task"), "test", default);
        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";

        var start = await client.PostAsJsonAsync($"{basePath}/start", new StartTaskRequest());
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TaskLifecycleResponse>())!;
        Assert.Equal("2-ready", started.Task.State);

        var notStartable = await client.PostAsJsonAsync($"{basePath}/start", new StartTaskRequest());
        // Idempotent: task is now "2-ready", not "3-progress", so start is not rejected outright but stays put.
        notStartable.EnsureSuccessStatusCode();

        var move = await client.PostAsJsonAsync(
            $"{basePath}/move", new MoveTaskRequest("4-auto-review"));
        move.EnsureSuccessStatusCode();
        var moved = (await move.Content.ReadFromJsonAsync<MoveTaskResponse>())!;
        Assert.Equal("4-auto-review", moved.Task.State);

        var moveBack = await client.PutAsJsonAsync($"{basePath}/state", new MoveTaskRequest("2-ready"));
        moveBack.EnsureSuccessStatusCode();

        var second = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Second ready task", State: "2-ready"), "test", default);
        var secondPath = $"/api/v1/projects/{project.ProjectId}/tasks/{second.TaskId}";
        var moveToTop = await client.PostAsync($"{secondPath}/move-to-top", null);
        moveToTop.EnsureSuccessStatusCode();
        Assert.Equal(1, (await moveToTop.Content.ReadFromJsonAsync<MoveTaskResponse>())!.Position);

        var continueResponse = await client.PostAsJsonAsync(
            $"{basePath}/continue", new ContinueTaskRequest("Please keep going"));
        continueResponse.EnsureSuccessStatusCode();
        Assert.Equal("2-ready", (await continueResponse.Content.ReadFromJsonAsync<TaskLifecycleResponse>())!.Task.State);

        var chatAfterContinue = await store.ReadOrchestratorContextAsync(project.ProjectId, task.TaskId, 10, "test", default);
        Assert.Contains(chatAfterContinue.Turns, turn => turn.Body == "Please keep going");

        var stopWithoutRun = await client.PostAsync($"{basePath}/stop", null);
        Assert.Equal(HttpStatusCode.NotFound, stopWithoutRun.StatusCode);

        await store.RegisterRunnerAsync(
            "runner-lifecycle",
            new RegisterRunnerRequest(
                "runner", "host-lifecycle", "instance-lifecycle", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test",
            default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-lifecycle", "instance-lifecycle"), "test", default);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(task.TaskId, claim.Task!.TaskId);

        var stop = await client.PostAsJsonAsync($"{basePath}/stop", new StopTaskRequest("user"));
        stop.EnsureSuccessStatusCode();
        var stopped = (await stop.Content.ReadFromJsonAsync<TaskLifecycleResponse>())!;
        Assert.Equal("stop-requested", stopped.Run!.Status);

        var deleteWithHistory = await client.DeleteAsync(basePath);
        Assert.Equal(HttpStatusCode.Conflict, deleteWithHistory.StatusCode);
        Assert.Equal("task-has-history", (await deleteWithHistory.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var freshTask = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Deletable"), "test", default);
        var deleteFresh = await client.DeleteAsync($"/api/v1/projects/{project.ProjectId}/tasks/{freshTask.TaskId}");
        deleteFresh.EnsureSuccessStatusCode();
        Assert.Null(await store.GetTaskAsync(project.ProjectId, freshTask.TaskId, default));
    }

    [Fact]
    public async Task Task_lifecycle_resolves_by_task_id_alone_for_the_connector_unscoped_project_token()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Unscoped WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Unscoped Project", "UNS"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Unscoped task"), "test", default);
        // The connector substitutes this literal for {projectId} when a legacy
        // single-parameter frontend path (e.g. /api/tasks/{taskId}/move) has no
        // project id to forward. Confirms the moved core-attach routes still
        // resolve the task when addressed this way.
        var basePath = $"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{task.TaskId}";

        var start = await client.PostAsJsonAsync($"{basePath}/start", new StartTaskRequest());
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TaskLifecycleResponse>())!;
        Assert.Equal(project.ProjectId, started.Task.ProjectId);
        Assert.Equal("2-ready", started.Task.State);

        var move = await client.PostAsJsonAsync($"{basePath}/move", new MoveTaskRequest("4-auto-review"));
        move.EnsureSuccessStatusCode();
        Assert.Equal("4-auto-review", (await move.Content.ReadFromJsonAsync<MoveTaskResponse>())!.Task.State);

        var scoped = await store.GetTaskAsync(project.ProjectId, task.TaskId, default);
        Assert.Equal("4-auto-review", scoped!.State);
    }

    [Fact]
    public async Task Orchestrator_chat_turns_and_attachments_round_trip()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Chat WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Chat Project", "CHT"), "test", default);

        var emptyTranscript = await client.GetFromJsonAsync<OrchestratorChatTranscriptResponse>(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-chat");
        Assert.Empty(emptyTranscript!.Turns);

        var send = await client.PostAsJsonAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-chat",
            new StudioOrchestratorChatMessageRequest("Hello orchestrator"));
        send.EnsureSuccessStatusCode();
        var reply = (await send.Content.ReadFromJsonAsync<OrchestratorChatResponse>())!;
        Assert.Single(reply.Turns);
        Assert.Equal("Hello orchestrator", reply.Turn.Body);

        using var upload = new MultipartFormDataContent();
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        upload.Add(fileContent, "file", "diagram.png");
        var uploadResponse = await client.PostAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-chat/attachments", upload);
        uploadResponse.EnsureSuccessStatusCode();
        var uploaded = (await uploadResponse.Content.ReadFromJsonAsync<ChatAttachmentUploadResponse>())!;
        Assert.EndsWith(".png", uploaded.FileName);

        var download = await client.GetAsync(uploaded.Url);
        download.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());

        var missingProjectUpload = await client.PostAsync(
            "/api/v1/studio/runner/missing-project/orchestrator-chat/attachments", upload);
        Assert.Equal(HttpStatusCode.NotFound, missingProjectUpload.StatusCode);
    }

    [Fact]
    public async Task Orchestrator_context_digest_and_sessions_reflect_project_activity()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Digest WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Digest Project", "DIG"), "test", default);
        await store.SendOrchestratorChatMessageAsync(
            project.ProjectId, new StudioOrchestratorChatMessageRequest("What is the plan?"), "test", default);

        var digest = await client.GetFromJsonAsync<StudioOrchestratorContextDigestResponse>(
            $"/api/v1/studio/orchestrator/context/project:{project.ProjectId}");
        Assert.Contains("Digest Project", digest!.Digest);
        Assert.Contains("What is the plan?", digest.Digest);

        var refreshed = await client.PostAsync(
            $"/api/v1/studio/orchestrator/context/project:{project.ProjectId}/refresh", null);
        refreshed.EnsureSuccessStatusCode();

        var global = await client.GetFromJsonAsync<StudioOrchestratorContextDigestResponse>(
            "/api/v1/studio/orchestrator/context/global");
        Assert.Equal("global", global!.ContextKey);

        var sessions = await client.GetFromJsonAsync<OrchestratorSessionListResponse>("/api/v1/studio/orchestrator/sessions");
        Assert.Contains(sessions!.Sessions, session => session.ProjectId == project.ProjectId);
    }

    [Fact]
    public async Task Runner_status_reports_active_work_grouped_by_project()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Runner WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Runner Project", "RUN"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Runner task", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync(
            "runner-status",
            new RegisterRunnerRequest(
                "runner", "host-status", "instance-status", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor, "permits", "local-queue", "host-post-processing"],
                HostOrchestratorContract.MinimumSupported,
                HostOrchestratorContract.MaximumSupported),
            "test",
            default);
        var report = new HostReportRequest(
            HostOrchestratorContract.Current, "host-status", "instance-status", 1, DateTime.UtcNow,
            new HostCapacityDto(1, 1, 0, 0, 1),
            [new HostCapabilityDto("git-push", "ready", ObservedAt: DateTime.UtcNow)],
            [], [], []);
        var available = await store.AcceptHostReportAsync("runner-status", report, "runner-status", default);
        var permit = Assert.Single(available.AvailableWork);
        var acceptance = await store.AcceptWorkPermitAsync(
            permit.PermitId,
            new WorkPermitAcceptRequest(
                HostOrchestratorContract.Current, "host-status", "instance-status", "runner-status",
                available.AcceptedSequence, available.PolicyVersion, "accept-once"),
            "runner-status",
            default);
        var runningReport = report with
        {
            Sequence = 2,
            Work =
            [
                new HostWorkStatusDto(
                    acceptance.PermitId, acceptance.Task.TaskId, acceptance.Task.TaskKey, acceptance.Run.RunId,
                    acceptance.Lease.LeaseId, acceptance.Lease.Fence, "running", null, 4242,
                    acceptance.Lease.AcquiredAt, DateTime.UtcNow),
            ],
        };
        await store.AcceptHostReportAsync("runner-status", runningReport, "runner-status", default);

        var status = await client.GetFromJsonAsync<StudioRunnerStatusResponse>("/api/v1/studio/runner/status");

        var projectStatus = Assert.Single(status!.Projects, p => p.ProjectId == project.ProjectId);
        var activeRun = Assert.Single(projectStatus.ActiveRuns);
        Assert.Equal(acceptance.Task.TaskId, activeRun.TaskId);
        Assert.Equal("running", activeRun.Phase);
    }

    [Fact]
    public async Task Studio_stream_replays_events_after_a_cursor_for_hub_reconnect()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        var store = factory.Services.GetRequiredService<TaskServerStore>();

        var first = await store.AppendStudioStreamEventAsync(
            StudioStreamEventKinds.TaskCreated, "prj_a", "tsk_a", new { note = "first" }, default);
        var second = await store.AppendStudioStreamEventAsync(
            StudioStreamEventKinds.TaskMoved, "prj_a", "tsk_a", new { note = "second" }, default);
        var third = await store.AppendStudioStreamEventAsync(
            StudioStreamEventKinds.TaskStarted, "prj_a", "tsk_a", new { note = "third" }, default);

        var replay = await store.ListStudioStreamEventsSinceAsync(first.Cursor, default);

        Assert.Equal([second.Cursor, third.Cursor], replay.Select(item => item.Cursor));
        Assert.Empty(await store.ListStudioStreamEventsSinceAsync(third.Cursor, default));

        using var client = Client(factory);
        var negotiate = await client.PostAsync("/hubs/v1/studio/negotiate?negotiateVersion=1", null);
        negotiate.EnsureSuccessStatusCode();
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-api-test");
        return client;
    }

    private sealed class StudioApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
