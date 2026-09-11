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
    public void A_drain_request_and_its_daemon_acknowledgement_round_trip()
    {
        Assert.Null(ReviewDrainGuard.ReadDrainRequest(_root));

        var written = ReviewDrainGuard.RequestDrain(_root, "release promotion");
        var request = ReviewDrainGuard.ReadDrainRequest(_root);

        Assert.NotNull(request);
        Assert.Equal(written.RequestId, request!.RequestId);
        Assert.Equal("release promotion", request!.Reason);
        Assert.Equal(ReviewDrainGuard.DrainMode, request.Mode);
        Assert.Null(ReviewDrainGuard.ReadDrainAcknowledgement(_root));

        ReviewDrainGuard.AcknowledgeDrain(_root, request, activeSlots: 2);
        var acknowledgement = ReviewDrainGuard.ReadDrainAcknowledgement(_root);

        Assert.NotNull(acknowledgement);
        Assert.Equal(request.RequestId, acknowledgement!.RequestId);
        Assert.Equal(2, acknowledgement.ActiveSlots);

        ReviewDrainGuard.ClearDrainState(_root);
        Assert.Null(ReviewDrainGuard.ReadDrainRequest(_root));
        Assert.Null(ReviewDrainGuard.ReadDrainAcknowledgement(_root));
    }

    [Fact]
    public void Daemon_acknowledgement_needs_only_read_access_to_a_root_created_control_lock()
    {
        if (!OperatingSystem.IsLinux()) return;
        var request = ReviewDrainGuard.RequestRestartGuard(_root, "root helper simulation");
        var lockPath = Path.Combine(_root, "review-drain-control.lock");
        File.SetUnixFileMode(
            lockPath,
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        ReviewDrainGuard.AcknowledgeDrain(_root, request, activeSlots: 0);

        Assert.Equal(
            request.RequestId,
            ReviewDrainGuard.ReadDrainAcknowledgement(_root)?.RequestId);
    }

    [Fact]
    public void An_unparsable_marker_still_counts_as_a_drain_request()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ReviewDrainGuard.MarkerPath(_root), "{ not json");

        Assert.NotNull(ReviewDrainGuard.ReadDrainRequest(_root));
    }

    [Fact]
    public async Task The_restart_guard_command_refuses_busy_slots_and_withdraws_its_barrier()
    {
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        state.Save(Slot("attempt-running", "running"));
        var logs = new List<string>();

        var refused = ReviewDrainCommand.RunRestartGuardAsync(
            options,
            logs.Add,
            CancellationToken.None);
        ReviewDrainGuard.DrainRequest? request;
        while ((request = ReviewDrainGuard.ReadDrainRequest(options.StateDir)) is null)
            await Task.Delay(10);
        Assert.Equal(ReviewDrainGuard.RestartGuardMode, request.Mode);
        ReviewDrainGuard.AcknowledgeDrain(options.StateDir, request, activeSlots: 1);

        var refusedExit = await refused.WaitAsync(TimeSpan.FromSeconds(10));
        var forced = await ReviewDrainCommand.RunRestartGuardAsync(
            Options(force: true),
            logs.Add,
            CancellationToken.None);

        Assert.Equal(3, refusedExit);
        Assert.Equal(0, forced);
        Assert.Null(ReviewDrainGuard.ReadDrainRequest(options.StateDir));
        Assert.Contains(logs, line =>
            line.Contains("restart-guard refused", StringComparison.Ordinal)
            && line.Contains("attempt-running", StringComparison.Ordinal)
            && line.Contains("agent-host --drain", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("restart-guard forced", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_idle_restart_guard_withdraws_or_holds_the_acknowledged_barrier(
        bool holdAdmission)
    {
        var options = Options(holdAdmission: holdAdmission);
        var guard = ReviewDrainCommand.RunRestartGuardAsync(
            options,
            _ => { },
            CancellationToken.None);
        ReviewDrainGuard.DrainRequest? request;
        while ((request = ReviewDrainGuard.ReadDrainRequest(options.StateDir)) is null)
            await Task.Delay(10);
        ReviewDrainGuard.AcknowledgeDrain(options.StateDir, request, activeSlots: 0);

        Assert.Equal(0, await guard.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            holdAdmission,
            ReviewDrainGuard.ReadDrainRequest(options.StateDir) is not null);
    }

    [Fact]
    public void An_old_guard_cannot_withdraw_a_newer_control_request()
    {
        var first = ReviewDrainGuard.RequestRestartGuard(_root, "first replacement");
        var second = ReviewDrainGuard.RequestDrain(_root, "operator drain");

        Assert.False(ReviewDrainGuard.WithdrawRequest(_root, first.RequestId));
        Assert.Equal(second.RequestId, ReviewDrainGuard.ReadDrainRequest(_root)?.RequestId);
    }

    [Fact]
    public async Task A_restart_guard_cannot_replace_an_existing_drain_request()
    {
        var drain = ReviewDrainGuard.RequestDrain(_root, "operator drain");
        var logs = new List<string>();

        var exitCode = await ReviewDrainCommand.RunRestartGuardAsync(
            Options(),
            logs.Add,
            CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Equal(drain.RequestId, ReviewDrainGuard.ReadDrainRequest(_root)?.RequestId);
        Assert.Contains(logs, line =>
            line.Contains("already active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pre_control_protocol_daemon_fails_the_candidate_guard_closed()
    {
        var options = Options();
        var logs = new List<string>();

        // No daemon acknowledges the candidate's marker. This models the
        // one-time upgrade from a release that predates host-local control.
        var exitCode = await ReviewDrainCommand.RunRestartGuardAsync(
                options,
                logs.Add,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, exitCode);
        Assert.Null(ReviewDrainGuard.ReadDrainRequest(options.StateDir));
        Assert.Contains(logs, line =>
            line.Contains("did not acknowledge closed admission", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drain_requests_the_stop_and_returns_when_the_last_slot_clears()
    {
        var options = Options();
        var state = new ReviewStateStore(options.StateDir);
        var busy = state.Save(Slot("attempt-running", "running"));
        var logs = new List<string>();

        var drain = ReviewDrainCommand.RunDrainAsync(options, logs.Add, CancellationToken.None);
        ReviewDrainGuard.DrainRequest? request;
        while ((request = ReviewDrainGuard.ReadDrainRequest(options.StateDir)) is null)
            await Task.Delay(10);
        ReviewDrainGuard.AcknowledgeDrain(options.StateDir, request!, activeSlots: 1);
        state.Delete(busy);
        ReviewDrainGuard.AcknowledgeDrain(options.StateDir, request!, activeSlots: 0);

        Assert.Equal(0, await drain.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(logs, line => line.Contains("drain requested", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("drain acknowledged", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("drain complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_census_does_not_complete_before_the_daemon_acknowledges_admission_closed()
    {
        var options = Options(drainTimeoutSeconds: 5);
        var logs = new List<string>();

        var drain = ReviewDrainCommand.RunDrainAsync(options, logs.Add, CancellationToken.None);
        ReviewDrainGuard.DrainRequest? request;
        while ((request = ReviewDrainGuard.ReadDrainRequest(options.StateDir)) is null)
            await Task.Delay(10);

        await Task.Delay(100);
        Assert.False(drain.IsCompleted);

        ReviewDrainGuard.AcknowledgeDrain(options.StateDir, request!, activeSlots: 0);

        Assert.Equal(0, await drain.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Drain_fails_closed_when_any_slot_record_is_unreadable()
    {
        var options = Options(drainTimeoutSeconds: 5);
        var state = new ReviewStateStore(options.StateDir);
        File.WriteAllText(Path.Combine(state.Root, "attempt-torn.review-slot.json"), "{ not json");
        var logs = new List<string>();

        var exitCode = await ReviewDrainCommand.RunDrainAsync(
            options,
            logs.Add,
            CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains(logs, line =>
            line.Contains("drain refused", StringComparison.Ordinal)
            && line.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drain_reports_the_slots_it_could_not_finish_within_its_bound()
    {
        var options = Options(drainTimeoutSeconds: 1);
        var state = new ReviewStateStore(options.StateDir);
        state.Save(Slot("attempt-running", "running"));
        var logs = new List<string>();

        var drain = ReviewDrainCommand.RunDrainAsync(options, logs.Add, CancellationToken.None);
        ReviewDrainGuard.DrainRequest? request;
        while ((request = ReviewDrainGuard.ReadDrainRequest(options.StateDir)) is null)
            await Task.Delay(10);
        ReviewDrainGuard.AcknowledgeDrain(options.StateDir, request!, activeSlots: 1);

        var exitCode = await drain.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(3, exitCode);
        Assert.Contains(logs, line =>
            line.Contains("drain timed out", StringComparison.Ordinal)
            && line.Contains("attempt-running", StringComparison.Ordinal));
    }

    private RunnerOptions Options(
        bool force = false,
        int drainTimeoutSeconds = 3600,
        bool holdAdmission = false) => new()
    {
        Force = force,
        HoldAdmission = holdAdmission,
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
        ServerRequestTimeoutSeconds = 2,
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
