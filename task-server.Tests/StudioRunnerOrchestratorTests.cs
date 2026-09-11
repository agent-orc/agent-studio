using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioRunnerOrchestratorTests
{
    [Fact]
    public async Task ContextKey_chat_round_trips_for_global_project_and_task_shapes()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Runner WS"), "test", default);
        // The bare "global" context key resolves as an ordinary project
        // identity (chat storage always needs a project) - so a project
        // literally named "global" is what backs the "global" shape here.
        var globalProject = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "global", "GLB"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Runner Chat Project", "RCP"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Chat task"), "test", default);

        var globalSend = await client.PostAsJsonAsync(
            "/api/v1/studio/runner/global/orchestrator-chat", new StudioOrchestratorChatMessageRequest("hello global"));
        globalSend.EnsureSuccessStatusCode();
        var globalReply = (await globalSend.Content.ReadFromJsonAsync<RunnerOrchestratorChatMessageResponse>())!;
        Assert.Equal("hello global", globalReply.Turn.Body);
        Assert.Equal(globalProject.ProjectId, globalReply.Transcript.Context.ProjectId);

        var globalGet = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            "/api/v1/studio/runner/global/orchestrator-chat");
        Assert.Single(globalGet!.Turns);
        Assert.Equal("hello global", globalGet.Turns[0].Body);

        var projectSend = await client.PostAsJsonAsync(
            $"/api/v1/studio/runner/project:{project.ProjectId}/orchestrator-chat",
            new StudioOrchestratorChatMessageRequest("hello project"));
        projectSend.EnsureSuccessStatusCode();
        var projectReply = (await projectSend.Content.ReadFromJsonAsync<RunnerOrchestratorChatMessageResponse>())!;
        Assert.Equal(project.ProjectId, projectReply.Transcript.Context.ProjectId);
        Assert.Null(projectReply.Transcript.Context.TaskId);

        var projectGet = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            $"/api/v1/studio/runner/project:{project.ProjectId}/orchestrator-chat");
        Assert.Single(projectGet!.Turns);
        Assert.Equal("hello project", projectGet.Turns[0].Body);

        var taskSend = await client.PostAsJsonAsync(
            $"/api/v1/studio/runner/task:{project.ProjectId}/{task.TaskId}/orchestrator-chat",
            new StudioOrchestratorChatMessageRequest("hello task"));
        taskSend.EnsureSuccessStatusCode();
        var taskReply = (await taskSend.Content.ReadFromJsonAsync<RunnerOrchestratorChatMessageResponse>())!;
        Assert.Equal(project.ProjectId, taskReply.Transcript.Context.ProjectId);
        Assert.Equal(task.TaskId, taskReply.Transcript.Context.TaskId);

        var taskGet = await client.GetFromJsonAsync<OrchestratorContextTranscriptResponse>(
            $"/api/v1/studio/runner/task:{project.ProjectId}/{task.TaskId}/orchestrator-chat");
        Assert.Single(taskGet!.Turns);
        Assert.Equal("hello task", taskGet.Turns[0].Body);

        // Confirms the new context-key routes stay unambiguous next to P0's
        // pre-existing bare-slug route at the identical `{x}/orchestrator-chat`
        // shape: P0's bare-project route reaches its own handler (not an
        // AmbiguousMatchException) and, because both routes address the same
        // project's orchestrator context, sees the turn just posted via the
        // "project:{id}" shape.
        var bareProjectChat = await client.GetFromJsonAsync<OrchestratorChatTranscriptResponse>(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-chat");
        Assert.Single(bareProjectChat!.Turns);
        Assert.Equal("hello project", bareProjectChat.Turns[0].Body);
    }

    [Fact]
    public async Task Mode_put_is_versioned_and_start_stop_are_idempotent()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Mode WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Mode Project", "MOD"), "test", default);

        var firstMode = await client.PutAsJsonAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/mode", new UpdateRunnerModeRequest("autonomous", 0));
        firstMode.EnsureSuccessStatusCode();
        var firstState = (await firstMode.Content.ReadFromJsonAsync<RunnerProjectStateDto>())!;
        Assert.Equal("autonomous", firstState.Mode);
        Assert.Equal(1, firstState.Version);
        Assert.False(firstState.Running);

        var staleMode = await client.PutAsJsonAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/mode", new UpdateRunnerModeRequest("supervised", 0));
        Assert.Equal(HttpStatusCode.Conflict, staleMode.StatusCode);
        Assert.Equal("resource-version-mismatch", (await staleMode.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var secondMode = await client.PutAsJsonAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/mode", new UpdateRunnerModeRequest("supervised", 1));
        secondMode.EnsureSuccessStatusCode();
        Assert.Equal(2, (await secondMode.Content.ReadFromJsonAsync<RunnerProjectStateDto>())!.Version);

        var start1 = await client.PostAsync($"/api/v1/studio/runner/{project.ProjectId}/start", null);
        start1.EnsureSuccessStatusCode();
        var started1 = (await start1.Content.ReadFromJsonAsync<RunnerProjectStateDto>())!;
        Assert.True(started1.Running);
        Assert.Equal("supervised", started1.Mode);

        var start2 = await client.PostAsync($"/api/v1/studio/runner/{project.ProjectId}/start", null);
        start2.EnsureSuccessStatusCode();
        Assert.True((await start2.Content.ReadFromJsonAsync<RunnerProjectStateDto>())!.Running);

        var stop1 = await client.PostAsync($"/api/v1/studio/runner/{project.ProjectId}/stop", null);
        stop1.EnsureSuccessStatusCode();
        Assert.False((await stop1.Content.ReadFromJsonAsync<RunnerProjectStateDto>())!.Running);

        var stop2 = await client.PostAsync($"/api/v1/studio/runner/{project.ProjectId}/stop", null);
        stop2.EnsureSuccessStatusCode();
        Assert.False((await stop2.Content.ReadFromJsonAsync<RunnerProjectStateDto>())!.Running);
    }

    [Fact]
    public async Task Orchestrator_log_lists_entries_and_accepts_a_human_override()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Log WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Log Project", "LOG"), "test", default);

        var emptyLog = await client.GetFromJsonAsync<OrchestratorLogResponse>(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-log");
        Assert.Empty(emptyLog!.Entries);

        var overridePayload = new { note = "operator stepped in", severity = "info" };
        var overrideResponse = await client.PostAsJsonAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-log/override",
            new { entry = overridePayload });
        Assert.Equal(HttpStatusCode.Created, overrideResponse.StatusCode);
        var overrideEntry = (await overrideResponse.Content.ReadFromJsonAsync<OrchestratorLogEntryDto>())!;
        Assert.True(overrideEntry.IsOverride);
        Assert.Equal("operator stepped in", overrideEntry.Entry.GetProperty("note").GetString());

        var log = await client.GetFromJsonAsync<OrchestratorLogResponse>(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-log");
        var entry = Assert.Single(log!.Entries);
        Assert.True(entry.IsOverride);
        Assert.Equal("operator stepped in", entry.Entry.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Pending_decisions_inserted_directly_round_trip_over_http()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Decisions WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Decisions Project", "DEC"), "test", default);

        var emptyList = await client.GetFromJsonAsync<PendingDecisionListResponse>(
            $"/api/v1/studio/runner/{project.ProjectId}/pending-decisions");
        Assert.Empty(emptyList!.Decisions);

        var created = await store.CreateStudioPendingDecisionAsync(
            project.ProjectId, "Pick a review policy", "test", default);
        Assert.Null(created.ResolvedAt);

        var list = await client.GetFromJsonAsync<PendingDecisionListResponse>(
            $"/api/v1/studio/runner/{project.ProjectId}/pending-decisions");
        var decision = Assert.Single(list!.Decisions);
        Assert.Equal(created.Id, decision.Id);
        Assert.Equal("Pick a review policy", decision.Description);
    }

    [Fact]
    public async Task Orchestrator_session_endpoints_filter_by_project_and_list_globally()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Session WS"), "test", default);
        var projectA = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Session Project A", "SPA"), "test", default);
        var projectB = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Session Project B", "SPB"), "test", default);
        await store.SendOrchestratorChatMessageAsync(
            projectA.ProjectId, new StudioOrchestratorChatMessageRequest("A activity"), "test", default);
        await store.SendOrchestratorChatMessageAsync(
            projectB.ProjectId, new StudioOrchestratorChatMessageRequest("B activity"), "test", default);

        var sessionsA = await client.GetFromJsonAsync<OrchestratorSessionListResponse>(
            $"/api/v1/studio/runner/{projectA.ProjectId}/orchestrator-session");
        Assert.All(sessionsA!.Sessions, session => Assert.Equal(projectA.ProjectId, session.ProjectId));
        Assert.Contains(sessionsA.Sessions, session => session.ProjectId == projectA.ProjectId);

        var globalSessions = await client.GetFromJsonAsync<OrchestratorSessionListResponse>(
            "/api/v1/studio/runner/global/orchestrator-session");
        Assert.Contains(globalSessions!.Sessions, session => session.ProjectId == projectA.ProjectId);
        Assert.Contains(globalSessions.Sessions, session => session.ProjectId == projectB.ProjectId);
    }

    [Fact]
    public async Task Token_summary_math_is_correct_and_cached_aggregate_honors_ttl()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Token WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Token Project", "TOK"), "test", default);

        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId, null,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "turn_a", DateTime.UtcNow, "orchestrator", "first reply", "model-x",
                new OrchestratorContextTokenUsageDto("model-x", 100, 50, 10, 5))),
            "test", default);
        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId, null,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "turn_b", DateTime.UtcNow, "orchestrator", "second reply", "model-x",
                new OrchestratorContextTokenUsageDto("model-x", 200, 75, 20, 15))),
            "test", default);

        var summary = await client.GetFromJsonAsync<TokenSummaryDto>(
            $"/api/v1/studio/runner/{project.ProjectId}/token-summary");
        Assert.Equal(300, summary!.InputTokens);
        Assert.Equal(125, summary.OutputTokens);
        Assert.Equal(30, summary.CacheReadTokens);
        Assert.Equal(20, summary.CacheCreationTokens);
        Assert.Equal(2, summary.TurnCount);

        var aggregate = await client.GetFromJsonAsync<TokenSummaryAggregateDto>(
            "/api/v1/studio/runner/token-summary-aggregate");
        Assert.Equal(300, aggregate!.InputTokens);
        Assert.Contains(aggregate.ByProject, item => item.ProjectId == project.ProjectId && item.InputTokens == 300);

        var cachedFirst = await client.GetFromJsonAsync<CachedTokenSummaryAggregateResponse>(
            "/api/v1/studio/runner/token-summary-aggregate/cached");
        Assert.Equal(300, cachedFirst!.Summary.InputTokens);

        // A second turn added after the cache is populated must NOT change
        // the cached payload until the TTL expires - the cache-hit signal is
        // `ComputedAt` staying exactly constant across repeated calls.
        await store.AppendOrchestratorContextTurnAsync(
            project.ProjectId, null,
            new AppendOrchestratorContextTurnRequest(new OrchestratorContextTurnDto(
                "turn_c", DateTime.UtcNow, "orchestrator", "third reply", "model-x",
                new OrchestratorContextTokenUsageDto("model-x", 1000, 1000, 1000, 1000))),
            "test", default);
        var cachedSecond = await client.GetFromJsonAsync<CachedTokenSummaryAggregateResponse>(
            "/api/v1/studio/runner/token-summary-aggregate/cached");
        Assert.Equal(cachedFirst.ComputedAt, cachedSecond!.ComputedAt);
        Assert.Equal(300, cachedSecond.Summary.InputTokens);

        // Force-expire the cache row directly, then confirm the next call
        // recomputes and observes the third turn.
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE studio_token_summary_cache SET computed_at = $stale WHERE cache_key = 'aggregate';";
            command.Parameters.AddWithValue("$stale", "2020-01-01T00:00:00.0000000Z");
            await command.ExecuteNonQueryAsync();
        }
        var cachedThird = await client.GetFromJsonAsync<CachedTokenSummaryAggregateResponse>(
            "/api/v1/studio/runner/token-summary-aggregate/cached");
        Assert.NotEqual(cachedFirst.ComputedAt, cachedThird!.ComputedAt);
        Assert.Equal(1300, cachedThird.Summary.InputTokens);
    }

    [Fact]
    public async Task Auto_review_queue_projects_in_flight_review_attempts()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Queue WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Queue Project", "QUE"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Queued task"), "test", default);

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using (var runCommand = connection.CreateCommand())
            {
                runCommand.CommandText = """
                    INSERT INTO runs(id, task_id, status, created_at) VALUES ('run_queue_1', $task, 'succeeded', '2026-01-01T00:00:00.0000000Z');
                    """;
                runCommand.Parameters.AddWithValue("$task", task.TaskId);
                await runCommand.ExecuteNonQueryAsync();
            }
            await using (var subjectCommand = connection.CreateCommand())
            {
                subjectCommand.CommandText = """
                    INSERT INTO review_subjects(
                        id, task_id, source_run_id, repository_id, expected_result_sha, review_policy_hash,
                        plan_json, idempotency_key, created_at)
                    VALUES ('rsub_queue_1', $task, 'run_queue_1', 'repo-1', 'deadbeef', 'policy-hash',
                            '{}', 'idem-queue-1', '2026-01-01T00:00:00.0000000Z');
                    INSERT INTO review_attempts(id, subject_id, task_id, attempt_number, status, created_at)
                    VALUES ('rat_queue_1', 'rsub_queue_1', $task, 1, 'queued', '2026-01-01T00:00:00.0000000Z');
                    """;
                subjectCommand.Parameters.AddWithValue("$task", task.TaskId);
                await subjectCommand.ExecuteNonQueryAsync();
            }
        }

        var queue = await client.GetFromJsonAsync<AutoReviewQueueResponse>("/api/v1/studio/runner/auto-review-queue");
        var entry = Assert.Single(queue!.Entries);
        Assert.Equal("rat_queue_1", entry.AttemptId);
        Assert.Equal(task.TaskId, entry.TaskId);
        Assert.Equal(project.ProjectId, entry.ProjectId);
        Assert.Equal("queued", entry.Status);
    }

    [Fact]
    public async Task Orchestrator_feed_merges_log_entries_and_chat_turns_newest_first()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Feed WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Feed Project", "FED"), "test", default);

        await store.SendOrchestratorChatMessageAsync(
            project.ProjectId, new StudioOrchestratorChatMessageRequest("feed chat turn"), "test", default);
        var overrideResponse = await client.PostAsJsonAsync(
            $"/api/v1/studio/runner/{project.ProjectId}/orchestrator-log/override",
            new { entry = new { note = "feed log entry" } });
        overrideResponse.EnsureSuccessStatusCode();

        var feed = await client.GetFromJsonAsync<OrchestratorFeedResponse>("/api/v1/studio/runner/orchestrator-feed?limit=50");
        Assert.Contains(feed!.Entries, item => item.Kind == "turn" && item.Text == "feed chat turn");
        Assert.Contains(feed.Entries, item => item.Kind == "log" && item.Text.Contains("feed log entry"));
        for (var i = 1; i < feed.Entries.Count; i++)
            Assert.True(feed.Entries[i - 1].OccurredAt >= feed.Entries[i].OccurredAt);
    }

    [Fact]
    public async Task Queue_starvation_counts_ready_tasks_with_no_recent_run()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioTestApiFactory(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Starvation WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Starvation Project", "STV"), "test", default);
        var starvedTask = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Starved task", State: "2-ready"), "test", default);
        var freshTask = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Fresh task", State: "2-ready"), "test", default);

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO runs(id, task_id, status, created_at) VALUES ('run_fresh_1', $task, 'running', $now);";
            command.Parameters.AddWithValue("$task", freshTask.TaskId);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var starvation = await client.GetFromJsonAsync<QueueStarvationResponse>(
            "/api/v1/studio/runner/queue-starvation?windowMinutes=15");
        Assert.Contains(starvation!.Tasks, task => task.TaskId == starvedTask.TaskId);
        Assert.DoesNotContain(starvation.Tasks, task => task.TaskId == freshTask.TaskId);
    }
}
