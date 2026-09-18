using System.Diagnostics;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableAgentProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-restart-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Replacement_daemon_reattaches_live_fake_job_and_reads_its_terminal_result()
    {
        var worktree = Path.Combine(_root, "worktree");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var releasePath = Path.Combine(results, "fake-job-release");
        var options = Options(stateRoot, worktree,
            WaitForReleaseCommand(releasePath, "before-restart\\n[[TASK_DONE]]\\n"),
            runTimeoutSeconds: 60);
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

        DetachedJobResult? result = null;
        try
        {
            // One deadline covers the complete handoff. The fake job remains alive
            // until the replacement has observed every durable prerequisite, so host
            // scheduling can delay a condition without making the condition vanish.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(
                () => File.Exists(original.IdentityPath),
                "the detached worker to publish its handoff identity",
                deadline.Token);

            // A replacement store/handle has no Process object or inherited pipe
            // from the starter. It can adopt only from the durable PID + cwd proof.
            var replacementStore = new RunnerStateStore(stateRoot);
            var recovered = Assert.Single(replacementStore.LoadAll());
            var proof = string.Empty;
            await WaitUntilAsync(
                () => DurableAgentProcess.VerifyLive(recovered, out proof),
                "the replacement daemon to verify the persisted process generation",
                deadline.Token);
            var attached = DurableAgentProcess.Attach(recovered);
            await WaitUntilAsync(
                () => attached.ReadAfter(0).Any(line => line.Text == "fake-job-ready"),
                "the fake job to publish its readiness marker",
                deadline.Token);

            await File.WriteAllTextAsync(releasePath, "continue", deadline.Token);
            result = await WaitForResultAsync(attached, deadline.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("[[TASK_DONE]]", result.StdOut);
            Assert.Contains(attached.ReadAfter(0), line => line.Text == "before-restart");
            Assert.Contains(attached.ReadAfter(0), line => line.Text == "[[TASK_DONE]]");
        }
        finally
        {
            if (result is null) original.Kill();
        }
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
    [Trait("Category", "MachineBound")]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void Pid_with_a_different_worktree_is_not_adopted()
    {
        PlatformGate.LinuxOnly("the worktree proof reads /proc/<pid>/cwd");

        var actual = Path.Combine(_root, "actual");
        var claimed = Path.Combine(_root, "claimed");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(actual);
        Directory.CreateDirectory(claimed);
        Directory.CreateDirectory(results);
        var releasePath = Path.Combine(results, "fake-job-release");
        var options = Options(
            stateRoot,
            actual,
            WaitForReleaseCommand(releasePath, "done\\n"),
            runTimeoutSeconds: 60);
        var lease = Lease("AGT-MISMATCH");
        var store = new RunnerStateStore(stateRoot);
        var slot = store.Create(lease.TaskKey, lease, claimed);
        var process = DurableAgentProcess.Start(options, slot.WorkerDirectory, actual, "", results);
        try
        {
            slot = store.Save(slot with
            {
                ProcessId = process.ProcessId,
                ProcessStartedAtUtc = process.ProcessStartedAtUtc,
                Phase = "running",
            });

            Assert.False(DurableAgentProcess.VerifyLive(slot, out var reason));
            Assert.Contains("does not match worktree", reason);
        }
        finally
        {
            process.Kill();
        }
    }

    // This used to poll 400x5ms against a real "sleep 1" process, so it failed
    // under load on otherwise untouched trees. The worker now stays alive until
    // the observable identity condition is proven or the shared deadline expires.
    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Worker_identity_closes_the_process_start_to_slot_save_restart_window()
    {
        var worktree = Path.Combine(_root, "launch-window");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var releasePath = Path.Combine(results, "fake-job-release");
        var options = Options(
            stateRoot,
            worktree,
            WaitForReleaseCommand(releasePath, "done\\n"),
            runTimeoutSeconds: 60);
        var lease = Lease("AGT-LAUNCH-WINDOW");
        var firstStore = new RunnerStateStore(stateRoot);
        var slot = firstStore.Create(lease.TaskKey, lease, worktree);
        slot = firstStore.Save(slot with { Phase = "launching" });

        var worker = DurableAgentProcess.Start(
            options, slot.WorkerDirectory, worktree, "", results);
        try
        {
            var replacementSlot = Assert.Single(new RunnerStateStore(stateRoot).LoadAll());

            PersistedRunnerSlot recovered = replacementSlot;
            var reason = string.Empty;
            var identityProven = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(
                () => identityProven = DurableAgentProcess.TryRecoverIdentity(
                    replacementSlot,
                    out recovered,
                    out reason),
                "the detached worker identity to become recoverable",
                deadline.Token);

            // TryRecoverIdentity returns true only after it has verified the live PID
            // generation, so this single assertion covers both halves of the contract.
            Assert.True(identityProven, reason);
            Assert.Equal(worker.ProcessId, recovered.ProcessId);
            Assert.InRange(
                Math.Abs((worker.ProcessStartedAtUtc - recovered.ProcessStartedAtUtc!.Value).TotalSeconds),
                0,
                2);
        }
        finally
        {
            worker.Kill();
        }
    }

    private static RunnerOptions Options(
        string stateRoot,
        string worktree,
        string cliArgs,
        int runTimeoutSeconds = 10) => new()
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
        RunTimeoutSeconds = runTimeoutSeconds,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private static string WaitForReleaseCommand(string releasePath, string terminalOutput)
    {
        var shellReleasePath = PosixShell.ToShellPath(releasePath).Replace("'", "'\"'\"'");
        return $"-c \"printf 'fake-job-ready\\n'; "
               + $"while [ ! -f '{shellReleasePath}' ]; do sleep 0.05; done; "
               + $"printf '{terminalOutput}'\"";
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!condition())
                await Task.Delay(50, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description}.");
        }
    }

    private static async Task<DetachedJobResult> WaitForResultAsync(
        DurableAgentProcess process,
        CancellationToken cancellationToken)
    {
        DetachedJobResult? result = null;
        await WaitUntilAsync(
            () => (result = process.ReadResult()) is not null,
            "the detached worker to atomically publish its complete terminal result",
            cancellationToken);
        return result!;
    }

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
