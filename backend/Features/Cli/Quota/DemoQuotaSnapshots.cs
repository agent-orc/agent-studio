using System.Globalization;
using AgentStudio.Security;

namespace AgentStudio.Cli;

/// <summary>
/// Pinned quota readings for isolated demo and worktree test backends (AGT-3593).
/// Those backends run on the shared runner host, so a live probe would spawn the
/// host's installed CLIs and surface the host's real subscription quota in a demo
/// workspace. <see cref="QuotaService"/> serves these snapshots instead and never
/// probes.
///
/// <para>
/// AGT-3794-F15: every window runs on a fixed schedule from a seedable anchor
/// instant (<see cref="AnchorKey"/>), so a reset instant stays put between
/// requests and "resets in" counts down like a real subscription. The used
/// percentage is a pure function of the position inside the window: it starts
/// at zero after each reset and climbs linearly to the window's end value.
/// Readings are taken on a fixed probe tick (<see cref="ProbeInterval"/>), so
/// the same tick always yields the same snapshot and the history store records
/// one reading per tick, the way a real probe would.
/// </para>
/// </summary>
public static class DemoQuotaSnapshots
{
    /// <summary>Environment flag set by <c>scripts/worktree-test-stack.sh</c> for isolated test backends.</summary>
    public const string WorktreeTestBackendKey = "ATP_WORKTREE_TEST_BACKEND";

    /// <summary>Explicit opt-in for a pinned quota source on any other backend.</summary>
    public const string ModeKey = "Quota:Mode";
    public const string PinnedMode = "demo";
    public const string EmptyFixtureMode = "fixture-empty";

    /// <summary>Optional ISO-8601 UTC instant that seeds the reset schedule.</summary>
    public const string AnchorKey = "Quota:DemoAnchorUtc";

    /// <summary>Default schedule anchor, a Monday at midnight UTC.</summary>
    public static readonly DateTime DefaultAnchorUtc = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Simulated probe cadence; shorter than the default 10-minute cache TTL so readings stay fresh.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    /// <summary>How much history a pinned backend backfills on start so quota charts have a curve.</summary>
    public static readonly TimeSpan BackfillSpan = TimeSpan.FromDays(7);

    public const string Source = "demo snapshot";

    private sealed record WindowPlan(string Label, TimeSpan Period, TimeSpan Phase, double EndPct);

    private static readonly IReadOnlyDictionary<string, (string Plan, WindowPlan[] Windows)> Plans =
        new Dictionary<string, (string, WindowPlan[])>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Codex] = ("Demo",
            [
                new("5-hour", TimeSpan.FromHours(5), TimeSpan.FromHours(3), 30),
                new("Weekly", TimeSpan.FromDays(7), TimeSpan.FromDays(4), 60),
            ]),
            [CliTypes.Claude] = ("Demo",
            [
                new("Current session (5h)", TimeSpan.FromHours(5), TimeSpan.FromHours(2), 40),
                new("Weekly (all models)", TimeSpan.FromDays(7), TimeSpan.FromDays(3), 55),
            ]),
        };

    /// <summary>
    /// True for the public demo profile, an isolated worktree test backend, or
    /// an explicit pinned mode. The fixture-empty mode keeps isolated remote
    /// execution off host quotas while leaving admission open for fake CLIs.
    /// </summary>
    public static bool Enabled(IConfiguration configuration)
        => SecurityProfiles.IsPublicDemo(configuration)
            || configuration[WorktreeTestBackendKey]?.Trim() == "1"
            || string.Equals(configuration[ModeKey]?.Trim(), PinnedMode, StringComparison.OrdinalIgnoreCase)
            || EmptyFixtureEnabled(configuration);

    public static bool EmptyFixtureEnabled(IConfiguration configuration)
        => string.Equals(configuration[ModeKey]?.Trim(), EmptyFixtureMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The configured schedule anchor, or <see cref="DefaultAnchorUtc"/> when unset or unparsable.</summary>
    public static DateTime Anchor(IConfiguration configuration)
        => DateTime.TryParse(configuration[AnchorKey], CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : DefaultAnchorUtc;

    /// <summary>The latest simulated probe tick at or before <paramref name="nowUtc"/>.</summary>
    public static DateTime ProbeTick(DateTime nowUtc, DateTime anchorUtc)
        => anchorUtc + ProbeInterval * Math.Floor((nowUtc - anchorUtc) / ProbeInterval);

    public static QuotaSnapshot EmptyFixtureFor(string cliType, DateTime nowUtc) => new()
    {
        CliType = cliType,
        FetchedAt = nowUtc,
        Plan = "Fixture",
        Source = Source,
        Windows = [],
    };

    /// <summary>The pinned snapshot for one CLI at the probe tick of <paramref name="nowUtc"/>.</summary>
    public static QuotaSnapshot For(string cliType, DateTime nowUtc)
        => For(cliType, nowUtc, DefaultAnchorUtc);

    /// <summary>The pinned snapshot for one CLI at the probe tick of <paramref name="nowUtc"/> on the schedule of <paramref name="anchorUtc"/>.</summary>
    public static QuotaSnapshot For(string cliType, DateTime nowUtc, DateTime anchorUtc)
    {
        if (!Plans.TryGetValue(cliType, out var plan))
            return new QuotaSnapshot { CliType = cliType, FetchedAt = default };
        var at = ProbeTick(nowUtc, anchorUtc);
        return new QuotaSnapshot
        {
            CliType = cliType,
            FetchedAt = at,
            Plan = plan.Plan,
            Source = Source,
            Windows = plan.Windows.Select(window => Window(window, at, anchorUtc)).ToList(),
        };
    }

    /// <summary>
    /// The snapshots a probe would have taken every <paramref name="step"/> over
    /// <paramref name="span"/> up to <paramref name="nowUtc"/>, oldest first.
    /// </summary>
    public static IEnumerable<QuotaSnapshot> History(
        string cliType, DateTime nowUtc, DateTime anchorUtc, TimeSpan span, TimeSpan step)
    {
        if (!Plans.ContainsKey(cliType) || step <= TimeSpan.Zero) yield break;
        var last = ProbeTick(nowUtc, anchorUtc);
        var count = (int)Math.Floor(span / step);
        for (var i = count; i >= 1; i--)
            yield return For(cliType, last - step * i, anchorUtc);
    }

    private static QuotaWindow Window(WindowPlan plan, DateTime at, DateTime anchorUtc)
    {
        var start = anchorUtc + plan.Phase - plan.Period;
        var cycles = Math.Floor((at - start) / plan.Period);
        var windowStart = start + plan.Period * cycles;
        var resetAt = windowStart + plan.Period;
        var fraction = (at - windowStart) / plan.Period;
        return new QuotaWindow
        {
            Label = plan.Label,
            UsedPct = Math.Round(plan.EndPct * fraction, 1),
            Unit = "%",
            ResetAt = resetAt,
            ObservedStartAt = windowStart,
        };
    }
}
