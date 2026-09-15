using System.Diagnostics;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableAgentProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-restart-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Replacement_daemon_reattaches_live_fake_job_and_reads_its_terminal_result()
    {
        var worktree = Path.Combine(_root, "worktree");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, worktree,
            "-c \"sleep 1; printf 'before-restart\\n[[TASK_DONE]]\\n'\"");
        var lease = Lease("AGT-RESTART");
        var firstStore = new RunnerStateStore(stateRoot);
        var slot = firstStore.Create(lease.TaskKey, lease, worktree);

        var original = DurableAgentProcess.Start(options, slot.WorkerDirectory, worktree, "", results);
        firstStore.Save(slot with
        {
            ProcessId = original.ProcessId,
            ProcessStartedAtUtc = original.ProcessStartedAtUtc,
            Phase = "running",
        });

        // A replacement store/handle has no Process object or inherited pipe
        // from the starter. It can adopt only from the durable PID + cwd proof.
        var replacementStore = new RunnerStateStore(stateRoot);
        var recovered = Assert.Single(replacementStore.LoadAll());
        Assert.True(DurableAgentProcess.VerifyLive(recovered, out var proof), proof);
        var attached = DurableAgentProcess.Attach(recovered);

        DetachedJobResult? result = null;
        for (var i = 0; i < 40 && result is null; i++)
        {
            await Task.Delay(100);
            result = attached.ReadResult();
        }

        Assert.NotNull(result);
        Assert.Equal(0, result!.ExitCode);
        Assert.Contains("[[TASK_DONE]]", result.StdOut);
        Assert.Contains(attached.ReadAfter(0), line => line.Text == "before-restart");
        Assert.Contains(attached.ReadAfter(0), line => line.Text == "[[TASK_DONE]]");
    }

    /// <summary>
    /// AGT-2834 (coding-run path): the detached worker, and therefore the agent
    /// and every command it runs, must carry the product cache locations the
    /// repository preparation of this claim restored into. Without them the
    /// checkout's assets file points at a package folder the agent cannot see
    /// and its first `dotnet build --no-restore` fails with NETSDK1064.
    /// </summary>
    [Fact]
    public async Task Detached_worker_runs_the_agent_with_the_prepared_cache_locations()
    {
        var worktree = Path.Combine(_root, "prepared-worktree");
        var results = Path.Combine(_root, "prepared-results");
        var stateRoot = Path.Combine(_root, "prepared-state");
        var packages = Path.Combine(_root, "prepared-packages");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        Directory.CreateDirectory(packages);
        var options = Options(stateRoot, worktree,
            "-c \"echo NUGET_PACKAGES=$NUGET_PACKAGES; printf '[[TASK_DONE]]\\n'\"");
        var lease = Lease("AGT-PREPARED-ENV");
        var store = new RunnerStateStore(stateRoot);
        var slot = store.Create(lease.TaskKey, lease, worktree);

        var worker = DurableAgentProcess.Start(
            options, slot.WorkerDirectory, worktree, "", results,
            preparationEnvironment: new Dictionary<string, string> { ["NUGET_PACKAGES"] = packages });

        DetachedJobResult? result = null;
        for (var i = 0; i < 40 && result is null; i++)
        {
            await Task.Delay(100);
            result = worker.ReadResult();
        }

        Assert.NotNull(result);
        Assert.Equal(0, result!.ExitCode);
        Assert.Contains("NUGET_PACKAGES=" + packages, result.StdOut);
        // The spec is the durable record a replacement daemon relaunches from.
        Assert.Contains(
            packages,
            await File.ReadAllTextAsync(Path.Combine(slot.WorkerDirectory, "spec.json")));
    }

    [Fact]
    public void Terminal_result_wins_when_worker_exits_between_discovery_checks()
    {
        var terminal = new DetachedJobResult(
            0,
            "before-restart\n[[TASK_DONE]]\n",
            "",
            false,
            DateTime.UtcNow);
        var resultReads = 0;

        var observation = DurableAgentProcess.InspectForReattach(
            () => ++resultReads == 1 ? null : terminal,
            () => (false, "process has exited"));

        Assert.False(observation.IsLive);
        Assert.Same(terminal, observation.Result);
        Assert.Equal("durable result ready", observation.Detail);
        Assert.Equal(2, resultReads);
    }

    // Linux-only 02.08. (AGT-2472): the worktree half of the adoption proof reads
    // /proc/<pid>/cwd. Windows exposes no equivalent for another process, so
    // DurableAgentProcess.VerifyLive deliberately skips that check there
    // (OperatingSystem.IsLinux() guard) and the rejection under test cannot occur.
    // Gating, not filtering: this reports as Skipped with its reason, so nobody
    // mistakes the Windows run for coverage of the PID-reuse defence.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Pid_with_a_different_worktree_is_not_adopted()
    {
        PlatformGate.LinuxOnly("the worktree proof reads /proc/<pid>/cwd");

        var actual = Path.Combine(_root, "actual");
        var claimed = Path.Combine(_root, "claimed");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(actual);
        Directory.CreateDirectory(claimed);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, actual, "-c \"sleep 2\"");
        var lease = Lease("AGT-MISMATCH");
        var store = new RunnerStateStore(stateRoot);
        var slot = store.Create(lease.TaskKey, lease, claimed);
        var process = DurableAgentProcess.Start(options, slot.WorkerDirectory, actual, "", results);
        slot = store.Save(slot with
        {
            ProcessId = process.ProcessId,
            ProcessStartedAtUtc = process.ProcessStartedAtUtc,
            Phase = "running",
        });

        Assert.False(DurableAgentProcess.VerifyLive(slot, out var reason));
        Assert.Contains("does not match worktree", reason);
        process.Kill();
        await Task.Delay(50);
    }

    // Wall-clock racer: polls 400x5ms against a real "sleep 1" process, so it
    // fails under load on an otherwise untouched tree. It produced the false
    // ProductFailure verdicts on AGT-2457 and AGT-2458, whose diffs do not touch
    // this file. Marked per the AGT-2484 contract so a non-reproducing failure
    // is quarantined instead of blocking; a reproduced failure still blocks.
    [Fact]
    [Trait("Category", "ReviewFlaky")]
    public async Task Worker_identity_closes_the_process_start_to_slot_save_restart_window()
    {
        var worktree = Path.Combine(_root, "launch-window");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, worktree, "-c \"sleep 1; printf 'done\\n'\"");
        var lease = Lease("AGT-LAUNCH-WINDOW");
        var firstStore = new RunnerStateStore(stateRoot);
        var slot = firstStore.Create(lease.TaskKey, lease, worktree);
        slot = firstStore.Save(slot with { Phase = "launching" });

        var worker = DurableAgentProcess.Start(
            options, slot.WorkerDirectory, worktree, "", results);
        var replacementSlot = Assert.Single(new RunnerStateStore(stateRoot).LoadAll());

        PersistedRunnerSlot recovered = replacementSlot;
        var reason = string.Empty;
        var identityProven = false;
        // Poll tightly: recovery is only observable while the worker is alive, and
        // on a host where the faked CLI binary does not exist (Windows, /bin/sh)
        // that window is tens of milliseconds. The contract under test is the
        // recovery itself, so the loop must not be able to step over the window.
        for (var i = 0; i < 400 && !identityProven; i++)
        {
            identityProven = DurableAgentProcess.TryRecoverIdentity(replacementSlot, out recovered, out reason);
            if (!identityProven) await Task.Delay(5);
        }

        // TryRecoverIdentity returns true only after it has verified the live PID
        // generation, so this single assertion covers both halves of the contract.
        // Re-verifying liveness afterwards would race the worker's own exit.
        Assert.True(identityProven, reason);
        Assert.Equal(worker.ProcessId, recovered.ProcessId);
        Assert.InRange(
            Math.Abs((worker.ProcessStartedAtUtc - recovered.ProcessStartedAtUtc!.Value).TotalSeconds),
            0,
            2);
        worker.Kill();
    }

    private static RunnerOptions Options(string stateRoot, string worktree, string cliArgs) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-restart-test",
        RunnerName = "runner-restart-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = worktree,
        StateDir = stateRoot,
        BaseBranch = "main",
        // These tests fake the CLI through CliBin/CliArgs, which only the legacy
        // engine consumes. The worker/reattach mechanics under test are
        // engine-independent; the CAR engine is covered by CarWorkerExecutionTests.
        ExecEngine = RunnerOptions.ExecEngineLegacy,
        // A real interpreter, not the literal "/bin/sh": the worker mechanics under
        // test are portable, and pinning a Unix path made the fake CLI unstartable
        // on Windows, which showed up as an unexplained non-zero exit code.
        CliBin = PosixShell.RequirePath(),
        CliArgs = cliArgs,
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 10,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private static RunLeaseInfoDto Lease(string taskKey) => new(
        taskKey,
        "runner-restart-test",
        "runner-restart-test",
        "test-host",
        Environment.ProcessId,
        "test",
        $"lease-{taskKey}",
        1,
        DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(2));

    public void Dispose()
    {
        // Best effort: a killed test worker may still be unwinding and hold a handle.
        ResilientDirectory.TryDelete(_root);
    }
}
