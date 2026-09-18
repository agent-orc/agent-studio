using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2003 coverage for the local-vs-remote runner-badge projection
/// (<see cref="TaskRunnerService.ProjectRunnerBadge"/>). A remote runner acquires
/// the task's run lease before it spawns its CLI, so the projected badge is how
/// the board card tells a card running on another host apart from a plain local
/// run, addressing the missing ownership comparison on the stable board.
/// </summary>
public sealed class RunnerBadgeProjectionTests
{
    private static readonly DateTime Now = new(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoLease_ProjectsNull()
    {
        Assert.Null(TaskRunnerService.ProjectRunnerBadge(null, "dev@host"));
    }

    [Fact]
    public void RemoteRunnerLease_IsFlaggedRemote_WithLeaseOwnerName()
    {
        var lease = Lease(runnerId: "agent-runner-01", runnerName: "agent-runner-01", host: "linux-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: "dev@windows-host");

        Assert.NotNull(badge);
        Assert.True(badge!.IsRemote);
        Assert.Equal("agent-runner-01", badge.RunnerName);
        Assert.Equal("linux-host", badge.Hostname);
        Assert.Equal(7, badge.FencingToken);
    }

    [Fact]
    public void LeaseOwnedByTheLocalBackend_ReadsAsLocal()
    {
        var lease = Lease(runnerId: "dev@windows-host", runnerName: "dev@windows-host", host: "windows-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: "dev@windows-host");

        Assert.NotNull(badge);
        Assert.False(badge!.IsRemote);
    }

    [Fact]
    public void BlankRunnerName_FallsBackToTheRunnerId()
    {
        var lease = Lease(runnerId: "agent-runner-02", runnerName: "", host: "linux-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: "dev@windows-host");

        Assert.Equal("agent-runner-02", badge!.RunnerName);
    }

    [Fact]
    public void BlankLocalIdentity_CannotProveLocal_SoAHeldLeaseReadsAsRemote()
    {
        var lease = Lease(runnerId: "agent-runner-01", runnerName: "agent-runner-01", host: "linux-host");

        var badge = TaskRunnerService.ProjectRunnerBadge(lease, localRunnerId: null);

        Assert.True(badge!.IsRemote);
    }

    // End-to-end against the real lease authority: a remote runner acquires the
    // run lease, Peek surfaces it, and the projection turns it into a remote badge.
    [Fact]
    public void AcquiredRemoteLease_PeekedAndProjected_YieldsRemoteBadge()
    {
        var leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance);
        leases.TryAcquire(new RunLeaseAcquireRequest(
            "PT-578", "agent-runner-01", "agent-runner-01", "linux-host", 4321, "remote"));

        var peek = leases.Peek("PT-578");
        var badge = TaskRunnerService.ProjectRunnerBadge(peek.Lease, localRunnerId: "stable@windows-host");

        Assert.Equal("Held", peek.Outcome);
        Assert.NotNull(badge);
        Assert.True(badge!.IsRemote);
        Assert.Equal("agent-runner-01", badge.RunnerName);
    }

    [Fact]
    public void ExecutionProjection_LocalProcess_IsCanonicalLocalRunning()
    {
        var task = ProgressTask();
        var execution = new CliExecution { Status = "running", ProcessId = 42, StartedAt = Now.AddMinutes(-2) };

        var result = TaskRunnerService.ProjectExecutionLocation(
            task, execution, new TaskRunActivity { Kind = TaskRunActivityKinds.Active },
            new RunLeaseInspection("none", null), "agent-runner-01", LocalIdentity(), null, Now);

        Assert.Equal(TaskExecutionStates.LocalRunning, result.State);
        Assert.Equal("local", result.ExecutionKind);
        Assert.Equal(42, result.ProcessId);
        Assert.Equal("agent-runner-01", result.ConfiguredRunnerId);
    }

    [Fact]
    public void ExecutionProjection_RemoteLease_UsesActualOwner_WhenConfigurationDiffers()
    {
        var lease = Lease("agent-runner-02", "runner two", "linux-02") with { LastHeartbeatAt = Now.AddSeconds(-5) };

        var result = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask(), null, null, new RunLeaseInspection("active", lease),
            "agent-runner-01", LocalIdentity(), Now.AddSeconds(-4), Now);

        Assert.Equal(TaskExecutionStates.RemoteRunning, result.State);
        Assert.Equal("agent-runner-02", result.RunnerId);
        Assert.Equal("agent-runner-01", result.ConfiguredRunnerId);
        Assert.Contains("fenced run lease", result.TrustReason);
    }

    [Fact]
    public void ExecutionProjection_StaleRemoteLease_IsDisconnected_AndRenewedLeaseRecovers()
    {
        var stale = Lease("agent-runner-01", "agent-runner-01", "linux-01") with { LastHeartbeatAt = Now.AddMinutes(-2) };
        var disconnected = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask(), null, null, new RunLeaseInspection("active", stale),
            "agent-runner-01", LocalIdentity(), Now.AddMinutes(-2), Now);
        var renewed = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask(), null, null,
            new RunLeaseInspection("active", stale with { LastHeartbeatAt = Now }),
            "agent-runner-01", LocalIdentity(), Now, Now);

        Assert.Equal(TaskExecutionStates.RemoteDisconnected, disconnected.State);
        Assert.Equal("disconnected", disconnected.ConnectionState);
        Assert.Equal(TaskExecutionStates.RemoteRunning, renewed.State);
        Assert.Equal("connected", renewed.ConnectionState);
    }

    // Regression guard for the sticky-"Recovering"-badge bug: a Progress task
    // that holds no lease and has no live local process, yet is still receiving
    // fresh activity (the job folder's last-activity stamp is recent), is NOT
    // orphaned - a runner is demonstrably still pushing. When the project routes
    // to a remote runner, present that runner as the owner instead of a
    // "recovering / session-lost" warning: folder/push replay is the normal
    // remote recovery path (e.g. after a task-server restart dropped the
    // in-memory lease), and it must self-heal as activity keeps arriving.
    [Fact]
    public void ExecutionProjection_ProgressWithoutOwner_ButFreshRemoteActivity_HealsToRemoteRunning()
    {
        var result = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask() with { LastActivity = Now.AddSeconds(-5) }, null,
            new TaskRunActivity { Kind = TaskRunActivityKinds.NoActiveRun },
            new RunLeaseInspection("none", null), "agent-runner-01", LocalIdentity(), null, Now);

        Assert.Equal(TaskExecutionStates.RemoteRunning, result.State);
        Assert.NotEqual(TaskExecutionStates.Recovering, result.State);
        Assert.Equal("remote", result.ExecutionKind);
        Assert.Equal("connected", result.ConnectionState);
        Assert.Equal("agent-runner-01", result.RunnerId);
    }

    // AGT-2869: the same ownerless, remote-routed task once the replay has
    // stopped is a phantom, not a live run. It stays its remote runner's card
    // (never the sticky "Recovering" badge), but the state is remote-stale and
    // the last runner event is named, so an operator can tell it apart from a
    // host that is still working. This is the exact projection AGT-2867 showed
    // as "Host running" for nine minutes while nobody was driving the run.
    [Fact]
    public void ExecutionProjection_ProgressWithoutOwner_StaleRemote_IsStaleNotRunning()
    {
        var result = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask() with { LastActivity = Now.AddMinutes(-30) }, null,
            new TaskRunActivity { Kind = TaskRunActivityKinds.NoActiveRun },
            new RunLeaseInspection("none", null), "agent-runner-01", LocalIdentity(), null, Now);

        Assert.Equal(TaskExecutionStates.RemoteStale, result.State);
        Assert.NotEqual(TaskExecutionStates.RemoteRunning, result.State);
        Assert.NotEqual(TaskExecutionStates.Recovering, result.State);
        Assert.Equal("reconnecting", result.ConnectionState);
        Assert.Contains("no fenced authority", result.LastRunnerEvent);
        Assert.Contains("nothing is driving this run", result.TrustReason);
    }

    // A heartbeat that stopped before its own lease ran out is the runner that
    // is sitting on a result it cannot deliver. It is reported as remote-stale,
    // separately from remote-disconnected, where the lease is still valid and
    // only the link blipped.
    [Fact]
    public void ExecutionProjection_HeartbeatOlderThanItsLease_IsRemoteStale()
    {
        var expired = Lease("agent-runner-01", "agent-runner-01", "linux-01") with
        {
            LastHeartbeatAt = Now.AddMinutes(-9),
        };

        var result = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask(), null, null, new RunLeaseInspection("expired", expired),
            "agent-runner-01", LocalIdentity(), Now.AddMinutes(-9), Now);

        Assert.Equal(TaskExecutionStates.RemoteStale, result.State);
        Assert.Equal("disconnected", result.ConnectionState);
        Assert.Equal("expired", result.LeaseState);
        Assert.Contains("Last runner event", result.LastRunnerEvent);
        Assert.Contains("no authority is driving it", result.TrustReason);
    }

    [Fact]
    public void ExecutionProjection_LiveRemoteRun_NamesNoRunnerEvent()
    {
        var lease = Lease("agent-runner-02", "runner two", "linux-02") with { LastHeartbeatAt = Now.AddSeconds(-5) };

        var result = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask(), null, null, new RunLeaseInspection("active", lease),
            "agent-runner-01", LocalIdentity(), Now.AddSeconds(-4), Now);

        Assert.Equal(TaskExecutionStates.RemoteRunning, result.State);
        Assert.Null(result.LastRunnerEvent);
    }

    // A locally-routed Progress task with no lease, no live process, and no
    // fresh activity is genuinely orphaned - the one case that still reads as
    // Recovering. Fresh local output heals it back to local-running.
    [Fact]
    public void ExecutionProjection_ProgressLocalOrphan_IsRecovering_ButFreshOutputHeals()
    {
        var recovering = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask() with { LastActivity = Now.AddMinutes(-30) }, null,
            new TaskRunActivity { Kind = TaskRunActivityKinds.NoActiveRun },
            new RunLeaseInspection("none", null), configuredRunnerId: null, LocalIdentity(), null, Now);
        var healed = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask() with { LastActivity = Now.AddSeconds(-5) }, null,
            new TaskRunActivity { Kind = TaskRunActivityKinds.NoActiveRun },
            new RunLeaseInspection("none", null), configuredRunnerId: null, LocalIdentity(), null, Now);

        Assert.Equal(TaskExecutionStates.Recovering, recovering.State);
        Assert.Equal(TaskExecutionStates.LocalRunning, healed.State);
        Assert.Equal("connected", healed.ConnectionState);
    }

    [Fact]
    public void ExecutionProjection_ReadyRemote_IsQueued()
    {
        var queued = TaskRunnerService.ProjectExecutionLocation(
            ProgressTask() with { State = TaskStates.Ready }, null, null,
            new RunLeaseInspection("none", null), "agent-runner-01", LocalIdentity(), null, Now);

        Assert.Equal(TaskExecutionStates.QueuedRemote, queued.State);
    }

    [Fact]
    public void ExecutionProjection_ReadyRemote_ProjectsCurrentRejectionOnly()
    {
        var current = new RemoteDispatchRejection
        {
            Code = "repository-url-missing",
            RunnerId = "agent-runner-01",
            RunnerName = "agent-runner-01",
            Reason = "project has no repositoryUrl",
            RejectedAtUtc = Now.AddMinutes(-1),
        };
        var task = ProgressTask() with
        {
            State = TaskStates.Ready,
            EnteredLaneAt = Now.AddMinutes(-2),
            RemoteDispatchRejection = current,
        };

        var projected = TaskRunnerService.ProjectExecutionLocation(
            task, null, null, new RunLeaseInspection("none", null),
            "agent-runner-01", LocalIdentity(), null, Now);
        var staleGeneration = TaskRunnerService.ProjectExecutionLocation(
            task with { EnteredLaneAt = Now }, null, null,
            new RunLeaseInspection("none", null),
            "agent-runner-01", LocalIdentity(), null, Now);

        Assert.Equal(current, projected.LastRejection);
        Assert.Null(staleGeneration.LastRejection);
    }

    private static RunLeaseInfoDto Lease(string runnerId, string runnerName, string host)
        => new(
            TaskKey: "PT-578",
            RunnerId: runnerId,
            RunnerName: runnerName,
            Hostname: host,
            Pid: 4321,
            BackendName: "remote",
            LeaseId: "lease-abc",
            FencingToken: 7,
            AcquiredAt: new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
            ExpiresAt: new DateTime(2026, 7, 9, 10, 2, 0, DateTimeKind.Utc));

    private static TaskInfo ProgressTask() => new()
    {
        Id = "task-1",
        TaskKey = "PT-578",
        ProjectName = "demo",
        State = TaskStates.Progress,
        FolderPath = "/worktrees/PT-578",
        LastActivity = Now.AddSeconds(-10),
        SessionName = "session-safe",
        Provenance = new TaskProvenance { Branch = "task/PT-578" },
    };

    private static RunnerIdentity LocalIdentity()
        => new("stable@local", "Local", "local-host", "stable", "token", RunnerIdentity.CurrentProtocolVersion);
}
