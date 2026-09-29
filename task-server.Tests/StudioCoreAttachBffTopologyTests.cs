using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;
using static AgentStudio.TestSupport.BuiltProcessLauncher;
using static AgentStudio.TestSupport.ProcessWaiters;

namespace TaskServer.Tests;

/// <summary>
/// AGT-2983: drives every one of the 27 core-attach operations on the exact
/// versioned path Angular now calls, through a real Studio BFF process in
/// front of a real Task Server process. <see cref="StudioCoreAttachTopologyTests"/>
/// proves the Task Server side directly; this proves the Studio-facing hop
/// reaches the same owner for the switched operations, including the task
/// routes addressed by project name and by the unscoped "-" token, the
/// workbench turn, and the <c>/hubs/v1/studio</c> negotiate.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class StudioCoreAttachBffTopologyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact(Timeout = 90000)]
    public async Task Every_core_attach_operation_reaches_the_task_server_through_the_studio_bff()
    {
        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        var serverUrl = $"http://127.0.0.1:{FreePort()}";
        var studioUrl = $"http://127.0.0.1:{FreePort()}";

        using var server = StartBuilt(
            root, "task-server", "task-server.dll",
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path);
        await WaitForHttpAsync(serverUrl + "/readyz", server);
        using var studioBff = StartBuilt(
            root, "studio-bff", "agent-studio-bff.dll",
            "--urls", studioUrl,
            "--TaskServer:BaseUrl", serverUrl);
        await WaitForHttpAsync(studioUrl + "/healthz", studioBff);

        using var studio = new HttpClient { BaseAddress = new Uri(studioUrl), Timeout = TimeSpan.FromSeconds(10) };
        studio.DefaultRequestHeaders.Add("X-Client-Id", "studio-core-attach-bff");

        // Auth x5.
        var status = await ReadAsync<StudioAuthStatusDto>(await studio.GetAsync("/api/v1/studio/auth/status"));
        Assert.True(status.BootstrapRequired);
        const string password = "correct horse battery staple through the bff";
        var bootstrap = await studio.PostAsJsonAsync("/api/v1/studio/auth/bootstrap", new StudioBootstrapRequest("bff-owner", password));
        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        var login = await ReadAsync<StudioAuthSessionDto>(
            await studio.PostAsJsonAsync("/api/v1/studio/auth/login", new StudioLoginRequest("bff-owner", password)));
        Assert.True(login.Status.Authenticated);
        // The BFF does not relay the nested human session (header or cookie);
        // that propagation belongs to the connector security work. It must
        // not keep the login's session cookie for itself either: the pooled
        // upstream handler is shared by every Studio caller. The route is
        // still owned and answered by the Task Server itself.
        var changePassword = await studio.PostAsJsonAsync(
            "/api/v1/studio/auth/change-password", new StudioChangePasswordRequest(password, password + " rotated"));
        Assert.Equal(HttpStatusCode.Unauthorized, changePassword.StatusCode);
        Assert.Equal("authentication-required", (await ReadAsync<ApiError>(changePassword)).Code);
        var logout = await studio.PostAsync("/api/v1/studio/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
        Assert.Equal("authentication-required", (await ReadAsync<ApiError>(logout)).Code);

        // Workspaces and projects x4.
        var workspace = await ReadAsync<WorkspaceDto>(
            await studio.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest("BFF workspace")));
        Assert.Contains(await ReadAsync<List<WorkspaceDto>>(await studio.GetAsync("/api/v1/workspaces")),
            item => item.WorkspaceId == workspace.WorkspaceId);
        var project = await ReadAsync<ProjectDto>(
            await studio.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(workspace.WorkspaceId, "BFF Project", "BFF")));
        Assert.Contains(await ReadAsync<List<ProjectDto>>(await studio.GetAsync("/api/v1/projects")),
            item => item.ProjectId == project.ProjectId);

        var task = await ReadAsync<TaskDto>(await studio.PostAsJsonAsync(
            $"/api/v1/projects/{project.ProjectId}/tasks", new CreateTaskRequest("BFF core-attach task")));
        var spare = await ReadAsync<TaskDto>(await studio.PostAsJsonAsync(
            $"/api/v1/projects/{project.ProjectId}/tasks", new CreateTaskRequest("BFF deletable task")));

        // Board, and the single-task detail read by project name and by "-".
        var board = await ReadAsync<StudioBoardResponse>(await studio.GetAsync("/api/v1/studio/board"));
        var boardTask = Assert.Single(board.Backlog, item => item.TaskId == task.TaskId);
        var projectSegment = Uri.EscapeDataString(project.Name);
        Assert.Equal(boardTask, await ReadAsync<TaskDto>(
            await studio.GetAsync($"/api/v1/projects/{projectSegment}/tasks/{task.TaskKey}?project={projectSegment}")));
        Assert.Equal(boardTask, await ReadAsync<TaskDto>(
            await studio.GetAsync($"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{task.TaskId}?watchPath=ignored")));

        // Task lifecycle x7, addressed the way the Studio sends a task-only call.
        var taskPath = $"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{task.TaskId}";
        Assert.Equal("2-ready", (await ReadAsync<TaskLifecycleResponse>(
            await studio.PostAsJsonAsync($"{taskPath}/start", new StartTaskRequest()))).Task.State);
        Assert.Equal(1, (await ReadAsync<MoveTaskResponse>(await studio.PostAsync($"{taskPath}/move-to-top", null))).Position);
        Assert.Equal("4-auto-review", (await ReadAsync<MoveTaskResponse>(
            await studio.PostAsJsonAsync($"{taskPath}/move", new MoveTaskRequest("4-auto-review")))).Task.State);
        Assert.Equal("2-ready", (await ReadAsync<MoveTaskResponse>(
            await studio.PutAsJsonAsync($"{taskPath}/state", new MoveTaskRequest("2-ready")))).Task.State);
        await ReadAsync<TaskLifecycleResponse>(
            await studio.PostAsJsonAsync($"{taskPath}/continue", new ContinueTaskRequest("Continue through the BFF")));
        var register = await studio.PutAsJsonAsync(
            "/api/v1/runners/runner-bff",
            new RegisterRunnerRequest(
                "runner", "host-bff", "instance-bff", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]));
        register.EnsureSuccessStatusCode();
        Assert.Equal("claimed", (await ReadAsync<ClaimResponse>(await studio.PostAsJsonAsync(
            "/api/v1/runners/runner-bff/claims", new ClaimRequest("runner-bff", "instance-bff")))).Status);
        Assert.Equal("stop-requested", (await ReadAsync<TaskLifecycleResponse>(
            await studio.PostAsJsonAsync($"{taskPath}/stop?reason=user", new StopTaskRequest("user")))).Run!.Status);
        var delete = await studio.DeleteAsync($"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{spare.TaskId}");
        delete.EnsureSuccessStatusCode();

        // Orchestrator context, sessions, chat, attachment, and the workbench turn.
        var chat = await ReadAsync<OrchestratorChatResponse>(await studio.PostAsJsonAsync(
            $"/api/v1/studio/runner/{projectSegment}/orchestrator-chat",
            new StudioOrchestratorChatMessageRequest("Hello through the BFF")));
        Assert.Equal("Hello through the BFF", chat.Turn.Body);
        var transcript = await ReadAsync<OrchestratorChatTranscriptResponse>(
            await studio.GetAsync($"/api/v1/studio/runner/{projectSegment}/orchestrator-chat"));
        Assert.Contains(transcript.Turns, turn => turn.TurnId == chat.Turn.TurnId);
        var digest = await ReadAsync<StudioOrchestratorContextDigestResponse>(
            await studio.GetAsync($"/api/v1/studio/orchestrator/context/project:{projectSegment}"));
        Assert.Contains("Hello through the BFF", digest.Digest);
        await ReadAsync<StudioOrchestratorContextDigestResponse>(
            await studio.PostAsync($"/api/v1/studio/orchestrator/context/project:{projectSegment}/refresh", null));
        // The task shape spans two path segments; the Studio calls it as-is.
        var taskDigestPath = $"/api/v1/studio/orchestrator/context/task:{projectSegment}/{task.TaskKey}";
        var taskDigest = await ReadAsync<StudioOrchestratorContextDigestResponse>(await studio.GetAsync(taskDigestPath));
        Assert.StartsWith("task:", taskDigest.ContextKey, StringComparison.Ordinal);
        Assert.EndsWith($"/{task.TaskKey}", taskDigest.ContextKey, StringComparison.Ordinal);
        await ReadAsync<StudioOrchestratorContextDigestResponse>(await studio.PostAsync($"{taskDigestPath}/refresh", null));
        Assert.Equal("global", (await ReadAsync<StudioOrchestratorContextDigestResponse>(
            await studio.GetAsync("/api/v1/studio/orchestrator/context/global"))).ContextKey);
        var sessions = await ReadAsync<OrchestratorSessionListResponse>(await studio.GetAsync("/api/v1/studio/orchestrator/sessions"));
        Assert.Contains(sessions.Sessions, item => item.ProjectId == project.ProjectId);
        var turn = await studio.PostAsJsonAsync(
            $"/api/v1/studio/orchestrator/sessions/workbench:{projectSegment}/AGT-W1/turns",
            new StudioOrchestratorTurnRequest("Discuss through the BFF", "codex", "gpt-5"));
        Assert.Equal(HttpStatusCode.Accepted, turn.StatusCode);

        using var upload = new MultipartFormDataContent();
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        upload.Add(file, "file", "bff.png");
        var uploaded = await ReadAsync<ChatAttachmentUploadResponse>(await studio.PostAsync(
            $"/api/v1/studio/runner/{projectSegment}/orchestrator-chat/attachments", upload));
        var attachment = await studio.GetAsync(
            $"/api/v1/studio/runner/{projectSegment}/orchestrator-chat/attachments/{Uri.EscapeDataString(uploaded.FileName)}");
        attachment.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await attachment.Content.ReadAsByteArrayAsync());

        // Runner status and the live-update hub.
        var runnerStatus = await ReadAsync<StudioRunnerStatusResponse>(await studio.GetAsync("/api/v1/studio/runner/status"));
        Assert.NotNull(runnerStatus.Projects);
        var negotiate = await studio.PostAsync("/hubs/v1/studio/negotiate?negotiateVersion=1", null);
        negotiate.EnsureSuccessStatusCode();
        var negotiated = await negotiate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(negotiated.TryGetProperty("connectionToken", out _) || negotiated.TryGetProperty("connectionId", out _));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.IsSuccessStatusCode || typeof(T) == typeof(ApiError),
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} returned {(int)response.StatusCode}: {body}");
            return JsonSerializer.Deserialize<T>(body, Web)!;
        }
    }
}
