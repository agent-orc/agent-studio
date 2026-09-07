namespace AgentStudio.Watcher;

/// <summary>Platform constants for the Watcher. Configuration may override them inside the stated clamps.</summary>
public static class WatcherDefaults
{
    public const string ConfigurationSection = "Watcher";

    /// <summary>Kill switch key, read at every tick so a restart is not required.</summary>
    public const string EnabledKey = "Watcher:Enabled";

    /// <summary>Shadow-mode default. W1 and W2 ship observe-only, so the sweep is on but mutation stays bounded.</summary>
    public const bool Enabled = true;

    /// <summary>The dossier's five-minute reconciliation cadence (§7).</summary>
    public const int SweepIntervalSeconds = 300;

    /// <summary>Sweeps a fingerprint must survive before a proposal is allowed (§10.3).</summary>
    public const int PersistenceSweeps = 2;

    /// <summary>Consecutive identical probe failures that make a repetition.</summary>
    public const int ProbeRepeatThreshold = 3;

    /// <summary>Review attempts on one subject SHA without a state change that make a repetition.</summary>
    public const int ReviewAttemptThreshold = 10;

    /// <summary>Distinct cards sharing one integration failure fingerprint that make a repetition.</summary>
    public const int IntegrationFailureCardThreshold = 2;

    /// <summary>How long a validation error may stand before hygiene reports it.</summary>
    public const int HygieneGraceHours = 24;

    /// <summary>How long after a tool version change a failure still counts as dependent.</summary>
    public const int DriftWindowHours = 24 * 7;

    /// <summary>How long a rejected fingerprint stays suppressed. Suppression is visible and expires.</summary>
    public const int SuppressionDays = 14;

    /// <summary>Bound on the evidence pack so one case cannot grow without limit.</summary>
    public const int MaxEvidenceItems = 24;

    /// <summary>Bound on a single evidence value before compression is considered.</summary>
    public const int MaxEvidenceValueChars = 2000;
}

/// <summary>Thresholds the pure detectors read. Every value is a producer-owned bound, never a model output.</summary>
public sealed record WatcherDetectorOptions
{
    public int ProbeRepeatThreshold { get; init; } = WatcherDefaults.ProbeRepeatThreshold;
    public int ReviewAttemptThreshold { get; init; } = WatcherDefaults.ReviewAttemptThreshold;
    public int IntegrationFailureCardThreshold { get; init; } = WatcherDefaults.IntegrationFailureCardThreshold;
    public TimeSpan HygieneGrace { get; init; } = TimeSpan.FromHours(WatcherDefaults.HygieneGraceHours);
    public TimeSpan DriftWindow { get; init; } = TimeSpan.FromHours(WatcherDefaults.DriftWindowHours);

    public static readonly WatcherDetectorOptions Default = new();
}

/// <summary>
/// The dedicated Watcher budget of §10.4: per-day and per-week caps in tokens
/// and in counts. Zero means "detect and count, but make no model call and no
/// proposal"; the unanalysed backlog stays visible.
/// </summary>
public sealed record WatcherContingentOptions
{
    public long DailyTokens { get; init; } = 200_000;
    public long WeeklyTokens { get; init; } = 1_000_000;
    public int DailyModelCalls { get; init; } = 20;
    public int WeeklyModelCalls { get; init; } = 100;
    public int DailyProposals { get; init; } = 10;
    public int WeeklyProposals { get; init; } = 40;
    public int DailyComments { get; init; } = 20;
    public int WeeklyComments { get; init; } = 80;

    /// <summary>Every budget at zero. Used by the acceptance case that proves cases still appear without proposals.</summary>
    public static readonly WatcherContingentOptions Exhausted = new()
    {
        DailyTokens = 0,
        WeeklyTokens = 0,
        DailyModelCalls = 0,
        WeeklyModelCalls = 0,
        DailyProposals = 0,
        WeeklyProposals = 0,
        DailyComments = 0,
        WeeklyComments = 0,
    };

    public static readonly WatcherContingentOptions Default = new();
}

/// <summary>Everything one Watcher tick reads from configuration.</summary>
public sealed record WatcherOptions(
    bool Enabled,
    TimeSpan SweepInterval,
    int PersistenceSweeps,
    TimeSpan SuppressionDuration,
    WatcherDetectorOptions Detectors,
    WatcherContingentOptions Contingent)
{
    public static WatcherOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(WatcherDefaults.ConfigurationSection);
        var contingent = section.GetSection("Contingent");

        return new WatcherOptions(
            section.GetValue<bool?>("Enabled") ?? WatcherDefaults.Enabled,
            TimeSpan.FromSeconds(Math.Clamp(
                section.GetValue<int?>("SweepIntervalSeconds") ?? WatcherDefaults.SweepIntervalSeconds,
                30,
                60 * 60)),
            Math.Clamp(
                section.GetValue<int?>("PersistenceSweeps") ?? WatcherDefaults.PersistenceSweeps,
                1,
                10),
            TimeSpan.FromDays(Math.Clamp(
                section.GetValue<int?>("SuppressionDays") ?? WatcherDefaults.SuppressionDays,
                1,
                365)),
            new WatcherDetectorOptions
            {
                ProbeRepeatThreshold = Math.Clamp(
                    section.GetValue<int?>("ProbeRepeatThreshold") ?? WatcherDefaults.ProbeRepeatThreshold, 2, 100),
                ReviewAttemptThreshold = Math.Clamp(
                    section.GetValue<int?>("ReviewAttemptThreshold") ?? WatcherDefaults.ReviewAttemptThreshold, 2, 1000),
                IntegrationFailureCardThreshold = Math.Clamp(
                    section.GetValue<int?>("IntegrationFailureCardThreshold")
                    ?? WatcherDefaults.IntegrationFailureCardThreshold, 2, 100),
                HygieneGrace = TimeSpan.FromHours(Math.Clamp(
                    section.GetValue<int?>("HygieneGraceHours") ?? WatcherDefaults.HygieneGraceHours, 1, 24 * 30)),
                DriftWindow = TimeSpan.FromHours(Math.Clamp(
                    section.GetValue<int?>("DriftWindowHours") ?? WatcherDefaults.DriftWindowHours, 1, 24 * 90)),
            },
            new WatcherContingentOptions
            {
                DailyTokens = NonNegative(contingent.GetValue<long?>("DailyTokens"), WatcherContingentOptions.Default.DailyTokens),
                WeeklyTokens = NonNegative(contingent.GetValue<long?>("WeeklyTokens"), WatcherContingentOptions.Default.WeeklyTokens),
                DailyModelCalls = (int)NonNegative(contingent.GetValue<int?>("DailyModelCalls"), WatcherContingentOptions.Default.DailyModelCalls),
                WeeklyModelCalls = (int)NonNegative(contingent.GetValue<int?>("WeeklyModelCalls"), WatcherContingentOptions.Default.WeeklyModelCalls),
                DailyProposals = (int)NonNegative(contingent.GetValue<int?>("DailyProposals"), WatcherContingentOptions.Default.DailyProposals),
                WeeklyProposals = (int)NonNegative(contingent.GetValue<int?>("WeeklyProposals"), WatcherContingentOptions.Default.WeeklyProposals),
                DailyComments = (int)NonNegative(contingent.GetValue<int?>("DailyComments"), WatcherContingentOptions.Default.DailyComments),
                WeeklyComments = (int)NonNegative(contingent.GetValue<int?>("WeeklyComments"), WatcherContingentOptions.Default.WeeklyComments),
            });
    }

    /// <summary>Zero is a legitimate budget, so only a negative value falls back to the default.</summary>
    private static long NonNegative(long? configured, long fallback)
        => configured is null ? fallback : Math.Max(0, configured.Value);
}
