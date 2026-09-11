using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;
using static AgentStudio.TestSupport.BuiltProcessLauncher;
using static AgentStudio.TestSupport.ProcessWaiters;

namespace TaskServer.Tests;

/// <summary>
/// Proves the P0 "core-attach" Studio bundle (login/session bootstrap, board
/// load, task create/move/start/continue, orchestrator chat, and resumable
/// live updates) against a real Task Server process reached over a real
/// loopback TCP connection - the same out-of-process pattern
/// <see cref="TopologyTests"/> uses for the rest of the v1 API. A
/// WebApplicationFactory in-process test proves the handler logic; this proves
/// the routes are actually reachable remotely and that a Studio client can
/// detach and reattach without losing state, which is the release gate named
/// in the studio-route-ownership concept dossier.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class StudioCoreAttachTopologyTests
{
    [Fact(Timeout = 60000)]
    public async Task Core_attach_bundle_bootstraps_mutates_and_resumes_live_updates_through_a_real_remote_process()
    {
        var root = ProtocolTests.RepositoryRoot();
        using var data = new TempDirectory();
        var serverUrl = $"http://127.0.0.1:{FreePort()}";

        using var server = StartBuilt(
            root,
            "task-server",
            "task-server.dll",
            "--urls", serverUrl,
            "--TaskServer:DataDirectory", data.Path);
        await WaitForHttpAsync(serverUrl + "/readyz", server);

        using var client = ProtocolClient(serverUrl);

        // 1. Login/session bootstrap.
        var initialStatus = await client.GetFromJsonAsync<StudioAuthStatusDto>("/api/v1/studio/auth/status");
        Assert.True(initialStatus!.BootstrapRequired);
        var bootstrap = await client.PostAsJsonAsync(
            "/api/v1/studio/auth/bootstrap",
            new StudioBootstrapRequest("remote-owner", "correct horse battery staple over the wire"));
        bootstrap.EnsureSuccessStatusCode();
        var session = (await bootstrap.Content.ReadFromJsonAsync<StudioAuthSessionDto>())!;
        Assert.True(session.Status.Authenticated);
        client.DefaultRequestHeaders.Add("X-Studio-Session-Token", session.SessionToken);

        var login = await client.PostAsJsonAsync(
            "/api/v1/studio/auth/login", new StudioLoginRequest("remote-owner", "correct horse battery staple over the wire"));
        login.EnsureSuccessStatusCode();

        // 2. Board load, empty before any task exists.
        var emptyBoard = await client.GetFromJsonAsync<StudioBoardResponse>("/api/v1/studio/board");
        Assert.Empty(emptyBoard!.Backlog);

        // 3. Workspace, project, and task creation through the standalone v1 API.
        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            client, "/api/v1/workspaces", new CreateWorkspaceRequest("Remote core-attach workspace"));
        var project = await PostAsync<CreateProjectRequest, ProjectDto>(
            client, "/api/v1/projects", new CreateProjectRequest(workspace.WorkspaceId, "Core Attach Project", "COR"));
        var task = await PostAsync<CreateTaskRequest, TaskDto>(
            client,
            $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest("Remote core-attach proof"));

        var boardWithTask = await client.GetFromJsonAsync<StudioBoardResponse>("/api/v1/studio/board");
        Assert.Contains(boardWithTask!.Backlog, item => item.TaskId == task.TaskId);

        // 4. Task create/move/start/continue while no Studio hub is attached at
        //    all - this is "Studio-detached execution": mutations and their
        //    durable stream events happen independent of a live Studio
        //    connection. Addressed by task id alone through the
        //    connector's unscoped-project compatibility path.
        var basePath = $"/api/v1/projects/{TaskServerStore.UnscopedProjectToken}/tasks/{task.TaskId}";
        var start = await client.PostAsJsonAsync($"{basePath}/start", new StartTaskRequest());
        start.EnsureSuccessStatusCode();
        Assert.Equal("2-ready", (await start.Content.ReadFromJsonAsync<TaskLifecycleResponse>())!.Task.State);

        var move = await client.PostAsJsonAsync($"{basePath}/move", new MoveTaskRequest("4-auto-review"));
        move.EnsureSuccessStatusCode();
        Assert.Equal("4-auto-review", (await move.Content.ReadFromJsonAsync<MoveTaskResponse>())!.Task.State);

        var continueDetached = await client.PostAsJsonAsync(
            $"{basePath}/continue", new ContinueTaskRequest("First remote continuation while detached"));
        continueDetached.EnsureSuccessStatusCode();

        // 5. Orchestrator chat, also while detached.
        var chat = await client.PostAsJsonAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-chat",
            new StudioOrchestratorChatMessageRequest("Status update generated with no Studio hub attached"));
        chat.EnsureSuccessStatusCode();

        var digest = await client.GetFromJsonAsync<StudioOrchestratorContextDigestResponse>(
            $"/api/v1/studio/orchestrator/context/project:{project.ProjectId}");
        Assert.Contains("Status update generated with no Studio hub attached", digest!.Digest);

        var sessions = await client.GetFromJsonAsync<OrchestratorSessionListResponse>("/api/v1/studio/orchestrator/sessions");
        Assert.Contains(sessions!.Sessions, item => item.ProjectId == project.ProjectId);

        var runnerStatus = await client.GetFromJsonAsync<StudioRunnerStatusResponse>("/api/v1/studio/runner/status");
        Assert.NotNull(runnerStatus);

        // 6. Reconnect from a cursor: a fresh Studio hub connection asking for
        //    everything after cursor 0 must replay every event generated
        //    above, in order, before delivering anything live. This proves
        //    resumable live updates survive a full Studio-detached period.
        await using (var replay = await StudioHubSocket.ConnectAsync(serverUrl, cursor: 0, CancellationToken.None))
        {
            var replayed = await replay.ReadStudioEventsAsync(4, TimeSpan.FromSeconds(15));
            Assert.Equal(
                [
                    StudioStreamEventKinds.TaskStarted,
                    StudioStreamEventKinds.TaskMoved,
                    StudioStreamEventKinds.TaskContinued,
                    StudioStreamEventKinds.OrchestratorChatAppended,
                ],
                replayed.Select(item => item.Kind));
            Assert.Equal(project.ProjectId, replayed[0].ProjectId);
            Assert.Equal(project.ProjectId, replayed[1].ProjectId);
            Assert.Equal(project.ProjectId, replayed[2].ProjectId);
            Assert.Equal(project.ProjectId, replayed[3].ProjectId);
            var lastCursorSeen = replayed[^1].Cursor;

            // 7. While still connected, a new mutation must arrive live over the
            //    same connection (not just on the next reconnect).
            var continueLive = await client.PostAsJsonAsync(
                $"{basePath}/continue", new ContinueTaskRequest("Second remote continuation while attached"));
            continueLive.EnsureSuccessStatusCode();
            var live = await replay.ReadStudioEventsAsync(1, TimeSpan.FromSeconds(15));
            var liveEvent = Assert.Single(live);
            Assert.Equal(StudioStreamEventKinds.TaskContinued, liveEvent.Kind);
            Assert.True(liveEvent.Cursor > lastCursorSeen);
            lastCursorSeen = liveEvent.Cursor;

            // 7b. A Runner claims the ready task and the Studio stop route
            //     requests the resulting run's cancellation - the remaining
            //     core-attach lifecycle verb not yet covered above - and that
            //     too arrives live over the same connection.
            var register = await client.PutAsJsonAsync(
                "/api/v1/runners/runner-core-attach",
                new RegisterRunnerRequest(
                    "runner", "host-core-attach", "instance-core-attach", "1.0.0", TaskServerProtocol.Current,
                    [ReviewCapabilities.CodingExecutor]));
            register.EnsureSuccessStatusCode();
            var claim = await client.PostAsJsonAsync(
                "/api/v1/runners/runner-core-attach/claims",
                new ClaimRequest("runner-core-attach", "instance-core-attach"));
            claim.EnsureSuccessStatusCode();
            var claimed = (await claim.Content.ReadFromJsonAsync<ClaimResponse>())!;
            Assert.Equal("claimed", claimed.Status);

            var stop = await client.PostAsJsonAsync($"{basePath}/stop", new StopTaskRequest("remote proof"));
            stop.EnsureSuccessStatusCode();
            Assert.Equal("stop-requested", (await stop.Content.ReadFromJsonAsync<TaskLifecycleResponse>())!.Run!.Status);
            var stopLive = await replay.ReadStudioEventsAsync(1, TimeSpan.FromSeconds(15));
            var stopEvent = Assert.Single(stopLive);
            Assert.Equal(StudioStreamEventKinds.TaskStopped, stopEvent.Kind);
            Assert.True(stopEvent.Cursor > lastCursorSeen);
            lastCursorSeen = stopEvent.Cursor;

            // 8. Detach again (dispose this connection), mutate with no hub
            //    attached, then reconnect from the last cursor this client
            //    actually saw. Only the new event must replay - not a repeat
            //    of everything already observed.
            await replay.DisposeAsync();

            var chatWhileDetachedAgain = await client.PostAsJsonAsync(
                $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-chat",
                new StudioOrchestratorChatMessageRequest("Second status update while detached again"));
            chatWhileDetachedAgain.EnsureSuccessStatusCode();

            await using var resumed = await StudioHubSocket.ConnectAsync(serverUrl, cursor: lastCursorSeen, CancellationToken.None);
            var resumedReplay = await resumed.ReadStudioEventsAsync(1, TimeSpan.FromSeconds(15));
            var resumedEvent = Assert.Single(resumedReplay);
            Assert.Equal(StudioStreamEventKinds.OrchestratorChatAppended, resumedEvent.Kind);
            Assert.True(resumedEvent.Cursor > lastCursorSeen);
        }
    }

    private static HttpClient ProtocolClient(string baseUrl)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-core-attach-remote-proof");
        return client;
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client, string path, TRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {detail}");
        return JsonSerializer.Deserialize<TResponse>(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>
    /// Minimal hand-rolled SignalR JSON-protocol client over a raw
    /// <see cref="ClientWebSocket"/>, scoped to exactly what this test needs:
    /// negotiate, connect with an optional replay cursor, and read
    /// <c>studioEvent</c> invocations in order. Adding the full SignalR
    /// client package for one test file was judged worse than 80 lines of
    /// explicit framing against a protocol this repository already documents
    /// (see TaskServerStudioHub.cs) and pins with an in-process negotiate test.
    /// </summary>
    private sealed class StudioHubSocket(ClientWebSocket socket) : IAsyncDisposable
    {
        private const byte RecordSeparator = 0x1e;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private string _pending = "";

        public static async Task<StudioHubSocket> ConnectAsync(string serverUrl, long? cursor, CancellationToken ct)
        {
            using var negotiateClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
            negotiateClient.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            negotiateClient.DefaultRequestHeaders.Add("X-Client-Id", "studio-core-attach-remote-proof");
            var negotiateResponse = await negotiateClient.PostAsync("/hubs/v1/studio/negotiate?negotiateVersion=1", null, ct);
            negotiateResponse.EnsureSuccessStatusCode();
            var negotiated = await negotiateResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var connectionToken = negotiated.TryGetProperty("connectionToken", out var tokenProperty)
                ? tokenProperty.GetString()
                : negotiated.GetProperty("connectionId").GetString();

            var wsUri = new UriBuilder(serverUrl) { Scheme = "ws", Path = "/hubs/v1/studio" };
            wsUri.Query = cursor is { } value
                ? $"id={Uri.EscapeDataString(connectionToken!)}&cursor={value}"
                : $"id={Uri.EscapeDataString(connectionToken!)}";

            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            socket.Options.SetRequestHeader("X-Client-Id", "studio-core-attach-remote-proof");
            await socket.ConnectAsync(wsUri.Uri, ct);

            var hub = new StudioHubSocket(socket);
            await hub.SendAsync("""{"protocol":"json","version":1}""", ct);
            await hub.ReadRecordAsync(TimeSpan.FromSeconds(10)); // handshake response
            return hub;
        }

        public async Task<IReadOnlyList<StudioStreamEventDto>> ReadStudioEventsAsync(int count, TimeSpan timeout)
        {
            var events = new List<StudioStreamEventDto>();
            while (events.Count < count)
            {
                var record = await ReadRecordAsync(timeout);
                if (!record.TryGetProperty("type", out var typeProperty) || typeProperty.GetInt32() != 1) continue;
                if (record.GetProperty("target").GetString() != "studioEvent") continue;
                var payload = record.GetProperty("arguments")[0];
                events.Add(payload.Deserialize<StudioStreamEventDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            }
            return events;
        }

        private async Task SendAsync(string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json + (char)RecordSeparator);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }

        private async Task<JsonElement> ReadRecordAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var separatorIndex = _pending.IndexOf((char)RecordSeparator);
                if (separatorIndex >= 0)
                {
                    var record = _pending[..separatorIndex];
                    _pending = _pending[(separatorIndex + 1)..];
                    return JsonSerializer.Deserialize<JsonElement>(record);
                }

                var result = await socket.ReceiveAsync(_buffer, cts.Token);
                _pending += Encoding.UTF8.GetString(_buffer, 0, result.Count);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
            }
            catch (Exception)
            {
                // The server may already be tearing this connection down; a
                // close-race here carries no information the test needs.
            }
            socket.Dispose();
        }
    }
}
