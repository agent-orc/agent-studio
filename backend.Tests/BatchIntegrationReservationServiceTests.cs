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
        var reservation = new BatchIntegrationReservationService(timeProvider: clock,
            alertAfter: TimeSpan.FromMinutes(15));
        reservation.MarkPushPending("project", "AGT-42", "abc");
        clock.Advance(TimeSpan.FromMinutes(16));
        var wait = reservation.GetWait("project");
        Assert.NotNull(wait);
        Assert.True(wait.AgeAlert);
        Assert.Contains("waiting on push AGT-42@abc since", wait.Reason);

        var root = Path.Combine(Path.GetTempPath(), "batch-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = BatchGateStateSnapshotReader.Read(new BatchGateStore(root),
                "project", new BatchGateFormationOptions(Enabled: true),
                pushWait: wait);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(state,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Contains("waiting on push AGT-42@abc since",
                json.RootElement.GetProperty("plan").GetProperty("reason").GetString());
            Assert.True(json.RootElement.GetProperty("pushWait")
                .GetProperty("ageAlert").GetBoolean());
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
    public void Each_reconciliation_pass_fetches_a_branch_once()
    {
        var cache = new Dictionary<(string Repo, string Branch), string?>();
        var fetches = 0;
        string? Fetch(string repo, string branch, CancellationToken ct)
        {
            fetches++;
            return "tip-" + fetches;
        }

        Assert.Equal("tip-1", BatchGatePilotService.FetchBaseOnce(
            cache, "repo", "develop", Fetch, CancellationToken.None));
        Assert.Equal("tip-1", BatchGatePilotService.FetchBaseOnce(
            cache, "repo", "develop", Fetch, CancellationToken.None));
        Assert.Equal(1, fetches);
        cache.Clear();
        Assert.Equal("tip-2", BatchGatePilotService.FetchBaseOnce(
            cache, "repo", "develop", Fetch, CancellationToken.None));
        Assert.Equal(2, fetches);
    }
}
