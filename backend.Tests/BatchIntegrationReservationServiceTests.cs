using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Tests;

public sealed class BatchIntegrationReservationServiceTests
{
    [Fact]
    public async Task Reconciliation_skips_a_merge_that_still_owns_the_project()
    {
        var reservation = new BatchIntegrationReservationService();
        using (await reservation.AcquireAsync("project", CancellationToken.None))
            Assert.Null(reservation.TryAcquire("project"));
        using var grant = reservation.TryAcquire("project");
        Assert.NotNull(grant);
    }

    [Fact]
    public void Batch_state_exposes_the_bounded_direct_integration_gate_reservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-state-cap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = BatchGateStateSnapshotReader.Read(new BatchGateStore(root),
                "project", new BatchGateFormationOptions(Enabled: true));
            var json = JsonSerializer.SerializeToElement(state,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.Equal(27, json.GetProperty("integrationGateLeaseCapMinutes").GetDouble());
            Assert.Equal(TimeSpan.FromMinutes(25), BatchIntegrationReservationService.GateBudget(
                TimeSpan.FromMinutes(40), reservationHeld: true));
            Assert.Equal(TimeSpan.FromMinutes(40), BatchIntegrationReservationService.GateBudget(
                TimeSpan.FromMinutes(40), reservationHeld: false));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Crash_window_marker_is_reconciled_before_batch_wait()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-push-" + Guid.NewGuid().ToString("N"));
        try
        {
            new BatchIntegrationReservationService(root).MarkPushPending(
                "project", "AGT-42", "approved-sha");
            var afterRestart = new BatchIntegrationReservationService(root);
            var checkedOrigin = false;
            using var grant = await afterRestart.AcquireBatchAsync("project", marker =>
            {
                checkedOrigin = true;
                return marker.ApprovedSha == "approved-sha";
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(checkedOrigin);
            Assert.Null(afterRestart.GetWait("project"));
            Assert.Empty(Directory.GetFiles(root, "*.pending", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Wait_reason_and_age_alert_are_exposed_by_state()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0,
            TimeSpan.Zero));
        var reservation = new BatchIntegrationReservationService(timeProvider: clock);
        reservation.MarkPushPending("project", "AGT-42", "abc");
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(reservation.GetWait("project")!.AgeAlert);
        clock.Advance(TimeSpan.FromMinutes(2));
        var wait = reservation.GetWait("project");
        Assert.NotNull(wait);
        Assert.True(wait.AgeAlert);
        Assert.Contains("waiting on approved integration push AGT-42@abc since", wait.Reason);
        Assert.Equal(4, wait.AgeMinutes);
        reservation.MarkPushPending("project", "AGT-43", "def");

        var root = Path.Combine(Path.GetTempPath(), "batch-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = BatchGateStateSnapshotReader.Read(new BatchGateStore(root),
                "project", new BatchGateFormationOptions(Enabled: true),
                pushWait: wait, pushWaits: reservation.GetWaits("project"));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(state,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Contains("waiting on approved integration push AGT-42@abc since",
                json.RootElement.GetProperty("plan").GetProperty("reason").GetString());
            Assert.True(json.RootElement.GetProperty("pushWait")
                .GetProperty("ageAlert").GetBoolean());
            Assert.Equal(4, json.RootElement.GetProperty("pushWait")
                .GetProperty("ageMinutes").GetDouble());
            Assert.Equal(2, json.RootElement.GetProperty("pushWaits").GetArrayLength());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Persisted_markers_are_visible_to_the_tick_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-push-" + Guid.NewGuid().ToString("N"));
        try
        {
            new BatchIntegrationReservationService(root).MarkPushPending(
                "project", "AGT-42", "approved-sha");
            var restarted = new BatchIntegrationReservationService(root);

            Assert.Contains("project", restarted.ProjectsWithPending());
            Assert.NotNull(restarted.GetWait("project"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Legacy_persisted_markers_load_from_configured_projects()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-push-" + Guid.NewGuid().ToString("N"));
        try
        {
            new BatchIntegrationReservationService(root).MarkPushPending(
                "project", "AGT-42", "approved-sha");
            var path = Assert.Single(Directory.GetFiles(root, "*.pending",
                SearchOption.AllDirectories));
            var marker = JsonSerializer.Deserialize<PendingIntegrationPush>(File.ReadAllText(path))!;
            File.WriteAllText(path, JsonSerializer.Serialize(marker with { Project = null }));
            var restarted = new BatchIntegrationReservationService(root);

            restarted.LoadPersistedProjects(["project"]);

            Assert.Contains("project", restarted.ProjectsWithPending());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Batch_wait_reconciliation_fetches_a_branch_once_per_pass()
    {
        var reservation = new BatchIntegrationReservationService();
        reservation.MarkPushPending("project", "AGT-1", "sha-1");
        reservation.MarkPushPending("project", "AGT-2", "sha-2");
        var cache = new Dictionary<(string Repo, string Branch), string?>();
        var fetches = 0;
        string? Fetch(string repo, string branch, CancellationToken ct)
        {
            fetches++;
            return "tip-" + fetches;
        }

        using var grant = await reservation.AcquireBatchAsync("project", marker =>
        {
            Assert.Equal("tip-1", BatchGatePilotService.FetchBaseOnce(
                cache, "repo", "develop", Fetch, CancellationToken.None));
            return true;
        }, CancellationToken.None, cache.Clear);
        Assert.Equal(1, fetches);
        Assert.Null(reservation.GetWait("project"));
    }

    // AGT-3882: per-task gates arrive every few minutes while a flight reserves.
    [Fact]
    public async Task Stream_of_per_task_integrations_does_not_starve_a_reserving_batch()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 11, 8, 0, TimeSpan.Zero));
        var reservation = new BatchIntegrationReservationService(timeProvider: clock);
        var running = await reservation.AcquireAsync("project",
            IntegrationReservationKinds.PerTaskIntegration, "AGT-3868", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(4));
        var batch = reservation.AcquireBatchAsync("project", null, CancellationToken.None,
            batchId: "f8037b78");
        var arrivals = new List<Task<IDisposable>>();
        foreach (var key in new[] { "AGT-3852", "AGT-3816", "AGT-3870" })
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            arrivals.Add(reservation.AcquireAsync("project",
                key == "AGT-3816" ? IntegrationReservationKinds.PerTaskFallbackIntegration
                    : IntegrationReservationKinds.PerTaskIntegration, key, CancellationToken.None));
        }
        Assert.False(batch.IsCompleted);

        // The wait is bounded by the running gate, not by the new arrivals.
        running.Dispose();
        using (var flight = await batch.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            await Task.Delay(50);
            Assert.All(arrivals, arrival => Assert.False(arrival.IsCompleted));
            Assert.Null(reservation.GetBatchWait("project"));
        }

        // Per-task gates resume once the flight has its reservation, in arrival order.
        var order = new List<string>();
        foreach (var (arrival, key) in arrivals.Zip(new[] { "AGT-3852", "AGT-3816", "AGT-3870" }))
        {
            using var grant = await arrival.WaitAsync(TimeSpan.FromSeconds(10));
            order.Add(key);
        }
        Assert.Equal(["AGT-3852", "AGT-3816", "AGT-3870"], order);
    }

    [Fact]
    public async Task Batch_wait_names_the_running_job_the_held_back_jobs_and_the_pending_pushes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 11, 8, 0, TimeSpan.Zero));
        var reservation = new BatchIntegrationReservationService(timeProvider: clock);
        Assert.Null(reservation.GetBatchWait("project"));
        var running = await reservation.AcquireAsync("project",
            IntegrationReservationKinds.PerTaskIntegration, "AGT-3868", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(4));
        using var cancel = new CancellationTokenSource();
        var batch = reservation.AcquireBatchAsync("project", null, cancel.Token, batchId: "f8037b78");
        clock.Advance(TimeSpan.FromMinutes(11));
        var heldBack = reservation.AcquireAsync("project",
            IntegrationReservationKinds.PerTaskIntegration, "AGT-3852", CancellationToken.None);

        var wait = Assert.IsType<BatchReservationWait>(reservation.GetBatchWait("project"));
        Assert.Equal("f8037b78", wait.BatchId);
        Assert.Equal(11, wait.WaitMinutes);
        Assert.Equal(new IntegrationReservationJob(IntegrationReservationKinds.PerTaskIntegration, "AGT-3868",
            new DateTimeOffset(2026, 11, 16, 11, 8, 0, TimeSpan.Zero)), wait.Holder);
        var held = Assert.Single(wait.HeldBack);
        Assert.Equal("AGT-3852", held.TaskKey);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 11, 23, 0, TimeSpan.Zero), held.SinceUtc);
        Assert.Contains("per-task-integration AGT-3868 holds the project reservation since 2026-11-16T11:08:00", wait.Reason);
        Assert.Contains("1 per-task integration(s) wait until the flight has its reservation", wait.Reason);
        Assert.Equal("AGT-3868", reservation.PerTaskHolder("project")?.TaskKey);

        // A cancelled flight (claim expiry) lets the held-back integration start.
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
        Assert.Null(reservation.GetBatchWait("project"));
        running.Dispose();
        using var grant = await heldBack.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Pending_push_goes_before_the_batch_and_the_batch_before_held_back_integrations()
    {
        var reservation = new BatchIntegrationReservationService();
        reservation.MarkPushPending("project", "AGT-3868", "abc");
        var running = await reservation.AcquireAsync("project",
            IntegrationReservationKinds.PerTaskIntegration, "AGT-3852", CancellationToken.None);
        var batch = reservation.AcquireBatchAsync("project", null, CancellationToken.None, batchId: "b1");
        var perTask = reservation.AcquireAsync("project",
            IntegrationReservationKinds.PerTaskIntegration, "AGT-3870", CancellationToken.None);
        var push = reservation.AcquireAsync("project",
            IntegrationReservationKinds.IntegrationPush, "AGT-3868", CancellationToken.None);
        var wait = reservation.GetBatchWait("project")!;
        Assert.Equal("AGT-3868", Assert.Single(wait.QueuedAhead).TaskKey);
        Assert.Equal("AGT-3868", Assert.Single(wait.PendingPushes).JobId);

        running.Dispose();
        using (await push.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.False(batch.IsCompleted);
            reservation.MarkPushVerified("project", "AGT-3868", "abc");
        }
        using (await batch.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            await Task.Delay(50);
            Assert.False(perTask.IsCompleted);
        }
        using var resumed = await perTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
