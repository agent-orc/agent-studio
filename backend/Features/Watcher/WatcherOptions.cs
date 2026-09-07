namespace AgentStudio.Watcher;

/// <summary>
/// Configuration defaults for the Watcher. The kill switch and cadence follow
/// the acceptance-rail convention so an operator finds them where every other
/// hosted rail keeps them.
/// </summary>
public static class WatcherDefaults
{
    public const string ConfigurationSection = "Watcher";

    /// <summary>
    /// Off by default. W1 and W2 are observe-only, but the dossier decision in
    /// section 9 is still pending, so the rail does not start itself.
    /// </summary>
    public const bool Enabled = false;

    /// <summary>Five-minute reconciliation sweep of dossier section 7.</summary>
    public const int IntervalSeconds = 300;

    /// <summary>Independent heartbeat cadence used by the health monitor.</summary>
    public const int HeartbeatSeconds = 60;
}

/// <summary>Options read fresh on every tick so the kill switch is hot.</summary>
public sealed record WatcherOptions(
    bool Enabled,
    TimeSpan Interval,
    WatcherDetectorThresholds Thresholds,
    WatcherContingent Contingent)
{
    public static WatcherOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(WatcherDefaults.ConfigurationSection);
        return new WatcherOptions(
            Enabled: section.GetValue<bool?>("Enabled") ?? WatcherDefaults.Enabled,
            Interval: TimeSpan.FromSeconds(Math.Clamp(
                section.GetValue<int?>("IntervalSeconds") ?? WatcherDefaults.IntervalSeconds,
                30,
                60 * 60)),
            Thresholds: WatcherDetectorThresholds.FromConfiguration(configuration),
            Contingent: WatcherContingent.FromConfiguration(configuration));
    }
}
