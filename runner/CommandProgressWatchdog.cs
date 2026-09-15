using System.Globalization;

namespace AgentRunner;

/// <summary>How a sampled process tree is progressing against its CPU floor.</summary>
public enum CommandProgress
{
    /// <summary>The tree consumed enough CPU to clear the floor; the window restarts.</summary>
    Advanced,

    /// <summary>Below the floor so far, but the no-progress window has not elapsed.</summary>
    Waiting,

    /// <summary>The window elapsed without the tree clearing its CPU floor.</summary>
    Stalled,

    /// <summary>CPU time cannot be read on this host, so no verdict is possible.</summary>
    Unobservable,
}

/// <summary>
/// Pure decision for the hang watchdog. A review verify command that deadlocks on
/// a shared build-server socket keeps running and keeps its slot, but it stops
/// consuming CPU. Wall-clock timeouts cannot tell that apart from a genuinely
/// long build, so the decision is made on consumed CPU time instead.
///
/// The floor is a single budget rather than an instantaneous rate: within any
/// <c>noProgressAfter</c> window the tree must burn at least
/// <c>noProgressAfter * <see cref="MinimumCoreFraction"/></c> of CPU time. A
/// healthy build clears that within seconds; a tree spinning at a tenth of a
/// percent of one core never does.
/// </summary>
public static class CommandProgressPolicy
{
    /// <summary>
    /// Share of one core the tree must average over the window to count as
    /// working. One percent is far below any real compile or test run and far
    /// above the residual poll traffic of a blocked socket read.
    /// </summary>
    public const double MinimumCoreFraction = 0.01;

    public static CommandProgress Evaluate(
        TimeSpan? sampledCpu,
        TimeSpan cpuAtWindowStart,
        TimeSpan sinceWindowStart,
        TimeSpan noProgressAfter,
        double minimumCoreFraction = MinimumCoreFraction)
    {
        if (noProgressAfter <= TimeSpan.Zero) return CommandProgress.Unobservable;
        if (sampledCpu is not { } cpu) return CommandProgress.Unobservable;

        var budget = noProgressAfter * minimumCoreFraction;
        if (cpu - cpuAtWindowStart > budget) return CommandProgress.Advanced;
        return sinceWindowStart >= noProgressAfter
            ? CommandProgress.Stalled
            : CommandProgress.Waiting;
    }
}

/// <summary>
/// Reads cumulative CPU time for a process and all of its descendants from
/// <c>/proc</c>. Linux only: the deadlock this guards against is a Linux review
/// host problem, and no other platform exposes the whole tree cheaply enough to
/// poll. Elsewhere the sampler returns <c>null</c> and the watchdog stays inert.
/// </summary>
public static class ProcessTreeCpu
{
    // /proc/<pid>/stat reports utime and stime in USER_HZ. The kernel pins
    // USER_HZ at 100 for the /proc ABI regardless of the configured kernel HZ,
    // so one tick is always 10 ms of CPU time.
    private const long TicksPerSecond = 100;

    public static TimeSpan? Sample(int rootProcessId)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc")) return null;
        if (rootProcessId <= 0) return null;

        var parents = new Dictionary<int, int>();
        var cpuTicks = new Dictionary<int, long>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            if (!TryReadStat(pid, out var parentPid, out var ticks)) continue;
            parents[pid] = parentPid;
            cpuTicks[pid] = ticks;
        }

        if (!cpuTicks.ContainsKey(rootProcessId)) return null;

        var total = 0L;
        foreach (var (pid, ticks) in cpuTicks)
            if (IsInTree(pid, rootProcessId, parents))
                total += ticks;
        return TimeSpan.FromSeconds((double)total / TicksPerSecond);
    }

    private static bool IsInTree(int pid, int rootProcessId, IReadOnlyDictionary<int, int> parents)
    {
        // Bounded by the number of sampled processes, so a /proc snapshot that
        // races a re-parent can never spin here.
        for (var hops = 0; hops <= parents.Count; hops++)
        {
            if (pid == rootProcessId) return true;
            if (pid <= 1 || !parents.TryGetValue(pid, out var parent)) return false;
            pid = parent;
        }
        return false;
    }

    private static bool TryReadStat(int pid, out int parentPid, out long cpuTicks)
    {
        parentPid = 0;
        cpuTicks = 0;
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The process exited while /proc was enumerated, or belongs to
            // another user. Either way it contributes nothing to this tree.
            return false;
        }

        // "pid (comm) state ppid ...". comm is arbitrary and may contain spaces
        // and parentheses, so fields are counted from the last ')'.
        var commEnd = stat.LastIndexOf(')');
        if (commEnd < 0 || commEnd + 2 >= stat.Length) return false;
        var fields = stat[(commEnd + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // fields[0] is state (stat field 3), so ppid is fields[1] and
        // utime/stime/cutime/cstime are fields[11..14].
        if (fields.Length < 15) return false;
        if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid))
            return false;
        // Reaped children are charged to their parent's cutime/cstime. Counting
        // those too keeps the total monotone across a fork-heavy build - a shell
        // step that spawns hundreds of short-lived tools must not read as idle
        // just because none of them is alive at sample time. No double counting:
        // a live child's own ticks are read from its own stat line and only move
        // into the parent's c* fields once it is gone from the sample.
        var ticks = 0L;
        foreach (var index in (int[])[11, 12, 13, 14])
        {
            if (!long.TryParse(
                    fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return false;
            ticks += value;
        }
        cpuTicks = ticks;
        return true;
    }
}

/// <summary>
/// Bounded side effect around <see cref="CommandProgressPolicy"/>: polls the CPU
/// time of a running process tree and fires once when the tree stops making
/// progress. The caller supplies the sampler, so the loop is testable without a
/// real process.
/// </summary>
public sealed class CommandProgressWatchdog : IAsyncDisposable
{
    private readonly Func<TimeSpan?> _sample;
    private readonly TimeSpan _noProgressAfter;
    private readonly TimeSpan _checkEvery;
    private readonly Action _onStalled;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _watchTask;
    private int _tripped;

    public CommandProgressWatchdog(
        Func<TimeSpan?> sample,
        TimeSpan noProgressAfter,
        Action onStalled,
        TimeSpan? checkEvery = null)
    {
        _sample = sample;
        _noProgressAfter = noProgressAfter;
        _onStalled = onStalled;
        _checkEvery = checkEvery
                      ?? TimeSpan.FromSeconds(Math.Clamp(noProgressAfter.TotalSeconds / 10, 1, 30));
        _watchTask = noProgressAfter <= TimeSpan.Zero ? Task.CompletedTask : WatchAsync();
    }

    /// <summary>True once the tree was declared stalled.</summary>
    public bool Stalled => Volatile.Read(ref _tripped) != 0;

    private async Task WatchAsync()
    {
        var windowStartCpu = _sample() ?? TimeSpan.Zero;
        var windowStart = DateTime.UtcNow;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(_checkEvery, _lifetime.Token);
                var progress = CommandProgressPolicy.Evaluate(
                    _sample(),
                    windowStartCpu,
                    DateTime.UtcNow - windowStart,
                    _noProgressAfter);
                switch (progress)
                {
                    case CommandProgress.Advanced:
                        windowStartCpu = _sample() ?? windowStartCpu;
                        windowStart = DateTime.UtcNow;
                        break;
                    case CommandProgress.Stalled:
                        Volatile.Write(ref _tripped, 1);
                        _onStalled();
                        return;
                    case CommandProgress.Unobservable:
                        // No CPU truth on this host: never guess a kill.
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await _watchTask; }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}
