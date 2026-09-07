namespace AgentStudio.Git;

/// <summary>
/// Tuning for the background git index. The defaults are the operator-laptop
/// budget from AGT-2726: the laptop that runs Studio also runs the Task Server,
/// the runner, and the review gates, so the index is deliberately a small,
/// bounded background consumer rather than a fast one.
/// </summary>
public sealed class GitStateIndexOptions
{
    public const string SectionName = "GitStateIndex";

    /// <summary>
    /// Quiet period after a ref change before a run starts. A fetch or a push
    /// rewrites many refs in a burst; without the debounce each ref would buy
    /// its own run.
    /// </summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Safety-net sweep for changes no watcher reports (network shares, a
    /// repository registered while the backend was down, reftable layouts).
    /// Deliberately slow: the change-driven path is the freshness mechanism.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the scheduler evaluates admission for every repository.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Repositories indexed at the same time. Two keeps the spawn burst small
    /// enough that an interactive git command on the same laptop still feels
    /// immediate.
    /// </summary>
    public int MaxConcurrentRepositories { get; set; } = 2;

    /// <summary>A run slower than this is logged with its slowest git command.</summary>
    public TimeSpan SlowRunThreshold { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Run git below normal priority on Windows. The index is background work;
    /// it must lose the CPU to the interactive shell, not win it.
    /// </summary>
    public bool LowProcessPriority { get; set; } = true;

    /// <summary>SLO warning threshold for the board's p95 (requirement 4).</summary>
    public TimeSpan GroupedP95Budget { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>SLO warning threshold for the process budget (requirement 4).</summary>
    public int SpawnsPerMinuteBudget { get; set; } = 20;

    internal GitStateIndexOptions Sanitized() => new()
    {
        Debounce = Clamp(Debounce, TimeSpan.Zero, TimeSpan.FromSeconds(10)),
        SweepInterval = Clamp(SweepInterval, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1)),
        TickInterval = Clamp(TickInterval, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(5)),
        MaxConcurrentRepositories = Math.Clamp(MaxConcurrentRepositories, 1, 8),
        SlowRunThreshold = Clamp(SlowRunThreshold, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(5)),
        LowProcessPriority = LowProcessPriority,
        GroupedP95Budget = Clamp(GroupedP95Budget, TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(1)),
        SpawnsPerMinuteBudget = Math.Clamp(SpawnsPerMinuteBudget, 1, 10_000),
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;
}
