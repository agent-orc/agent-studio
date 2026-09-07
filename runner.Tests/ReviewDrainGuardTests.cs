using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The pre-stop check that made a plain review restart refuse instead of
/// discarding tens of minutes of gate work per slot, plus the drain request the
/// daemon reads.
/// </summary>
public sealed class ReviewDrainGuardTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-drain-guard-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Theory]
    [InlineData("preparing", true)]
    [InlineData("launching", true)]
    [InlineData("running", true)]
    [InlineData("finalizing", true)]
    [InlineData("handed-off", true)]
    [InlineData("report-submitting", true)]
    [InlineData("report-pending", true)]
    [InlineData("report-accepted", false)]
    [InlineData("report-rejected-terminal", false)]
    [InlineData("terminal-cleanup-pending", false)]
    [InlineData("adoption-failed", false)]
    [InlineData("cleaned", false)]
    [InlineData(null, false)]
    public void Only_a_slot_that_still_owes_a_report_counts_as_busy(string? phase, bool busy)
        => Assert.Equal(busy, ReviewRestartGuardPolicy.IsBusyPhase(phase));

    [Theory]
    [InlineData(0, 0, false, ReviewRestartGuardDecision.Allow)]
    [InlineData(0, 0, true, ReviewRestartGuardDecision.Allow)]
    [InlineData(2, 0, false, ReviewRestartGuardDecision.RefuseBusy)]
    [InlineData(2, 0, true, ReviewRestartGuardDecision.Allow)]
    [InlineData(0, 1, false, ReviewRestartGuardDecision.RefuseUnreadable)]
    [InlineData(0, 1, true, ReviewRestartGuardDecision.Allow)]
    [InlineData(2, 1, false, ReviewRestartGuardDecision.RefuseBusy)]
    public void Restart_admission_is_a_direct_function_of_busy_unreadable_and_force(
        int busy,
        int unreadable,
        bool force,
        ReviewRestartGuardDecision expected)
    {
        var snapshot = new ReviewSlotBusySnapshot(
            busy + unreadable,
            unreadable,
            Enumerable.Range(0, busy).Select(index => $"attempt-{index}").ToArray());

        Assert.Equal(expected, ReviewRestartGuardPolicy.Decide(snapshot, force));
    }

    [Fact]
    public void An_absent_state_directory_reports_no_work_at_risk()
    {
        var snapshot = ReviewDrainGuard.Inspect(Path.Combine(_root, "never-started"));

        Assert.Equal(0, snapshot.Total);
        Assert.Equal(0, snapshot.Busy);
        Assert.Equal(0, snapshot.Unreadable);
    }

    [Fact]
    public void The_census_names_the_busy_attempts_and_ignores_terminal_leftovers()
    {
        var state = new ReviewStateStore(_root);
        state.Save(Slot("attempt-running", "running"));
        state.Save(Slot("attempt-handed-off", "handed-off"));
        state.Save(Slot("attempt-done", "terminal-cleanup-pending"));

        var snapshot = ReviewDrainGuard.Inspect(_root);

        Assert.Equal(3, snapshot.Total);
        Assert.Equal(0, snapshot.Unreadable);
        Assert.Equal(
            ["attempt-handed-off", "attempt-running"],
            snapshot.BusyAttemptIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void A_corrupt_slot_record_is_counted_rather_than_thrown_and_fails_the_guard_closed()
    {
        var state = new ReviewStateStore(_root);
        state.Save(Slot("attempt-done", "report-accepted"));
        File.WriteAllText(Path.Combine(state.Root, "attempt-torn.review-slot.json"), "{ not json");

        var snapshot = ReviewDrainGuard.Inspect(_root);

        Assert.Equal(2, snapshot.Total);
        Assert.Equal(0, snapshot.Busy);
        Assert.Equal(1, snapshot.Unreadable);
        Assert.Equal(
            ReviewRestartGuardDecision.RefuseUnreadable,
            ReviewRestartGuardPolicy.Decide(snapshot, force: false));
    }

    [Fact]
    public void A_drain_request_round_trips_and_the_next_daemon_start_clears_it()
    {
        Assert.Null(ReviewDrainGuard.ReadDrainRequest(_root));

        ReviewDrainGuard.RequestDrain(_root, "release promotion");
        var request = ReviewDrainGuard.ReadDrainRequest(_root);

        Assert.NotNull(request);
        Assert.Equal("release promotion", request!.Reason);

        ReviewDrainGuard.ClearDrainRequest(_root);
        Assert.Null(ReviewDrainGuard.ReadDrainRequest(_root));
    }

    [Fact]
    public void An_unparsable_marker_still_counts_as_a_drain_request()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ReviewDrainGuard.MarkerPath(_root), "{ not json");

        Assert.NotNull(ReviewDrainGuard.ReadDrainRequest(_root));
    }

    [Fact]
    public void The_restart_guard_command_refuses_busy_slots_and_names_the_drain()
    {
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        state.Save(Slot("attempt-running", "running"));
        var logs = new List<string>();

        var refused = ReviewDrainCommand.RunRestartGuard(options, logs.Add);
        var forced = ReviewDrainCommand.RunRestartGuard(Options(force: true), logs.Add);

        Assert.Equal(3, refused);
        Assert.Equal(0, forced);
        Assert.Contains(logs, line =>
            line.Contains("restart-guard refused", StringComparison.Ordinal)
            && line.Contains("attempt-running", StringComparison.Ordinal)
            && line.Contains("agent-host --drain", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("restart-guard forced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drain_requests_the_stop_and_returns_when_the_last_slot_clears()
    {
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        var busy = state.Save(Slot("attempt-running", "running"));
        var logs = new List<string>();

        var drain = ReviewDrainCommand.RunDrainAsync(options, logs.Add, CancellationToken.None);
        while (ReviewDrainGuard.ReadDrainRequest(options.StateDir) is null)
            await Task.Delay(10);
        state.Delete(busy);

        Assert.Equal(0, await drain.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(logs, line => line.Contains("drain requested", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("drain complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drain_reports_the_slots_it_could_not_finish_within_its_bound()
    {
        var options = Options(drainTimeoutSeconds: 1);
        var state = new ReviewStateStore(options.StateDir);
        state.Save(Slot("attempt-running", "running"));
        var logs = new List<string>();

        var exitCode = await ReviewDrainCommand
            .RunDrainAsync(options, logs.Add, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(3, exitCode);
        Assert.Contains(logs, line =>
            line.Contains("drain timed out", StringComparison.Ordinal)
            && line.Contains("attempt-running", StringComparison.Ordinal));
    }

    private RunnerOptions Options(bool force = false, int drainTimeoutSeconds = 3600) => new()
    {
        Force = force,
        DrainTimeoutSeconds = drainTimeoutSeconds,
        ServerUrl = "http://127.0.0.1:5030",
        RunnerId = "review-runner",
        RunnerName = "review-runner",
        Hostname = "review-host",
        BackendName = "test",
        Role = "review",
        WorkDir = Path.Combine(_root, "work"),
        ReviewWorkDir = Path.Combine(_root, "review-work"),
        StateDir = _root,
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        PollSeconds = 1,
    };

    private PersistedReviewSlot Slot(string attemptId, string phase)
    {
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var attempt = new ReviewAttemptDto(
            attemptId, "subject-1", "AGT-2753", 1, "leased",
            "review-runner", "review-host", 17, now, null, null, null, null);
        var subject = new ReviewSubjectDto(
            "subject-1", "AGT-2753", "run-1", "example/repository", null,
            new string('a', 40), null, "bundle", new string('b', 64),
            "coding-host", "policy-v1", new ReviewPlanDto([], []), now);
        var lease = new ReviewLeaseDto(
            "lease-1", attemptId, "subject-1", "review-runner", "instance-1",
            "review-host", 17, now, now.AddMinutes(2), "active",
            $"review-{attemptId}-f17", 25000, 23);
        return new PersistedReviewSlot(
            new ReviewClaimResponse("claimed", attempt, subject, lease),
            Path.Combine(_root, "reviews", attemptId),
            Path.Combine(_root, "review-work", attemptId, "repository"),
            null,
            null,
            phase,
            now,
            CreatedAtUtc: now);
    }
}
