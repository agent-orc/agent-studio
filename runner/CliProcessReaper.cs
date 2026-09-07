using System.Diagnostics;

namespace AgentRunner;

/// <summary>
/// Bounded cleanup for detached CLI workers after attempt authority ends.
/// Process generation verification prevents a recycled PID from being killed.
/// </summary>
public sealed class CliProcessReaper
{
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumReviewBudget = TimeSpan.FromHours(2);
    public static CliProcessReaper Shared { get; } = new();

    private static long _reapedCount;
    private readonly Func<int, ProcessSnapshot?> _inspect;
    private readonly Func<int, CancellationToken, Task> _kill;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public CliProcessReaper(
        Func<int, ProcessSnapshot?>? inspect = null,
        Func<int, CancellationToken, Task>? kill = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inspect = inspect ?? Inspect;
        _kill = kill ?? KillTreeAsync;
        _delay = delay ?? Task.Delay;
    }

    public static long ReapedCount => Interlocked.Read(ref _reapedCount);

    public async Task<int> ReapAsync(
        int pid,
        DateTime expectedStartedAtUtc,
        string attemptId,
        string workspacePath,
        Action<string> log,
        CancellationToken cancellationToken,
        TimeSpan? gracePeriod = null)
    {
        var grace = gracePeriod ?? DefaultGracePeriod;
        if (grace > TimeSpan.Zero) await _delay(grace, cancellationToken);

        var snapshot = _inspect(pid);
        var reaped = 0;
        if (snapshot is not null
            && Math.Abs((snapshot.StartedAtUtc - expectedStartedAtUtc).TotalSeconds) <= 2)
        {
            await _kill(pid, cancellationToken);
            var age = DateTime.UtcNow - snapshot.StartedAtUtc;
            log(
                $"cli-process-reaped pid={pid} ageSeconds={Math.Max(0, age.TotalSeconds):0} " +
                $"attempt={attemptId}");
            reaped = 1;
        }

        // A provider helper may have daemonized and escaped the worker's child
        // tree. The attempt workspace is the second, bounded ownership proof.
        try
        {
            var escaped = await WorktreeProcessReaper.ReapAsync(
                workspacePath,
                log,
                CancellationToken.None);
            if (escaped > 0)
            {
                log(
                    $"cli-process-reaped pid=workspace-sweep ageSeconds=unknown " +
                    $"attempt={attemptId} count={escaped}");
                reaped += escaped;
            }
        }
        catch (WorktreeProcessException exception)
        {
            log(
                $"cli-process-reap-incomplete attempt={attemptId} " +
                $"pids={string.Join(',', exception.ProcessIds)}");
        }

        Interlocked.Add(ref _reapedCount, reaped);
        return reaped;
    }

    public async Task<int> SweepReviewAsync(
        IEnumerable<PersistedReviewSlot> slots,
        DateTime utcNow,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var slot in slots.Where(slot =>
                     slot.ProcessId is not null
                     && slot.ProcessStartedAtUtc is not null
                     && utcNow - slot.ProcessStartedAtUtc.Value > MaximumReviewBudget))
        {
            total += await ReapAsync(
                slot.ProcessId!.Value,
                slot.ProcessStartedAtUtc!.Value,
                slot.AttemptId,
                slot.WorkspacePath,
                log,
                cancellationToken,
                TimeSpan.Zero);
        }
        return total;
    }

    public async Task<int> SweepCodingAsync(
        IEnumerable<PersistedRunnerSlot> slots,
        DateTime utcNow,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var slot in slots.Where(slot =>
                     slot.ProcessId is not null
                     && slot.ProcessStartedAtUtc is not null
                     && utcNow - slot.ProcessStartedAtUtc.Value > MaximumReviewBudget))
        {
            total += await ReapAsync(
                slot.ProcessId!.Value,
                slot.ProcessStartedAtUtc!.Value,
                slot.AttemptId,
                slot.WorktreePath,
                log,
                cancellationToken,
                TimeSpan.Zero);
        }
        return total;
    }

    private static ProcessSnapshot? Inspect(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited
                ? null
                : new ProcessSnapshot(process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static async Task KillTreeAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
            // The process exited between inspection and cleanup.
        }
    }

    public sealed record ProcessSnapshot(DateTime StartedAtUtc);
}
