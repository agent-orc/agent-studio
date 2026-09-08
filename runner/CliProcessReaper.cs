using System.Diagnostics;

namespace AgentRunner;

/// <summary>
/// Best-effort cleanup for CLI child processes an attempt leaves behind once
/// its worker is confirmed not live (terminal report, lease loss, or a purge
/// from reconciliation). <see cref="WorktreeProcessReaper"/> already knows how
/// to find and signal every process rooted at a workspace path; this wraps it
/// with the per-process age/attempt logging and running total the process
/// hygiene contract asks for, and makes the call safe to issue from a path
/// that must still finish (report submission, state cleanup) even when a
/// stuck process refuses to die.
/// </summary>
public static class CliProcessReaper
{
    private static long _reapedCount;

    /// <summary>Total CLI child processes this daemon process has reaped, for host telemetry.</summary>
    public static long ReapedCount => Interlocked.Read(ref _reapedCount);

    /// <summary>Folds a count reaped by another pass (e.g. <see cref="CliOrphanSweep"/>) into the shared total.</summary>
    public static void RecordExternalReap(int count)
    {
        if (count > 0) Interlocked.Add(ref _reapedCount, count);
    }

    public static async Task<int> ReapWorkspaceAsync(
        string workspacePath,
        string attemptId,
        Action<string> log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return 0;

        var now = DateTime.UtcNow;
        var victims = WorktreeProcessReaper.FindByCwd(workspacePath);
        if (victims.Count == 0) return 0;

        var ages = victims.ToDictionary(victim => victim.Pid, victim => Age(victim.Pid, now));
        int reaped;
        try
        {
            reaped = await WorktreeProcessReaper.ReapAsync(workspacePath, log, ct);
        }
        catch (WorktreeProcessException exception)
        {
            log(
                $"cli-process-reap-incomplete attempt={attemptId} " +
                $"survivors={string.Join(',', exception.ProcessIds)}: {exception.Message}");
            return 0;
        }

        foreach (var victim in victims)
        {
            var age = ages[victim.Pid];
            log(
                $"cli-process-reaped pid={victim.Pid} " +
                $"age={(age is { } a ? a.TotalSeconds.ToString("F0") : "unknown")}s " +
                $"attempt={attemptId}");
        }
        Interlocked.Add(ref _reapedCount, reaped);
        return reaped;
    }

    private static TimeSpan? Age(int pid, DateTime now)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return now - process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static void ResetForTests() => Interlocked.Exchange(ref _reapedCount, 0);
}
