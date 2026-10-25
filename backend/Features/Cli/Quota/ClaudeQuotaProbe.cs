using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Probes Anthropic's <c>claude</c> CLI for plan + quota info via the <c>/usage</c>
/// slash command, which renders a panel like:
/// <code>
/// Current session            ██████████████████ 104% used
///                            Resets 3:40am (Europe/Berlin)
/// Current week (all models)  ██████████████      28% used
///                            Resets Apr 28, 2pm (Europe/Berlin)
/// </code>
/// In the PTY snapshot inter-word spaces are usually collapsed
/// (e.g. <c>Currentsession...104%usedResets3:40am(Europe/Berlin)</c>),
/// so all parsing regexes are intentionally permissive about whitespace.
///
/// Notes:
///  - <c>/usage</c> works even when over-quota; we no longer fall back to the
///    welcome banner alone.
///  - Plan is read from the welcome banner (<c>Claude Pro</c> / <c>Claude Max</c>) and,
///    when missing, from the configured <c>Quota:ClaudePlan</c> setting.
///  - The "Current session" bucket can read above 100% (extra usage / overages);
///    we surface the raw value so the UI can colour-code it.
/// </summary>
public sealed class ClaudeQuotaProbe : QuotaProbeBase
{
    private static readonly Regex PlanRegex = new(
        @"Claude\s*(Pro\+?|Pro\s*Plus|Free|Team|Max(?:\s*\d+x)?|Enterprise)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Current session ... NN% used Resets H:MMam (Tz)"
    private static readonly Regex SessionRegex = new(
        @"Current\s*session[^\d]{0,160}?(?<pct>\d+)\s*%\s*used[^R]{0,40}?Resets\s*(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*(?:\((?<tz>[^)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Current week (all models) ... NN% used Resets Mon DD, H[:MM][am|pm] (Tz)"
    // or the compact Claude variant: "Currentweek(allmodels)...32%usedResets2pm(Europe/Berlin)"
    private static readonly Regex WeekRegex = new(
        @"Current\s*week\s*\(\s*all\s*models?\s*\)[^\d]{0,220}?(?<pct>\d+)\s*%\s*used[^R]{0,60}?Resets\s*(?<reset>(?:[A-Za-z]+\s*\d+\s*,?\s*)?\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*(?:\((?<tz>[^)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Inline rate-limit warning Claude prints when over quota — used as a
    // last-ditch fallback for the session reset time when /usage failed.
    private static readonly Regex RateLimitResetRegex = new(
        @"out\s*of\s*(?<bucket>\w+)\s*usage[^·]*·\s*resets\s*(?<time>\d{1,2}:\d{2}\s*(?:am|pm)?)\s*(?:\((?<tz>[^\)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Banner is present (we got a session) but the /usage panel never rendered."
    // When this matches and Windows ends up empty, log Warn so a future CLI-output
    // format change is visible in logs instead of silently returning windows: [].
    private static readonly Regex WelcomeBannerRegex = new(
        @"Claude\s*Code\s*v|Welcome\s*back|Tips\s*for\s*getting\s*started",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Signature of a first-run onboarding screen or a feature/what's-new upsell dialog that
    // sits in front of the ready REPL and swallows the /usage slash command. Distinct from a
    // /usage format drift — see LooksLikeOnboardingWizard / the self-diagnosis in ProbeAsync.
    private static readonly Regex OnboardingWizardRegex = new(
        @"Choose\s*the\s*text\s*style|text\s*style\s*that\s*looks\s*best|Let'?s\s*get\s*started|match\s*terminal|fullscreen\s*renderer|Try\s*the\s*new\s*full|Flicker-?free\s*output",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Claude Code 2.1.202 replaced the old standalone /usage panel with a
    // tabbed Settings / Status / Config / Usage / Stats view. API-billed
    // accounts only get session cost/token statistics in that view, not
    // subscription utilization percentages. Treat that as a recognized
    // "quota exists but the CLI did not report a number" shape rather than
    // returning no windows or a parser error.
    //
    // PTY snapshots commonly collapse all inter-word whitespace, hence the
    // deliberately whitespace-optional headings. The two anchors keep a
    // random assistant response mentioning "usage" from matching.
    private static readonly Regex TabbedSessionUsageRegex = new(
        @"Settings\s*Status\s*Config\s*Usage\s*Stats.*?" +
        @"Session.*?Total\s*cost\s*:.*?Total\s*duration\s*\(\s*API\s*\)\s*:.*?" +
        @"Usage\s*:.*?(?:input|output|cache\s*read|cache\s*write)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Inserts a space between a letter and the digit after it ("Oct13" -> "Oct 13").
    private static readonly Regex LetterDigitBoundaryRegex = new(
        @"(?<=[A-Za-z])(?=\d)", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRunRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;

    // Reset labels already reported as unparsed; the probe is a singleton, so
    // each format drift is logged once per process instead of on every probe.
    private readonly HashSet<string> _unparsedResetLabels = new(StringComparer.Ordinal);

    public ClaudeQuotaProbe(
        ILogger<ClaudeQuotaProbe> logger,
        CliRouter router,
        CliEnvironment env,
        IConfiguration configuration,
        TimeProvider? time = null)
        : base(logger, router, env, configuration)
    {
        _configuration = configuration;
        _time = time ?? TimeProvider.System;
    }

    public override string CliType => CliTypes.Claude;

    public override async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        var cliVersion = DetectCliVersion();
        try
        {
            var trustPattern  = new Regex(@"trust\s*this\s*folder|Quick\s*safety\s*check", RegexOptions.IgnoreCase);
            var themePattern  = new Regex(@"Choose\s*the\s*text\s*style|text\s*style\s*that\s*looks\s*best|match\s*terminal", RegexOptions.IgnoreCase);
            var upsellPattern = new Regex(@"fullscreen\s*renderer|Flicker-?free|Try\s*the\s*new|What'?s\s*new|Not\s*now|Esc\s*to\s*cancel", RegexOptions.IgnoreCase);
            var readyPattern  = new Regex(@"\?\s*for\s*shortcuts|for\s*shortcuts|esc\s*to\s*interrupt", RegexOptions.IgnoreCase);
            var usagePattern  = new Regex(
                @"Current\s*session|Current\s*week|Settings\s*Status\s*Config\s*Usage\s*Stats",
                RegexOptions.IgnoreCase);

            // Drive claude past its startup gates, THEN run /usage. Claude Code 2.1.x can
            // interpose several interactive screens between spawn and the ready REPL:
            //   folder-trust dialog  →  first-run THEME picker  →  feature/what's-new upsells
            //     (e.g. "Try the new fullscreen renderer?")  →  ready prompt.
            // The old flow fired /usage on the WELCOME BANNER, but the banner is already on
            // screen during onboarding, so /usage leaked into the wizard and windows came back
            // empty. Instead we dismiss each known gate with a guarded step (no-op + no key-leak
            // when that gate isn't showing) and only send /usage once the READY affordance
            // ("? for shortcuts", which no wizard renders) appears.
            //
            //  - Trust / theme steps are SendKeysOnlyIfMatched: once accepted, Claude persists
            //    the choice (~/.claude.json) and the dialog never returns; blindly sending keys
            //    would leak them into the chat input and corrupt the following /usage.
            //  - Upsells are declined with <Esc> (never <Enter>: the highlighted default may be
            //    "Yes", and switching on the fullscreen/alt-screen renderer would break the PTY
            //    snapshot we scrape). Two passes because the CLI can stack more than one.
            //  - send-usage is NOT guarded: if the ready-affordance text shifts in a future
            //    release we still fire /usage after the wait rather than hang forever.
            var snap = await ProbeWithStepsAsync(
            [
                new ProbeStep("await-trust",      WaitForPattern: trustPattern,  WaitTimeoutMs: 4000, SendKeys: "1<Enter>",      SettleTimeoutMs: 6000, SendKeysOnlyIfMatched: true),
                new ProbeStep("dismiss-theme",    WaitForPattern: themePattern,  WaitTimeoutMs: 3500, SendKeys: "<Enter>",       SettleIdleMs: 1000, SettleTimeoutMs: 5000, SendKeysOnlyIfMatched: true),
                new ProbeStep("dismiss-upsell-1", WaitForPattern: upsellPattern, WaitTimeoutMs: 3500, SendKeys: "<Esc>",         SettleIdleMs: 1000, SettleTimeoutMs: 5000, SendKeysOnlyIfMatched: true),
                new ProbeStep("dismiss-upsell-2", WaitForPattern: upsellPattern, WaitTimeoutMs: 2500, SendKeys: "<Esc>",         SettleIdleMs: 1000, SettleTimeoutMs: 5000, SendKeysOnlyIfMatched: true),
                new ProbeStep("send-usage",       WaitForPattern: readyPattern,  WaitTimeoutMs: 10000, SendKeys: "/usage<Enter>", SettleIdleMs: 1500, SettleTimeoutMs: 8000),
                new ProbeStep("await-usage",      WaitForPattern: usagePattern,  WaitTimeoutMs: 8000,  SettleIdleMs: 1200, SettleTimeoutMs: 5000)
            ],
            initialIdleMs: 8000,
            ct);

            string? plan = PlanRegex.Match(snap) is { Success: true } pm
                ? NormalizePlan(pm.Groups[1].Value)
                : null;
            if (plan == null)
            {
                var configured = _configuration["Quota:ClaudePlan"];
                if (!string.IsNullOrWhiteSpace(configured)) plan = NormalizePlan(configured);
            }

            var nowUtc = _time.GetUtcNow().UtcDateTime;
            var windows = ParseUsageWindows(snap, nowUtc);

            // Fallback: if /usage didn't render but the rate-limit warning did,
            // synthesize at least the session bucket so the user has *something*.
            if (windows.Count == 0 && RateLimitResetRegex.Match(snap) is { Success: true } rm)
            {
                var time = rm.Groups["time"].Value.Trim();
                var tz   = rm.Groups["tz"].Success ? rm.Groups["tz"].Value.Trim() : null;
                windows.Add(new QuotaWindow
                {
                    Label      = $"Out of {rm.Groups["bucket"].Value} usage",
                    UsedPct    = 100,
                    Unit       = "%",
                    ResetAt    = ParseResetTimeUtc(time, tz, nowUtc),
                    ResetLabel = tz != null ? $"{time} ({tz})" : time
                });
            }

            WarnUnparsedResets(windows, _logger, _unparsedResetLabels);

            // Self-diagnosis order matters: an onboarding/upsell wizard on screen is a DISTINCT
            // failure from a real /usage format drift, and it wins. Checking it first keeps the
            // parser-drift signal meaningful (it no longer fires on a wizard the banner happens
            // to sit behind) and puts the actionable cause — "finish onboarding" — in the log.
            if (windows.Count == 0 && LooksLikeOnboardingWizard(snap))
            {
                _logger.LogWarning(
                    "Claude /usage probe returned 0 windows because the CLI is stuck on a first-run onboarding / " +
                    "feature-upsell wizard (theme picker or a \"Try the new …\" dialog), so /usage never reached the " +
                    "ready prompt. Fix: finish Claude Code onboarding (or seed hasCompletedOnboarding in ~/.claude.json), " +
                    "or extend the probe's dismiss steps for this CLI version. Raw sample (tail): {Sample}",
                    TruncateForDebug(snap, 400));
            }
            else if (LooksLikeParserDrift(snap, windows))
            {
                _logger.LogWarning(
                    "Claude /usage probe returned 0 windows but the welcome banner is present in the snapshot. " +
                    "Likely causes: (1) /usage output format changed in a new Claude Code release and the parser regexes need updating, " +
                    "or (2) the slash command never executed (chat-input leak from a stale trust step). " +
                    "Raw sample (tail): {Sample}",
                    TruncateForDebug(snap, 400));
            }

            return new QuotaSnapshot
            {
                CliType   = CliType,
                CliVersion = cliVersion,
                Plan      = plan,
                Source    = "/usage",
                RawSample = TruncateForDebug(snap),
                Windows   = windows,
                Error     = windows.Count == 0 && LooksLikeOnboardingWizard(snap)
                    ? "Claude CLI is showing its first-run onboarding/feature wizard, so /usage never ran. Finish Claude Code onboarding, or update the quota probe's dismiss steps."
                    : (plan == null && windows.Count == 0)
                        ? "Could not parse plan or quota info from Claude /usage panel."
                        : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude quota probe failed");
            return new QuotaSnapshot
            {
                CliType = CliType,
                CliVersion = cliVersion,
                Source = "/usage",
                Error = DescribeProbeFailure(ex)
            };
        }
    }

    /// <summary>
    /// Drift detector: true when the snapshot clearly contains a Claude session
    /// (banner / welcome-back / tips line) but the parser produced no windows.
    /// That combination is the signature of a CLI-output format change or a
    /// probe-step bug that prevented /usage from rendering. Public so tests can
    /// pin the heuristic against the cached real-world rawSample.
    /// </summary>
    public static bool LooksLikeParserDrift(string snap, IReadOnlyCollection<QuotaWindow> windows)
    {
        if (windows.Count > 0) return false;
        if (string.IsNullOrEmpty(snap)) return false;
        return WelcomeBannerRegex.IsMatch(snap);
    }

    /// <summary>
    /// True when the snapshot shows Claude's first-run onboarding (theme picker, "Let's get
    /// started") or a post-onboarding feature upsell ("Try the new fullscreen renderer?").
    /// These screens sit in front of the ready REPL and swallow the /usage slash command, so
    /// windows come back empty for a reason that is NOT a /usage format drift. Public so tests
    /// can pin the heuristic against captured v2.1.201 wizard snapshots.
    /// </summary>
    public static bool LooksLikeOnboardingWizard(string snap)
        => !string.IsNullOrEmpty(snap) && OnboardingWizardRegex.IsMatch(snap);

    /// <summary>
    /// Logs one warning per distinct reset label that did not parse into a
    /// <see cref="QuotaWindow.ResetAt"/>. A window without a reset time drops out
    /// of projected-breach admission silently, so the raw label goes into the log
    /// to make the next format drift visible. <paramref name="seenLabels"/> holds
    /// the labels already reported.
    /// </summary>
    public static void WarnUnparsedResets(
        IEnumerable<QuotaWindow> windows, ILogger logger, ISet<string> seenLabels)
    {
        foreach (var window in windows)
        {
            if (window.ResetAt is not null || string.IsNullOrWhiteSpace(window.ResetLabel)) continue;
            bool firstSighting;
            lock (seenLabels) firstSighting = seenLabels.Add(window.ResetLabel);
            if (!firstSighting) continue;
            logger.LogWarning(
                "Claude /usage reset label could not be parsed into a reset time, so the window {Window} " +
                "is not projected. Raw label: {ResetLabel}",
                window.Label, window.ResetLabel);
        }
    }

    private static string NormalizePlan(string raw)
    {
        var t = raw.Trim();
        if (t.Equals("Pro Plus", StringComparison.OrdinalIgnoreCase) || t.Equals("ProPlus", StringComparison.OrdinalIgnoreCase))
            return "Pro+";
        return t;
    }

    public static List<QuotaWindow> ParseUsageWindows(string snap, DateTime nowUtc)
    {
        var windows = new List<QuotaWindow>();

        if (SessionRegex.Match(snap) is { Success: true } sm
            && double.TryParse(sm.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sPct))
        {
            var time = sm.Groups["time"].Value.Trim();
            var tz   = sm.Groups["tz"].Success ? sm.Groups["tz"].Value.Trim() : null;
            windows.Add(new QuotaWindow
            {
                Label      = "Current session (5h)",
                UsedPct    = Math.Min(100, sPct),
                Unit       = "%",
                ResetAt    = ParseResetTimeUtc(time, tz, nowUtc),
                ResetLabel = tz != null ? $"{time} ({tz})" : time
            });
        }

        if (WeekRegex.Match(snap) is { Success: true } wm
            && double.TryParse(wm.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wPct))
        {
            var reset = wm.Groups["reset"].Value.Trim();
            var tz    = wm.Groups["tz"].Success ? wm.Groups["tz"].Value.Trim() : null;
            var hasDate = Regex.IsMatch(reset, @"[A-Za-z]+\s*\d+", RegexOptions.IgnoreCase);
            windows.Add(new QuotaWindow
            {
                Label      = "Weekly (all models)",
                UsedPct    = Math.Min(100, wPct),
                Unit       = "%",
                ResetAt    = hasDate ? ParseResetDateUtc(reset, tz, nowUtc) : ParseResetTimeUtc(reset, tz, nowUtc),
                ResetLabel = tz != null ? $"{reset} ({tz})" : reset
            });
        }

        // New tabbed /usage screen, but no subscription utilization block.
        // One explicit null-valued window lets all consumers distinguish
        // "recognized and unknown" from "probe has not produced anything".
        // Admission already ignores windows without a usable UsedPct.
        if (windows.Count == 0 && TabbedSessionUsageRegex.IsMatch(snap))
        {
            windows.Add(new QuotaWindow
            {
                Label = "Quota",
                UsedPct = null,
                Unit = "%"
            });
        }

        return windows;
    }

    /// <summary>
    /// Parses "3:40am" / "15:30" with an optional IANA timezone in parens and
    /// returns the next occurrence of that wall-clock time after <paramref name="nowUtc"/>, in UTC.
    /// </summary>
    private static DateTime? ParseResetTimeUtc(string timeStr, string? ianaTz, DateTime nowUtc)
    {
        var formats = new[] { "h:mmtt", "hh:mmtt", "htt", "hhtt", "H:mm", "HH:mm" };
        var clean = timeStr.Replace(" ", "");
        if (!DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        TimeZoneInfo tz;
        try { tz = ianaTz != null ? TimeZoneInfo.FindSystemTimeZoneById(ianaTz) : TimeZoneInfo.Local; }
        catch { tz = TimeZoneInfo.Local; }

        var nowInTz   = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var resetInTz = new DateTime(nowInTz.Year, nowInTz.Month, nowInTz.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        if (resetInTz <= nowInTz) resetInTz = resetInTz.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(resetInTz, tz);
    }

    /// <summary>
    /// Parses "Apr 28, 2pm", "Apr 28, 14:30" or the compact PTY form "Oct13,2pm"
    /// with an optional IANA timezone and returns the corresponding UTC instant.
    /// The year is taken from <paramref name="nowUtc"/>.
    /// </summary>
    private static DateTime? ParseResetDateUtc(string dateStr, string? ianaTz, DateTime nowUtc)
    {
        // Normalise "Oct13,2pm" and "Apr 28, 2pm" to "Oct 13 2pm" / "Apr 28 2pm" so a single
        // set of formats covers both. Only a letter followed by a digit is split; "2pm" stays
        // intact because the formats expect "htt".
        var spaced = LetterDigitBoundaryRegex.Replace(dateStr, " ").Replace(",", " ");
        var clean = WhitespaceRunRegex.Replace(spaced, " ").Trim();
        var formats = new[]
        {
            "MMM d htt", "MMM d hh tt", "MMM d h:mmtt", "MMM d hh:mmtt",
            "MMM d HH:mm", "MMM d H:mm",
            "MMMM d htt", "MMMM d h:mmtt", "MMMM d HH:mm"
        };
        if (!DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return null;

        TimeZoneInfo tz;
        try { tz = ianaTz != null ? TimeZoneInfo.FindSystemTimeZoneById(ianaTz) : TimeZoneInfo.Local; }
        catch { tz = TimeZoneInfo.Local; }

        // Year is missing from the input — assume the current year, roll to next year if past.
        var nowInTz = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var year = nowInTz.Year;
        var candidate = new DateTime(year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        if (candidate < nowInTz.AddDays(-1)) candidate = candidate.AddYears(1);
        return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
    }
}
