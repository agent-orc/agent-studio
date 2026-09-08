using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Load-bearing containment contract for AGT-2759: a review worker that dies
/// without a live daemon watching it must not leave its CLI child (claude,
/// codex) running forever. <see cref="CliProcessReaper"/> and
/// <see cref="CliOrphanSweep"/> are the two nets - per-attempt reap at cleanup
/// time, and a host-wide sweep for whatever slips past it.
/// </summary>
public sealed class CliProcessReaperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"cli-process-reaper-tests-{Guid.NewGuid():N}");

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task ReapWorkspaceAsync_kills_a_long_lived_child_and_logs_pid_age_and_attempt()
    {
        PlatformGate.LinuxOnly("the reaper scans /proc/<pid>/cwd");

        var workspace = Path.Combine(_root, "review-attempt-1", "repository");
        Directory.CreateDirectory(workspace);
        var logs = new List<string>();

        var processTask = ProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "sleep 300 & wait"],
            workingDirectory: workspace,
            isolateProcessGroup: true);
        for (var attempt = 0;
             attempt < 100 && WorktreeProcessReaper.FindByCwd(workspace).Count == 0;
             attempt++)
            await Task.Delay(20);
        Assert.NotEmpty(WorktreeProcessReaper.FindByCwd(workspace));

        var before = CliProcessReaper.ReapedCount;
        var reaped = await CliProcessReaper.ReapWorkspaceAsync(
            workspace, "review-attempt-1", logs.Add, CancellationToken.None);
        var process = await processTask;

        Assert.True(reaped > 0, "expected at least the sleep child to be reaped");
        Assert.NotEqual(0, process.ExitCode);
        Assert.Empty(WorktreeProcessReaper.FindByCwd(workspace));
        Assert.Equal(before + reaped, CliProcessReaper.ReapedCount);
        var reapedLines = logs.Where(line => line.StartsWith("cli-process-reaped", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(reapedLines);
        Assert.All(reapedLines, line =>
        {
            Assert.Contains("attempt=review-attempt-1", line, StringComparison.Ordinal);
            Assert.Contains("pid=", line, StringComparison.Ordinal);
            Assert.Contains("age=", line, StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task ReapWorkspaceAsync_is_a_no_op_when_nothing_lives_in_the_workspace()
    {
        PlatformGate.LinuxOnly("the reaper scans /proc/<pid>/cwd");

        var workspace = Path.Combine(_root, "review-attempt-empty", "repository");
        Directory.CreateDirectory(workspace);
        var logs = new List<string>();

        var reaped = await CliProcessReaper.ReapWorkspaceAsync(
            workspace, "review-attempt-empty", logs.Add, CancellationToken.None);

        Assert.Equal(0, reaped);
        Assert.Empty(logs);
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Reconciler_purge_of_a_dead_worker_reaps_its_orphaned_child_before_deleting_state()
    {
        PlatformGate.LinuxOnly("the reaper scans /proc/<pid>/cwd");

        var state = new ReviewStateStore(_root);
        var now = DateTime.UtcNow;
        var workspace = Path.Combine(_root, "work", "review-orphaned", "repository");
        Directory.CreateDirectory(workspace);
        state.Save(state.Create(
            Claim("review-orphaned", now, now.AddHours(1)),
            workspace) with
        {
            ProcessId = 999_999, // a worker PID that no longer exists
            ProcessStartedAtUtc = now.AddMinutes(-10),
            Phase = "running",
        });

        var processTask = ProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "sleep 300 & wait"],
            workingDirectory: workspace,
            isolateProcessGroup: true);
        for (var attempt = 0;
             attempt < 100 && WorktreeProcessReaper.FindByCwd(workspace).Count == 0;
             attempt++)
            await Task.Delay(20);
        Assert.NotEmpty(WorktreeProcessReaper.FindByCwd(workspace));

        var logs = new List<string>();
        var reconciler = new ReviewSlotReconciler(
            state,
            (_, _) => Task.FromResult<ReviewAttemptDto?>(null), // no server authority -> PurgeInvalidAuthority
            _ => new ReviewProcessObservation(false, "worker no longer exists"),
            log: logs.Add);

        var result = await reconciler.ReconcileAsync(
            new HashSet<string>(StringComparer.Ordinal),
            now,
            CancellationToken.None);
        var process = await processTask;

        Assert.Equal(1, result.Purged);
        Assert.Empty(state.LoadAll());
        Assert.NotEqual(0, process.ExitCode);
        Assert.Empty(WorktreeProcessReaper.FindByCwd(workspace));
        Assert.Contains(logs, line => line.StartsWith("cli-process-reaped", StringComparison.Ordinal));
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task OrphanSweep_kills_a_tracked_binary_process_older_than_the_review_budget()
    {
        PlatformGate.LinuxOnly("the sweep scans /proc for comm and cwd");

        var root = Path.Combine(_root, "review-work");
        Directory.CreateDirectory(root);
        var claudeBin = FakeCliBinary(root, "claude");
        var logs = new List<string>();

        var processTask = ProcessRunner.RunAsync(claudeBin, ["300"], workingDirectory: root);
        await WaitForCommAsync(root, "claude");

        var reaped = CliOrphanSweep.Sweep([root], TimeSpan.Zero, logs.Add);
        var process = await processTask;

        Assert.Equal(1, reaped);
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(logs, line =>
            line.StartsWith("cli-process-reaped", StringComparison.Ordinal)
            && line.Contains("attempt=orphan", StringComparison.Ordinal)
            && line.Contains("review budget", StringComparison.Ordinal));
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task OrphanSweep_kills_a_tracked_binary_process_whose_cwd_was_already_deleted()
    {
        PlatformGate.LinuxOnly("the sweep scans /proc for comm and cwd");

        var root = Path.Combine(_root, "review-work-2");
        var deletedCwd = Path.Combine(root, "review-attempt-gone", "repository");
        Directory.CreateDirectory(deletedCwd);
        var claudeBin = FakeCliBinary(root, "claude");
        var logs = new List<string>();

        var processTask = ProcessRunner.RunAsync(claudeBin, ["300"], workingDirectory: deletedCwd);
        await WaitForCommAsync(root, "claude");
        Directory.Delete(Path.Combine(root, "review-attempt-gone"), recursive: true);

        // A never-aging budget proves the deleted-cwd path fires independently
        // of process age.
        var reaped = CliOrphanSweep.Sweep([root], TimeSpan.FromDays(365), logs.Add);
        var process = await processTask;

        Assert.Equal(1, reaped);
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(logs, line =>
            line.StartsWith("cli-process-reaped", StringComparison.Ordinal)
            && line.Contains("cwd was already removed", StringComparison.Ordinal));
    }

    /// <summary>
    /// /proc/&lt;pid&gt;/comm reflects the basename of the path passed to
    /// execve, not argv[0]. A symlink named "claude" pointing at the real
    /// `sleep` binary and invoked by that symlink path is enough to produce a
    /// process CliOrphanSweep classifies as a tracked CLI, without needing the
    /// real Claude CLI installed.
    /// </summary>
    private string FakeCliBinary(string root, string name)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        if (!File.Exists(path)) File.CreateSymbolicLink(path, "/bin/sleep");
        return path;
    }

    private static async Task WaitForCommAsync(string root, string comm)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (WorktreeProcessReaper.FindByCwd(root).Any(process => HasComm(process.Pid, comm)))
                return;
            await Task.Delay(20);
        }
        Assert.Fail($"no process with comm '{comm}' appeared under {root} in time");
    }

    private static bool HasComm(int pid, string comm)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim() == comm; }
        catch (IOException) { return false; }
    }

    public void Dispose()
    {
        ResilientDirectory.TryDelete(_root);
    }

    private static ReviewClaimResponse Claim(
        string attemptId,
        DateTime createdAt,
        DateTime expiresAt)
    {
        var subjectId = $"subject-{attemptId}";
        var attempt = new ReviewAttemptDto(
            attemptId,
            subjectId,
            "AGT-2759",
            1,
            "leased",
            "review-runner",
            "review-host",
            7,
            createdAt,
            null,
            null,
            null,
            null);
        var subject = new ReviewSubjectDto(
            subjectId,
            "AGT-2759",
            "run-1",
            "example/repository",
            null,
            new string('a', 40),
            null,
            "bundle",
            new string('b', 64),
            "coding-host",
            "policy-v1",
            new ReviewPlanDto([], []),
            createdAt);
        var lease = new ReviewLeaseDto(
            $"lease-{attemptId}",
            attemptId,
            subjectId,
            "review-runner",
            "instance-1",
            "review-host",
            7,
            createdAt,
            expiresAt,
            "active",
            $"resource-{attemptId}",
            25000,
            11);
        return new ReviewClaimResponse("claimed", attempt, subject, lease);
    }
}
