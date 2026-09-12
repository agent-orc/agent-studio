using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class StudioP2SupervisorEndpointsTests
{
    [Fact]
    public async Task Accepted_integration_alert_is_quiet_until_a_result_finalization_genuinely_fails()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SupervisorApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();

        var quiet = await client.GetFromJsonAsync<AcceptedIntegrationAlertResponse>(
            "/api/v1/studio/pipeline/accepted-integration-alert");
        Assert.False(quiet!.HasAlert);
        Assert.Null(quiet.Detail);

        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Alert WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Alert Project", "ALR"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Alert task", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync(
            "runner-alert",
            new RegisterRunnerRequest("runner", "host-alert", "instance-alert", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-alert", "instance-alert"), "test", default);
        Assert.Equal("claimed", claim.Status);

        // result_finalizations is populated by the post-step finalization
        // pipeline (out of this group's scope); insert one genuine failed row
        // referencing the real run so the alert query has real data to find.
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO result_finalizations(run_id, status, attempt_count, max_attempts, error, last_idempotency_key, updated_at)
                VALUES ($run, 'failed', 3, 3, 'artifact sha mismatch', 'idem-alert-1', $now);
                """;
            command.Parameters.AddWithValue("$run", claim.Run!.RunId);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var alert = await client.GetFromJsonAsync<AcceptedIntegrationAlertResponse>(
            "/api/v1/studio/pipeline/accepted-integration-alert");
        Assert.True(alert!.HasAlert);
        Assert.Equal("artifact sha mismatch", alert.Detail);
        Assert.NotNull(alert.DetectedAt);
        _ = task;
    }

    [Fact]
    public async Task Cycle_time_regression_radar_and_throughput_are_derived_from_recorded_lane_transitions()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SupervisorApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Cycle WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Cycle Project", "CYC"), "test", default);

        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Cycle task"), "test", default);
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest("2-ready"), "test", default);
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest("3-progress"), "test", default);
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest("4-auto-review"), "test", default);
        // A bounce back to progress is a regression the radar should flag.
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest("3-progress"), "test", default);
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest("4-auto-review"), "test", default);
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest("6-completed"), "test", default);

        var aggregate = await client.GetFromJsonAsync<CycleTimeAggregateResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/cycle-time?window=all");
        Assert.Equal(1, aggregate!.TaskCount);
        Assert.Equal(1, aggregate.CompletedTaskCount);
        Assert.NotNull(aggregate.MedianCycleTimeSeconds);
        Assert.NotEmpty(aggregate.StageTotals);
        var taskSummary = Assert.Single(aggregate.Tasks);
        Assert.True(taskSummary.Completed);
        Assert.Null(taskSummary.Transitions);

        var single = await client.GetFromJsonAsync<CycleTimeTaskResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/cycle-time/tasks/{task.TaskKey}");
        Assert.True(single!.Task.Completed);
        Assert.NotNull(single.Task.Transitions);
        Assert.True(single.Task.Transitions!.Count >= 6);

        var missingWindow = await client.GetAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/cycle-time?window=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, missingWindow.StatusCode);

        var radar = await client.GetFromJsonAsync<RegressionRadarResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/regression-radar");
        Assert.Equal(1, radar!.RegressionCount);
        var regression = Assert.Single(radar.Recent);
        Assert.Equal("4-auto-review", regression.FromState);
        Assert.Equal("3-progress", regression.ToState);

        var throughput = await client.GetFromJsonAsync<ThroughputResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/throughput?window=all");
        Assert.Equal(1, throughput!.TotalCompleted);
        Assert.Equal(throughput.TotalCompleted, throughput.Periods.Sum(period => period.CompletedCount));
    }

    [Fact]
    public async Task Supervisor_cancel_run_and_force_fail_release_the_active_lease_through_the_existing_authority_path()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SupervisorApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Intervene WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Intervene Project", "ITV"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Interveneable", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync(
            "runner-intervene",
            new RegisterRunnerRequest("runner", "host-intervene", "instance-intervene", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-intervene", "instance-intervene"), "test", default);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal("3-progress", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);

        var cancel = await client.PostAsync($"/api/v1/studio/supervisor/{project.ProjectId}/intervene/cancel-run", null);
        cancel.EnsureSuccessStatusCode();
        var cancelled = await cancel.Content.ReadFromJsonAsync<SupervisorInterventionResponse>();
        Assert.Equal(1, cancelled!.AffectedRunCount);
        Assert.Contains(claim.Run!.RunId, cancelled.RunIds);
        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);

        // No active run left: a second cancel is a safe no-op.
        var cancelAgain = await client.PostAsync($"/api/v1/studio/supervisor/{project.ProjectId}/intervene/cancel-run", null);
        cancelAgain.EnsureSuccessStatusCode();
        Assert.Equal(0, (await cancelAgain.Content.ReadFromJsonAsync<SupervisorInterventionResponse>())!.AffectedRunCount);

        var reclaim = await store.ClaimAsync(new ClaimRequest("runner-intervene", "instance-intervene"), "test", default);
        Assert.Equal("claimed", reclaim.Status);
        var forceFail = await client.PostAsync($"/api/v1/studio/supervisor/{project.ProjectId}/intervene/force-fail", null);
        forceFail.EnsureSuccessStatusCode();
        Assert.Equal(1, (await forceFail.Content.ReadFromJsonAsync<SupervisorInterventionResponse>())!.AffectedRunCount);
    }

    [Fact]
    public async Task Pause_and_resume_pickup_round_trip_through_meta_cycle_and_observation()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SupervisorApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Pickup WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Pickup Project", "PCK"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Backlog"), "test", default);
        await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Ready", State: "2-ready"), "test", default);

        var initialObservation = await client.GetFromJsonAsync<SupervisorObservationResponse>(
            $"/api/v1/studio/supervisor/{project.ProjectId}/observation");
        Assert.False(initialObservation!.PickupPaused);
        Assert.Equal(0, initialObservation.ActiveRunCount);
        Assert.Equal(1, initialObservation.QueueDepthByState["0-backlog"]);
        Assert.Equal(1, initialObservation.QueueDepthByState["2-ready"]);

        var pause = await client.PostAsync($"/api/v1/studio/supervisor/{project.ProjectId}/intervene/pause-pickup", null);
        pause.EnsureSuccessStatusCode();
        Assert.True((await pause.Content.ReadFromJsonAsync<SupervisorPickupStateResponse>())!.PickupPaused);

        var metaAfterPause = await client.GetFromJsonAsync<SupervisorMetaCycleResponse>(
            $"/api/v1/studio/supervisor/{project.ProjectId}/meta-cycle");
        Assert.Equal(1, metaAfterPause!.InterventionCount);
        Assert.Equal("pause-pickup", metaAfterPause.LastInterventionKind);
        Assert.True(metaAfterPause.PickupPaused);

        var observationAfterPause = await client.GetFromJsonAsync<SupervisorObservationResponse>(
            $"/api/v1/studio/supervisor/{project.ProjectId}/observation");
        Assert.True(observationAfterPause!.PickupPaused);

        var resume = await client.PostAsync($"/api/v1/studio/supervisor/{project.ProjectId}/intervene/resume", null);
        resume.EnsureSuccessStatusCode();
        Assert.False((await resume.Content.ReadFromJsonAsync<SupervisorPickupStateResponse>())!.PickupPaused);

        var metaAfterResume = await client.GetFromJsonAsync<SupervisorMetaCycleResponse>(
            $"/api/v1/studio/supervisor/{project.ProjectId}/meta-cycle");
        Assert.Equal(2, metaAfterResume!.InterventionCount);
        Assert.Equal("resume", metaAfterResume.LastInterventionKind);
        Assert.False(metaAfterResume.PickupPaused);
    }

    [Fact]
    public async Task Recent_events_replays_only_this_projects_studio_stream_events_newest_first()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SupervisorApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Events WS"), "test", default);
        var projectA = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Events Project A", "EVA"), "test", default);
        var projectB = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Events Project B", "EVB"), "test", default);

        await store.AppendStudioStreamEventAsync(StudioStreamEventKinds.TaskCreated, projectA.ProjectId, "tsk_a1", new { note = "a1" }, default);
        await store.AppendStudioStreamEventAsync(StudioStreamEventKinds.TaskMoved, projectB.ProjectId, "tsk_b1", new { note = "b1" }, default);
        var lastA = await store.AppendStudioStreamEventAsync(StudioStreamEventKinds.TaskStarted, projectA.ProjectId, "tsk_a1", new { note = "a2" }, default);

        var recent = await client.GetFromJsonAsync<SupervisorRecentEventsResponse>(
            $"/api/v1/studio/supervisor/{projectA.ProjectId}/recent-events");
        Assert.Equal(2, recent!.Events.Count);
        Assert.All(recent.Events, item => Assert.Equal(projectA.ProjectId, item.ProjectId));
        Assert.Equal(lastA.Cursor, recent.Events[0].Cursor);
    }

    [Fact]
    public async Task Queue_health_repair_reclaims_a_task_whose_lease_expired_without_a_release()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SupervisorApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Repair WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Repair Project", "RPR"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("Stuck task", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync(
            "runner-repair",
            new RegisterRunnerRequest("runner", "host-repair", "instance-repair", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-repair", "instance-repair"), "test", default);
        Assert.Equal("claimed", claim.Status);

        // Simulate a runner that vanished without ever renewing or releasing
        // its lease: back-date expires_at directly, the way a stalled process
        // would leave it, without going through any Task Server write path.
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE leases SET expires_at = '2000-01-01T00:00:00.0000000Z' WHERE run_id = $run;";
            command.Parameters.AddWithValue("$run", claim.Run!.RunId);
            await command.ExecuteNonQueryAsync();
        }

        var repair = await client.PostAsync($"/api/v1/studio/projects/{project.ProjectId}/queue-health/repair", null);
        repair.EnsureSuccessStatusCode();
        var result = await repair.Content.ReadFromJsonAsync<QueueHealthRepairResponse>();
        Assert.Equal(1, result!.LeasesMarkedUnknown);
        Assert.Equal(1, result.TasksRequeued);
        Assert.Equal("2-ready", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);

        // Idempotent: running it again finds nothing left to repair.
        var repairAgain = await client.PostAsync($"/api/v1/studio/projects/{project.ProjectId}/queue-health/repair", null);
        repairAgain.EnsureSuccessStatusCode();
        var resultAgain = await repairAgain.Content.ReadFromJsonAsync<QueueHealthRepairResponse>();
        Assert.Equal(0, resultAgain!.LeasesMarkedUnknown);
        Assert.Equal(0, resultAgain.TasksRequeued);
    }

    [Fact]
    public async Task Test_run_ingestion_backs_the_project_test_runs_listing_newest_first()
    {
        using var temp = new TempDirectory();
        await using var factory = new P2SupervisorApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Test Runs WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Test Runs Project", "TRN"), "test", default);

        var empty = await client.GetFromJsonAsync<TestRunListResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/test-runs");
        Assert.Empty(empty!.Runs);

        var first = await client.PostAsJsonAsync(
            $"/api/v1/studio/test-runs/{project.ProjectId}/ingest",
            new IngestTestRunRequest(Outcome: "passed", StartedAt: DateTime.UtcNow.AddMinutes(-10)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/studio/test-runs/{project.ProjectId}/ingest",
            new IngestTestRunRequest(Outcome: "failed", StartedAt: DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondRun = await second.Content.ReadFromJsonAsync<TestRunDto>();

        var list = await client.GetFromJsonAsync<TestRunListResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/test-runs");
        Assert.Equal(2, list!.Runs.Count);
        Assert.Equal(secondRun!.TestRunId, list.Runs[0].TestRunId);
        Assert.Equal("failed", list.Runs[0].Outcome);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p2-supervisor-test");
        return client;
    }

    private sealed class P2SupervisorApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
