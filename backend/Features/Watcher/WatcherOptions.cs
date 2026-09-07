namespace AgentStudio.Watcher;

/// <summary>
/// Watcher kill switch and sweep cadence, read fresh every tick from the
/// <c>Watcher</c> configuration section (same shape as <c>AcceptanceRailOptions</c>).
/// Defaults to disabled: a workspace must opt in, the same convention as
/// <c>Supervisor:SoftReasoningEnabled</c>, because W2 creates real task cards.
/// </summary>
public sealed record WatcherOptions
{
    public const string ConfigurationSection = "Watcher";

    public bool Enabled { get; init; }
    public int IntervalSeconds { get; init; } = 300;

    /// <summary>A case must be observed in this many distinct sweeps before W2 drafts a proposal (§10.3 "persists over two sweeps").</summary>
    public int PersistenceSweepsBeforeProposal { get; init; } = 2;

    /// <summary>How long a rejected fingerprint stays suppressed (§10.4).</summary>
    public int SuppressionDays { get; init; } = 14;

    public static WatcherOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigurationSection);
        return new WatcherOptions
        {
            Enabled = section.GetValue("Enabled", false),
            IntervalSeconds = Math.Clamp(section.GetValue("IntervalSeconds", 300), 30, 3600),
            PersistenceSweepsBeforeProposal = Math.Max(1, section.GetValue("PersistenceSweepsBeforeProposal", 2)),
            SuppressionDays = Math.Max(1, section.GetValue("SuppressionDays", 14)),
        };
    }
}

/// <summary>
/// Per-day and per-week Watcher contingent (§10.4). A budget of zero disables
/// that category entirely: the Watcher keeps detecting and counting cases,
/// but stops calling models or creating proposals/comments in that category.
/// </summary>
public sealed record WatcherContingentBudgets
{
    public const string ConfigurationSection = "Watcher:Contingent";

    public long DailyTokenBudget { get; init; } = 200_000;
    public long WeeklyTokenBudget { get; init; } = 1_000_000;
    public int DailyProposalBudget { get; init; } = 10;
    public int WeeklyProposalBudget { get; init; } = 40;
    public int DailyModelCallBudget { get; init; } = 20;
    public int WeeklyModelCallBudget { get; init; } = 100;

    public static WatcherContingentBudgets FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigurationSection);
        return new WatcherContingentBudgets
        {
            DailyTokenBudget = section.GetValue("DailyTokenBudget", 200_000L),
            WeeklyTokenBudget = section.GetValue("WeeklyTokenBudget", 1_000_000L),
            DailyProposalBudget = section.GetValue("DailyProposalBudget", 10),
            WeeklyProposalBudget = section.GetValue("WeeklyProposalBudget", 40),
            DailyModelCallBudget = section.GetValue("DailyModelCallBudget", 20),
            WeeklyModelCallBudget = section.GetValue("WeeklyModelCallBudget", 100),
        };
    }
}
