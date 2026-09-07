namespace AgentStudio.Watcher;

/// <summary>Defaults for the <c>Watcher</c> configuration section.</summary>
public static class WatcherDefaults
{
    public const string ConfigurationSection = "Watcher";
    public const string ContingentSection = "Contingent";

    /// <summary>Kill switch. Off leaves the service resident but idle.</summary>
    public const bool Enabled = false;

    /// <summary>Section 7 reconciliation cadence.</summary>
    public const int IntervalSeconds = 300;

    /// <summary>Section 5 allows analysis only on a declared uncertainty.</summary>
    public const bool AnalysisEnabled = true;

    /// <summary>The strong analysis floor of section 5. Quota or price cannot lower it.</summary>
    public const string AnalysisTier = "sol-medium";

    public const int AnalysisTimeoutSeconds = 180;
    public const int CompressionThresholdCharacters = 6_000;
    public const int EvidenceCharacterBudget = WatcherEvidencePackBuilder.DefaultCharacterBudget;
    public const int MaxProposalsPerSweep = 5;
    public const int SuppressionDays = 14;
    public const int HeartbeatMissesBeforeUnavailable = 3;
}

/// <summary>
/// Everything the Watcher reads from configuration, resolved once per sweep so
/// the operator can flip a value without restarting the backend. Every numeric
/// value is clamped where it is parsed.
/// </summary>
public sealed record WatcherOptions
{
    public bool Enabled { get; init; } = WatcherDefaults.Enabled;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(WatcherDefaults.IntervalSeconds);
    public WatcherDetectorOptions Detectors { get; init; } = WatcherDetectorOptions.Default;
    public WatcherContingentLimits Contingent { get; init; } = WatcherContingentLimits.Default;

    /// <summary>When false the Watcher counts and reports but never calls a model.</summary>
    public bool AnalysisEnabled { get; init; } = WatcherDefaults.AnalysisEnabled;

    /// <summary>Routing-policy tier id used for strong analysis.</summary>
    public string AnalysisTier { get; init; } = WatcherDefaults.AnalysisTier;

    public TimeSpan AnalysisTimeout { get; init; } = TimeSpan.FromSeconds(WatcherDefaults.AnalysisTimeoutSeconds);
    public int CompressionThresholdCharacters { get; init; } = WatcherDefaults.CompressionThresholdCharacters;
    public int EvidenceCharacterBudget { get; init; } = WatcherDefaults.EvidenceCharacterBudget;

    /// <summary>Upper bound on proposals one sweep may write, below the contingent.</summary>
    public int MaxProposalsPerSweep { get; init; } = WatcherDefaults.MaxProposalsPerSweep;

    /// <summary>How long a rejected fingerprint stays suppressed before it can return.</summary>
    public TimeSpan SuppressionPeriod { get; init; } = TimeSpan.FromDays(WatcherDefaults.SuppressionDays);

    /// <summary>
    /// Project that receives proposals for workspace-wide cases, by name. When
    /// unset the first watch path is used and the fallback is logged, because a
    /// silently misfiled card is worse than a noisy one.
    /// </summary>
    public string? ProposalProject { get; init; }

    public static WatcherOptions Default { get; } = new();

    public static WatcherOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(WatcherDefaults.ConfigurationSection);
        var contingent = section.GetSection(WatcherDefaults.ContingentSection);

        return new WatcherOptions
        {
            Enabled = section.GetValue<bool?>("Enabled") ?? WatcherDefaults.Enabled,
            Interval = TimeSpan.FromSeconds(Math.Clamp(
                section.GetValue<int?>("IntervalSeconds") ?? WatcherDefaults.IntervalSeconds, 30, 60 * 60)),
            AnalysisEnabled = section.GetValue<bool?>("AnalysisEnabled") ?? WatcherDefaults.AnalysisEnabled,
            AnalysisTier = Trim(section["AnalysisTier"]) ?? WatcherDefaults.AnalysisTier,
            AnalysisTimeout = TimeSpan.FromSeconds(Math.Clamp(
                section.GetValue<int?>("AnalysisTimeoutSeconds") ?? WatcherDefaults.AnalysisTimeoutSeconds, 30, 900)),
            CompressionThresholdCharacters = Math.Clamp(
                section.GetValue<int?>("CompressionThresholdCharacters")
                ?? WatcherDefaults.CompressionThresholdCharacters, 1_000, 100_000),
            EvidenceCharacterBudget = Math.Clamp(
                section.GetValue<int?>("EvidenceCharacterBudget")
                ?? WatcherDefaults.EvidenceCharacterBudget, 500, 200_000),
            MaxProposalsPerSweep = Math.Clamp(
                section.GetValue<int?>("MaxProposalsPerSweep") ?? WatcherDefaults.MaxProposalsPerSweep, 1, 50),
            SuppressionPeriod = TimeSpan.FromDays(Math.Clamp(
                section.GetValue<int?>("SuppressionDays") ?? WatcherDefaults.SuppressionDays, 1, 365)),
            ProposalProject = Trim(section["ProposalProject"]),
            Detectors = new WatcherDetectorOptions
            {
                RepetitionThreshold = Math.Clamp(
                    section.GetValue<int?>("RepetitionThreshold")
                    ?? WatcherDetectorOptions.Default.RepetitionThreshold, 2, 100),
                RepetitionCardThreshold = Math.Clamp(
                    section.GetValue<int?>("RepetitionCardThreshold")
                    ?? WatcherDetectorOptions.Default.RepetitionCardThreshold, 2, 100),
                SilenceCadenceFactor = Math.Clamp(
                    section.GetValue<double?>("SilenceCadenceFactor")
                    ?? WatcherDetectorOptions.Default.SilenceCadenceFactor, 0.5, 20),
                HygieneGracePeriod = TimeSpan.FromHours(Math.Clamp(
                    section.GetValue<int?>("HygieneGraceHours") ?? 24, 1, 24 * 90)),
                DriftWindow = TimeSpan.FromDays(Math.Clamp(
                    section.GetValue<int?>("DriftWindowDays") ?? 30, 1, 365)),
            },
            Contingent = new WatcherContingentLimits
            {
                ModelCallsPerDay = Count(contingent, "ModelCallsPerDay", WatcherContingentLimits.Default.ModelCallsPerDay),
                ModelCallsPerWeek = Count(contingent, "ModelCallsPerWeek", WatcherContingentLimits.Default.ModelCallsPerWeek),
                TokensPerDay = Tokens(contingent, "TokensPerDay", WatcherContingentLimits.Default.TokensPerDay),
                TokensPerWeek = Tokens(contingent, "TokensPerWeek", WatcherContingentLimits.Default.TokensPerWeek),
                ProposalsPerDay = Count(contingent, "ProposalsPerDay", WatcherContingentLimits.Default.ProposalsPerDay),
                ProposalsPerWeek = Count(contingent, "ProposalsPerWeek", WatcherContingentLimits.Default.ProposalsPerWeek),
                CommentsPerDay = Count(contingent, "CommentsPerDay", WatcherContingentLimits.Default.CommentsPerDay),
                CommentsPerWeek = Count(contingent, "CommentsPerWeek", WatcherContingentLimits.Default.CommentsPerWeek),
            },
        };
    }

    // A configured zero is meaningful: it closes the dimension. Only an absent
    // value falls back to the default, and a negative value is read as zero
    // rather than as an accidental ceiling of minus one.
    private static int? Count(IConfigurationSection section, string key, int? fallback)
    {
        var value = section.GetValue<int?>(key);
        return value is null ? fallback : Math.Max(0, value.Value);
    }

    private static long? Tokens(IConfigurationSection section, string key, long? fallback)
    {
        var value = section.GetValue<long?>(key);
        return value is null ? fallback : Math.Max(0L, value.Value);
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
