using AgentStudio.Security;

namespace AgentStudio.Cli;

/// <summary>
/// Pinned quota readings for isolated demo and worktree test backends (AGT-3593).
/// Those backends run on the shared runner host, so a live probe would spawn the
/// host's installed CLIs and surface the host's real subscription quota in a demo
/// workspace. <see cref="QuotaService"/> serves these snapshots instead and never
/// probes. Percentages are fixed; timestamps follow the clock so the footer reads
/// the snapshot as fresh rather than stale.
/// </summary>
public static class DemoQuotaSnapshots
{
    /// <summary>Environment flag set by <c>scripts/worktree-test-stack.sh</c> for isolated test backends.</summary>
    public const string WorktreeTestBackendKey = "ATP_WORKTREE_TEST_BACKEND";

    /// <summary>Explicit opt-in for a pinned quota source on any other backend.</summary>
    public const string ModeKey = "Quota:Mode";
    public const string PinnedMode = "demo";

    public const string Source = "demo snapshot";

    /// <summary>
    /// True for the public demo profile, an isolated worktree test backend, or
    /// <c>Quota:Mode=demo</c>.
    /// </summary>
    public static bool Enabled(IConfiguration configuration)
        => SecurityProfiles.IsPublicDemo(configuration)
            || configuration[WorktreeTestBackendKey]?.Trim() == "1"
            || string.Equals(configuration[ModeKey]?.Trim(), PinnedMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The pinned snapshot for one CLI as of <paramref name="nowUtc"/>.</summary>
    public static QuotaSnapshot For(string cliType, DateTime nowUtc) => cliType switch
    {
        CliTypes.Codex => new QuotaSnapshot
        {
            CliType = cliType,
            FetchedAt = nowUtc,
            Plan = "Demo",
            Source = Source,
            Windows =
            [
                Window("5-hour", 18, nowUtc.AddHours(3)),
                Window("Weekly", 42, nowUtc.AddDays(4)),
            ]
        },
        CliTypes.Claude => new QuotaSnapshot
        {
            CliType = cliType,
            FetchedAt = nowUtc,
            Plan = "Demo",
            Source = Source,
            Windows =
            [
                Window("Current session (5h)", 24, nowUtc.AddHours(2)),
                Window("Weekly (all models)", 35, nowUtc.AddDays(3)),
            ]
        },
        _ => new QuotaSnapshot { CliType = cliType, FetchedAt = default },
    };

    private static QuotaWindow Window(string label, double usedPct, DateTime resetAt)
        => new() { Label = label, UsedPct = usedPct, Unit = "%", ResetAt = resetAt };
}
