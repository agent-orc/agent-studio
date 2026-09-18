using System.Diagnostics;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2870: the guard that stands between the runner and <c>kill</c>.
///
/// <para>On 18.09.2026 at 09:41:44 every process of uid 1000 on the runner host
/// was SIGKILLed in one second, because <c>DurableAgentProcess.Attach</c>
/// substitutes <c>-1</c> for a slot without a recorded worker identity and
/// <c>Process.Kill(entireProcessTree: true)</c> turns that into
/// <c>kill(-1, SIGKILL)</c>. Every case here is a decision over values, so the
/// whole matrix runs without a single signal being sent; the two behavioural
/// tests at the end only ever spawn and observe a process this suite started
/// itself.</para>
/// </summary>
public sealed class ProcessSignalGuardTests
{
    private const int LiveLookingPid = 4242;

    // ---- pid floor -----------------------------------------------------------

    /// <summary>
    /// The exact values the incident turned on. <c>-1</c> is "every process this
    /// uid may signal", <c>0</c> is "the caller's own process group",
    /// <c>default(int)</c> is what an unfilled fixture field carries, and
    /// <c>1</c> is init.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    public void A_broadcast_or_init_pid_is_never_signallable(int pid)
    {
        var verdict = ProcessSignalPolicy.ForPid(pid, ownPid: 900, ownParentPid: 800);

        Assert.Equal(SignalRefusal.NotASignallablePid, verdict.Refusal);
        Assert.False(verdict.IsAllowed);
    }

    [Fact]
    public void A_default_initialised_pid_field_is_refused_like_an_explicit_zero()
    {
        var unrecorded = default(int);

        Assert.Equal(
            SignalRefusal.NotASignallablePid,
            ProcessSignalPolicy.ForPid(unrecorded, ownPid: 900, ownParentPid: 800).Refusal);
    }

    [Fact]
    public void An_ordinary_pid_is_allowed()
        => Assert.True(
            ProcessSignalPolicy.ForPid(LiveLookingPid, ownPid: 900, ownParentPid: 800).IsAllowed);

    // ---- self and ancestor ---------------------------------------------------

    [Fact]
    public void The_daemon_never_signals_itself()
    {
        var verdict = ProcessSignalPolicy.ForPid(900, ownPid: 900, ownParentPid: 800);

        Assert.Equal(SignalRefusal.SelfOrAncestor, verdict.Refusal);
    }

    /// <summary>
    /// The parent is systemd's per-unit supervisor or the shell that started a
    /// foreground daemon. Killing it takes the whole unit down with the worker.
    /// </summary>
    [Fact]
    public void The_daemon_never_signals_the_process_that_started_it()
    {
        var verdict = ProcessSignalPolicy.ForPid(800, ownPid: 900, ownParentPid: 800);

        Assert.Equal(SignalRefusal.SelfOrAncestor, verdict.Refusal);
    }

    // ---- recorded identity ---------------------------------------------------

    /// <summary>
    /// A worker that died frees its pid number, and the kernel hands the number
    /// out again. Without the start-time proof the runner kills whatever was
    /// unlucky enough to be issued the number of a worker it lost.
    /// </summary>
    [Fact]
    public void A_pid_whose_start_time_disagrees_with_the_slot_is_refused()
    {
        var recorded = new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc);

        var verdict = ProcessSignalPolicy.ForPid(
            LiveLookingPid,
            ownPid: 900,
            ownParentPid: 800,
            expectedStartUtc: recorded,
            observedStartUtc: recorded.AddMinutes(9));

        Assert.Equal(SignalRefusal.IdentityMismatch, verdict.Refusal);
    }

    /// <summary>
    /// Same tolerance the liveness check already uses, so a worker cannot be
    /// proven live by one rule and refused by the other.
    /// </summary>
    [Fact]
    public void A_start_time_inside_the_liveness_tolerance_is_the_same_process()
    {
        var recorded = new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc);

        Assert.True(ProcessSignalPolicy.ForPid(
            LiveLookingPid,
            ownPid: 900,
            ownParentPid: 800,
            expectedStartUtc: recorded,
            observedStartUtc: recorded.Add(ProcessSignalPolicy.StartTimeTolerance)).IsAllowed);
    }

    [Fact]
    public void A_recorded_identity_that_cannot_be_read_back_is_refused_rather_than_assumed()
    {
        var verdict = ProcessSignalPolicy.ForPid(
            LiveLookingPid,
            ownPid: 900,
            ownParentPid: 800,
            expectedStartUtc: new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc),
            observedStartUtc: null);

        Assert.Equal(SignalRefusal.IdentityMismatch, verdict.Refusal);
    }

    /// <summary>
    /// A /proc sweep has no recorded identity to compare against, and must still
    /// be able to reap. The pid floor and the self check carry that case.
    /// </summary>
    [Fact]
    public void A_sweep_without_a_recorded_identity_may_still_signal()
        => Assert.True(ProcessSignalPolicy.ForPid(
            LiveLookingPid,
            ownPid: 900,
            ownParentPid: 800,
            expectedStartUtc: null,
            observedStartUtc: null).IsAllowed);

    // ---- process groups ------------------------------------------------------

    /// <summary>
    /// <c>kill</c> reads a negative pid as a group broadcast, so a pgid of 1
    /// reproduces the incident through the other door.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void A_process_group_below_two_is_never_signallable(int pgid)
        => Assert.Equal(
            SignalRefusal.NotASignallableProcessGroup,
            ProcessSignalPolicy.ForProcessGroup(pgid, ownProcessGroupId: 900).Refusal);

    [Fact]
    public void The_daemons_own_process_group_is_never_signallable()
        => Assert.Equal(
            SignalRefusal.NotASignallableProcessGroup,
            ProcessSignalPolicy.ForProcessGroup(900, ownProcessGroupId: 900).Refusal);

    [Fact]
    public void A_foreign_process_group_is_signallable()
        => Assert.True(ProcessSignalPolicy.ForProcessGroup(1234, ownProcessGroupId: 900).IsAllowed);

    // ---- cgroup paths --------------------------------------------------------

    /// <summary>
    /// The cgroup path is read from a marker file, so it is input. Writing
    /// <c>cgroup.kill</c> to the unit root empties the daemon's own slice.
    /// </summary>
    [Theory]
    [InlineData("/sys/fs/cgroup/system.slice/agent-runner.service")]
    [InlineData("/sys/fs/cgroup/system.slice/agent-runner.service/daemon")]
    [InlineData("/sys/fs/cgroup")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_a_worker_cgroup_may_be_emptied(string? candidate)
        => Assert.Equal(
            SignalRefusal.ForeignCgroup,
            ProcessSignalPolicy.ForCgroup(
                candidate,
                delegationRoot: "/sys/fs/cgroup/system.slice/agent-runner.service").Refusal);

    [Fact]
    public void A_worker_cgroup_of_another_unit_is_refused()
        => Assert.Equal(
            SignalRefusal.ForeignCgroup,
            ProcessSignalPolicy.ForCgroup(
                "/sys/fs/cgroup/system.slice/agent-runner-review.service/worker-AGT-1",
                delegationRoot: "/sys/fs/cgroup/system.slice/agent-runner.service").Refusal);

    [Fact]
    public void A_worker_cgroup_below_this_daemons_root_is_allowed()
        => Assert.True(ProcessSignalPolicy.ForCgroup(
            "/sys/fs/cgroup/system.slice/agent-runner.service/worker-AGT-2870-1",
            delegationRoot: "/sys/fs/cgroup/system.slice/agent-runner.service").IsAllowed);

    /// <summary>
    /// A host without a delegated subtree resolves no root. The structural rule
    /// still applies, so an unresolved root cannot widen what may be emptied.
    /// </summary>
    [Fact]
    public void Without_a_resolved_root_the_worker_prefix_still_decides()
    {
        Assert.True(ProcessSignalPolicy.ForCgroup("/tmp/fake/worker-x", delegationRoot: null).IsAllowed);
        Assert.Equal(
            SignalRefusal.ForeignCgroup,
            ProcessSignalPolicy.ForCgroup("/tmp/fake", delegationRoot: null).Refusal);
    }

    // ---- the incident, end to end -------------------------------------------

    /// <summary>
    /// The regression this card exists for. A slot whose worker identity was
    /// never persisted - the claim-to-Process.Start window an operator stop
    /// races into - must produce a logged refusal, not a host-wide SIGKILL.
    /// Nothing is signalled: the assertion is on the refusal, and the sleeping
    /// process this test started itself is alive afterwards as the proof that
    /// no broadcast went out.
    /// </summary>
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void A_slot_without_a_recorded_worker_identity_kills_nothing()
    {
        PlatformGate.RequiresPosixShell();
        using var bystander = Process.Start(new ProcessStartInfo(PosixShell.RequirePath())
        {
            ArgumentList = { "-c", "sleep 30" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var workerDirectory = Path.Combine(Path.GetTempPath(), $"agt2870-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workerDirectory);
        var log = new List<string>();
        try
        {
            var slot = SlotWithoutIdentity(workerDirectory);

            // This is verbatim the call RemoteTaskRunner.StopRequestedAsync makes.
            DurableAgentProcess.Attach(slot).Kill(log.Add);

            Assert.Contains(log, line => line.Contains("signal-refused", StringComparison.Ordinal));
            Assert.Contains(
                log,
                line => line.Contains(nameof(SignalRefusal.NotASignallablePid), StringComparison.Ordinal));
            Assert.False(bystander.HasExited);
        }
        finally
        {
            // Only ever the process this test started.
            try { if (!bystander.HasExited) bystander.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            Directory.Delete(workerDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The same slot, put through the cgroup half of the stop path: a marker that
    /// names a directory which is not a <c>worker-*</c> cgroup is refused, and
    /// nothing in it is signalled.
    /// </summary>
    [Fact]
    public void A_marker_that_names_a_foreign_cgroup_is_refused_before_anything_is_signalled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agt2870-marker-{Guid.NewGuid():N}");
        var workerDirectory = Path.Combine(root, "worker");
        var foreignCgroup = Path.Combine(root, "agent-runner.service");
        Directory.CreateDirectory(workerDirectory);
        Directory.CreateDirectory(foreignCgroup);
        var log = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(workerDirectory, "cgroup.path"), foreignCgroup);
            // A resident that would be signalled if the path were followed. The
            // pid is this test process, which the guard must never reach.
            File.WriteAllText(
                Path.Combine(foreignCgroup, "cgroup.procs"),
                $"{Environment.ProcessId}\n");

            var killed = WorkerCgroup.ReleaseFor(workerDirectory, log.Add);

            Assert.Equal(0, killed);
            Assert.Contains(
                log,
                line => line.Contains(nameof(SignalRefusal.ForeignCgroup), StringComparison.Ordinal));
            Assert.True(Directory.Exists(foreignCgroup));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static PersistedRunnerSlot SlotWithoutIdentity(string workerDirectory)
        => new(
            "AGT-2870",
            "attempt-no-identity",
            new RunLeaseInfoDto(
                "AGT-2870",
                "agent-runner-01",
                "agent-runner-01",
                "runner-host",
                Environment.ProcessId,
                "backend",
                "lease-no-identity",
                2,
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(5),
                "attempt-no-identity",
                1),
            RunId: null,
            LeaseInstanceId: null,
            ProjectId: null,
            RepositoryUrl: null,
            DefaultBranch: null,
            TaskKind: "task",
            WorktreePath: workerDirectory,
            WorkerDirectory: workerDirectory,
            // The field the incident turned on: null here, -1 after Attach.
            ProcessId: null,
            ProcessStartedAtUtc: null,
            LastOutputSequence: 0,
            Phase: "working",
            UpdatedAtUtc: DateTime.UtcNow);
}
