using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentRunner;

/// <summary>
/// Process truth owned by one runner daemon. Snapshots are safe to send on
/// every claim poll and lease heartbeat. Linux snapshots also enforce the hard
/// hygiene invariant that no tracked child may keep a deleted cwd.
/// </summary>
public sealed class RunnerProcessInventoryTracker
{
    private readonly ConcurrentDictionary<string, MutableProcess> _processes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunnerInvariantReport> _reports =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _acknowledgedActions =
        new(StringComparer.Ordinal);
    private readonly Func<int, string?> _cwdResolver;
    private readonly Action<int> _kill;
    private readonly Func<DateTime> _utcNow;

    public RunnerProcessInventoryTracker(
        Func<int, string?>? cwdResolver = null,
        Action<int>? kill = null,
        Func<DateTime>? utcNow = null)
    {
        _cwdResolver = cwdResolver ?? ResolveCwd;
        _kill = kill ?? KillProcessTree;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public IDisposable Track(string runId, string taskKey, string cwd)
    {
        var process = new MutableProcess(runId, taskKey, 0, cwd, _utcNow());
        _processes[runId] = process;
        return new Registration(this, runId);
    }

    public void AttachProcess(string runId, int pid)
    {
        if (_processes.TryGetValue(runId, out var process))
            process.Pid = pid;
    }

    public RunnerProcessInventory Snapshot()
    {
        ReapDeletedCwds();
        return new RunnerProcessInventory(
            _utcNow(),
            _processes.Values
                .OrderBy(process => process.RunId, StringComparer.Ordinal)
                .Select(process => new RunnerProcessInfo(
                    process.RunId,
                    process.TaskKey,
                    process.Pid,
                    process.Cwd,
                    process.StartedAt))
                .ToArray(),
            _reports.Values.OrderBy(report => report.DetectedAt).ToArray(),
            _acknowledgedActions.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    public void Apply(IReadOnlyList<RunnerReconciliationAction>? actions)
    {
        if (actions is null) return;
        foreach (var action in actions)
        {
            if (string.Equals(action.Action, "terminate-process", StringComparison.Ordinal)
                && action.Pid is > 0)
            {
                if (string.IsNullOrWhiteSpace(action.RunId)
                    || !_processes.TryGetValue(action.RunId, out var process)
                    || process.Pid != action.Pid.Value)
                {
                    // The run already ended or the PID was reused. Acknowledge the
                    // stale action without touching an unrelated process.
                    _acknowledgedActions.TryAdd(action.ActionId, 0);
                    continue;
                }

                if (!process.TryBeginTermination())
                    continue;
                try
                {
                    _kill(action.Pid.Value);
                    _processes.TryRemove(action.RunId, out _);
                    Report(new RunnerInvariantReport(
                        $"{action.ActionId}-applied",
                        action.Category,
                        _utcNow(),
                        "terminated-orphan-process",
                        $"Applied {action.ActionId}: {action.Detail}",
                        action.RunId,
                        action.TaskKey,
                        action.Pid));
                }
                catch
                {
                    // Do not acknowledge a failed termination. The server will
                    // return the idempotent action again.
                    process.EndTermination();
                    continue;
                }
            }
            _acknowledgedActions.TryAdd(action.ActionId, 0);
        }
    }

    public void Report(RunnerInvariantReport report) =>
        _reports[report.ReportId] = report;

    public void AcknowledgeReports(RunnerProcessInventory snapshot)
    {
        foreach (var report in snapshot.Reports ?? [])
            _reports.TryRemove(report.ReportId, out _);
        foreach (var actionId in snapshot.AcknowledgedActionIds ?? [])
            _acknowledgedActions.TryRemove(actionId, out _);
    }

    private void ReapDeletedCwds()
    {
        foreach (var process in _processes.Values)
        {
            if (process.Pid <= 0) continue;
            string? actualCwd;
            try { actualCwd = _cwdResolver(process.Pid); }
            catch { continue; }
            if (actualCwd is null
                || !actualCwd.EndsWith(" (deleted)", StringComparison.Ordinal))
                continue;

            if (!process.TryBeginTermination())
                continue;
            try
            {
                _kill(process.Pid);
                _processes.TryRemove(process.RunId, out _);
                Report(new RunnerInvariantReport(
                    $"inv_deleted_cwd_{process.RunId}_{process.Pid}",
                    "worktree-hygiene",
                    _utcNow(),
                    "terminated-deleted-cwd",
                    $"Terminated pid {process.Pid} because its cwd was deleted: {actualCwd}",
                    process.RunId,
                    process.TaskKey,
                    process.Pid));
            }
            catch (Exception exception)
            {
                process.EndTermination();
                Report(new RunnerInvariantReport(
                    $"inv_deleted_cwd_{process.RunId}_{process.Pid}",
                    "worktree-hygiene",
                    _utcNow(),
                    "termination-failed",
                    $"Failed to terminate pid {process.Pid} after its cwd was deleted: {exception.Message}",
                    process.RunId,
                    process.TaskKey,
                    process.Pid));
            }
        }
    }

    private static string? ResolveCwd(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var info = new FileInfo($"/proc/{pid}/cwd");
        return info.LinkTarget;
    }

    /// <summary>
    /// AGT-2870: the pid comes from a registration that may have been recorded
    /// before the process existed, so the shared guard refuses the broadcast pids
    /// rather than handing them to the runtime, which admits them.
    /// </summary>
    private static void KillProcessTree(int pid)
        => ProcessSignalGuard.TryKillTree(pid, $"run-inventory pid={pid}");

    private void Remove(string runId) => _processes.TryRemove(runId, out _);

    private sealed class Registration(RunnerProcessInventoryTracker owner, string runId) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Remove(runId);
        }
    }

    private sealed record MutableProcess(
        string RunId,
        string TaskKey,
        int InitialPid,
        string Cwd,
        DateTime StartedAt)
    {
        private int _terminating;

        public int Pid { get; set; } = InitialPid;

        public bool TryBeginTermination() =>
            Interlocked.CompareExchange(ref _terminating, 1, 0) == 0;

        public void EndTermination() =>
            Interlocked.Exchange(ref _terminating, 0);
    }
}
