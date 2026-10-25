using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Probes OpenAI's <c>codex</c> CLI for plan + 5h/weekly quota via the
/// <c>/status</c> slash command, which renders a panel like:
/// <code>
/// Account:        robertmischke@gmail.com (Plus)
/// 5h limit:       [░░░░░░░░░░] 0% left (resets 02:33)
/// Weekly limit:   [██████████] 84% left (resets 21:33 on 3 May)
/// </code>
/// IMPORTANT: Codex reports <b>% left</b> (the bar visually shows the remaining
/// budget). We invert (<c>usedPct = 100 - left</c>) so the model is consistent
/// with the other probes and the UI can colour-code high values as critical.
///
/// In the PTY snapshot inter-word spaces are usually collapsed
/// (<c>5hlimit:[░░░░]0%left(resets02:33)</c>), so the regexes are intentionally permissive.
/// </summary>
public sealed class CodexQuotaProbe : QuotaProbeBase
{
    private const string HookReviewStepName = "dismiss-hook-review";
    internal const string HookReviewBlockedError = "Codex hook-trust review dialog blocked /status.";

    private static readonly Regex TrustPattern = new(
        @"trust\s*the\s*contents|Yes,\s*continue",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UpdatePattern = new(
        @"Update\s*available|Skip\s*until\s*next\s*version",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HookReviewPattern = new(
        @"hooks?\s*need\s*review|Press\s*t\s*to\s*trust\s*all",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WelcomePattern = new(
        @"OpenAI\s*Codex|model:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StatusPattern = new(
        @"5h\s*limit|Weekly\s*limit|Account:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Codex 0.149.0 moved the reset onto a following visual row and can omit
    // the reset altogether while the status panel is refreshing. Read the
    // percentage first and make the reset suffix optional. The legacy regexes
    // below remain as fallbacks for the older single-line panel.
    private static readonly Regex FiveHourRegex = new(
        @"5h\s*limit\s*:?\s*(?:\[[^\]]*\]\s*)?(?<left>\d+)\s*%\s*left",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WeeklyRegex = new(
        @"Weekly\s*limit\s*:?\s*(?:\[[^\]]*\]\s*)?(?<left>\d+)\s*%\s*left",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NextLimitHeaderRegex = new(
        @"(?:5h|Weekly|Spark)\s*limit\s*:?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ResetSuffixRegex = new(
        @"\(?\s*resets\s*(?<reset>[^)\r\n│]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LegacyFiveHourRegex = new(
        @"5h\s*limit\s*:?\s*\[[^\]]*\]\s*(?<left>\d+)\s*%\s*left[^()]*\(\s*resets\s*(?<reset>[^)]+)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LegacyWeeklyRegex = new(
        @"Weekly\s*limit\s*:?\s*\[[^\]]*\]\s*(?<left>\d+)\s*%\s*left[^()]*\(\s*resets\s*(?<reset>[^)]+)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Header of the Spark sub-block: "<model>-Spark limit:". It is
    // DELIBERATELY version-agnostic. The previous form pinned the model to
    // "GPT-5.3-Codex-Spark"; when the CLI bumped the Spark model (5.3 -> 5.6),
    // the header stopped matching, the standard/spark split silently collapsed,
    // and the standard-window regexes then latched onto the Spark block's
    // near-empty "% left" line - reporting an exhausted 5-hour/Weekly window as
    // ~1-4% used. That is the AGT-2064 false-snapshot glitch. Only the Spark
    // sub-panel header contains the word "Spark", so matching on "Spark limit"
    // alone is both sufficient and immune to future model renames.
    private static readonly Regex SparkHeaderRegex = new(
        @"Spark\s*limit\s*:?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "Account: someone@example.com (Plus)" — captures the parenthesised plan name.
    private static readonly Regex PlanRegex = new(
        @"Account\s*:?[^()\r\n]{1,120}\(\s*(?<plan>[A-Za-z][A-Za-z0-9 +]{0,30})\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fallback footer when /status couldn't render: "5h NN% · weekly NN%".
    // The footer values are also "% left", so we invert.
    private static readonly Regex FooterRegex = new(
        @"5h\s*(?<h5left>\d+)\s*%\s*[·•]\s*weekly\s*(?<wkleft>\d+)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public CodexQuotaProbe(
        ILogger<CodexQuotaProbe> logger,
        CliRouter router,
        CliEnvironment env,
        IConfiguration? configuration = null)
        : base(logger, router, env, configuration) { }

    public override string CliType => CliTypes.Codex;

    public override async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        var cliVersion = DetectCliVersion();
        try
        {
            var snap = await ProbeWithStepsAsync(
                BuildProbeSteps(),
                initialIdleMs: 8000,
                ct,
                onPatternMatched: step =>
                {
                    if (step.Name == HookReviewStepName)
                    {
                        _logger.LogWarning(
                            "Codex hook-trust review modal detected. Trust the hooks once with 't' in an interactive Codex session, or leave them untrusted. The quota probe closes the dialog with Esc and continues.");
                    }
                });

            var snapshot = ParseSnapshot(snap, cliVersion);

            // Log the parsed windows next to the raw sample so a future false
            // snapshot (AGT-2064) is diagnosable from logs alone, without having
            // to reconstruct the PTY transcript. Kept at Information because a
            // quota probe is a low-frequency, high-value observability surface.
            var sparkSeen = SparkHeaderRegex.IsMatch(snap);
            _logger.LogInformation(
                "codex_quota_probe_parsed plan={Plan} sparkBlock={Spark} windows=[{Windows}]",
                snapshot.Plan ?? "<none>",
                sparkSeen,
                string.Join(", ", snapshot.Windows.Select(w => $"{w.Label}={w.UsedPct}%")));

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex quota probe failed");
            return new QuotaSnapshot
            {
                CliType = CliType,
                CliVersion = cliVersion,
                Source = "/status",
                Error = DescribeProbeFailure(ex)
            };
        }
    }

    internal static IReadOnlyList<ProbeStep> BuildProbeSteps()
        =>
        [
            // Codex's trust prompt has "1. Yes, continue" pre-selected and accepts a
            // bare Enter. Sending "1<Enter>" works for confirmation but ALSO leaves a
            // stray "1" in the chat input box, which then prefixes the next slash
            // command and turns "/status" into a chat message instead of a command.
            new ProbeStep("await-trust", WaitForPattern: TrustPattern, WaitTimeoutMs: 4000, SendKeys: "<Enter>", SettleTimeoutMs: 6000, PreSendDelayMs: 300, SendKeysOnlyIfMatched: true),
            // 0.149.0 can interpose an update picker before the ready REPL.
            // Move off "Update now" and persist "Skip until next version"
            // so a quota probe never attempts to mutate the global CLI.
            new ProbeStep("dismiss-update", WaitForPattern: UpdatePattern, WaitTimeoutMs: 4000, SendKeys: "<Down><Enter>", SettleIdleMs: 1000, SettleTimeoutMs: 6000, SendKeysOnlyIfMatched: true),
            // Hook trust is an operator decision. Close the review dialog without
            // changing trust, and never leak Esc into the input when it is absent.
            new ProbeStep(HookReviewStepName, WaitForPattern: HookReviewPattern, WaitTimeoutMs: 3000, SendKeys: "<Esc>", SendKeysOnlyIfMatched: true, SettleIdleMs: 800, SettleTimeoutMs: 4000),
            // Clear the buffer so the await-welcome match only sees post-gate content.
            new ProbeStep("await-welcome", ClearBufferBefore: true, WaitForPattern: WelcomePattern, WaitTimeoutMs: 10000, SendKeys: "/status", SettleIdleMs: 800, SettleTimeoutMs: 3000, PreSendDelayMs: 800),
            // Send Enter as a separate keystroke. Codex sometimes drops a fast-following
            // Enter while it is still parsing the slash command.
            new ProbeStep("submit-status", PreSendDelayMs: 500, SendKeys: "<Enter>", SettleIdleMs: 1500, SettleTimeoutMs: 8000),
            new ProbeStep("await-status", WaitForPattern: StatusPattern, WaitTimeoutMs: 10000, SettleIdleMs: 1500, SettleTimeoutMs: 6000)
        ];

    internal static QuotaSnapshot ParseSnapshot(string snap, string? cliVersion = null)
    {
        var plan = ParsePlan(snap);
        var windows = ParseStatusWindows(snap);
        var hookReviewBlockedStatus = HookReviewPattern.IsMatch(snap)
            && !StatusPattern.IsMatch(snap);

        return new QuotaSnapshot
        {
            CliType = CliTypes.Codex,
            CliVersion = cliVersion,
            Plan = plan,
            Source = "/status",
            RawSample = TruncateForDebug(snap),
            Windows = windows,
            Error = plan != null || windows.Count > 0
                ? null
                : hookReviewBlockedStatus
                    ? HookReviewBlockedError
                    : "Could not parse Codex /status output."
        };
    }

    public static List<QuotaWindow> ParseStatusWindows(string snap)
    {
        var windows = new List<QuotaWindow>();
        var sparkHeader = SparkHeaderRegex.Match(snap);
        // The standard 5-hour/Weekly windows are read ONLY from the region above
        // the Spark header. This is what keeps the Spark sub-block's own
        // near-empty 5h/Weekly lines from being mistaken for the main windows
        // (AGT-2064). See SparkHeaderRegex for why the header match is
        // version-agnostic.
        var standardSnap = sparkHeader.Success ? snap[..sparkHeader.Index] : snap;

        AddLimitWindow(windows, standardSnap, FiveHourRegex, LegacyFiveHourRegex, "5-hour");
        AddLimitWindow(windows, standardSnap, WeeklyRegex, LegacyWeeklyRegex, "Weekly");

        if (sparkHeader.Success)
        {
            var sparkSnap = snap[(sparkHeader.Index + sparkHeader.Length)..];
            AddLimitWindow(windows, sparkSnap, FiveHourRegex, LegacyFiveHourRegex, "Spark 5-hour");
            AddLimitWindow(windows, sparkSnap, WeeklyRegex, LegacyWeeklyRegex, "Spark Weekly");
        }

        // Footer fallback when /status didn't render: at least we still
        // get usage percentages, just no reset time.
        if (windows.Count == 0 && FooterRegex.Match(snap) is { Success: true } fmm
            && int.TryParse(fmm.Groups["h5left"].Value, out var h5)
            && int.TryParse(fmm.Groups["wkleft"].Value, out var wk))
        {
            windows.Add(new QuotaWindow { Label = "5-hour", UsedPct = 100 - h5, Unit = "%" });
            windows.Add(new QuotaWindow { Label = "Weekly", UsedPct = 100 - wk, Unit = "%" });
        }

        return windows;
    }

    public static string? ParsePlan(string snap)
        => PlanRegex.Match(snap) is { Success: true } match
            ? match.Groups["plan"].Value.Trim()
            : null;

    private static void AddLimitWindow(
        List<QuotaWindow> windows,
        string snap,
        Regex currentRegex,
        Regex legacyRegex,
        string label)
    {
        var match = currentRegex.Match(snap);
        if (!match.Success) match = legacyRegex.Match(snap);
        if (!match.Success
            || !int.TryParse(match.Groups["left"].Value, out var left))
        {
            return;
        }

        var resetRaw = ReadResetSuffix(snap, match);
        windows.Add(new QuotaWindow
        {
            Label      = label,
            UsedPct    = 100 - left,
            Unit       = "%",
            ResetAt    = resetRaw == null ? null : ParseResetUtc(resetRaw),
            ResetLabel = resetRaw
        });
    }

    private static string? ReadResetSuffix(string snap, Match limitMatch)
    {
        if (limitMatch.Groups["reset"].Success)
            return limitMatch.Groups["reset"].Value.Trim();

        var tail = snap[(limitMatch.Index + limitMatch.Length)..];
        var nextLimit = NextLimitHeaderRegex.Match(tail);
        if (nextLimit.Success) tail = tail[..nextLimit.Index];
        if (tail.Length > 240) tail = tail[..240];

        var reset = ResetSuffixRegex.Match(tail);
        return reset.Success ? reset.Groups["reset"].Value.Trim() : null;
    }

    /// <summary>
    /// Codex reset strings come in two shapes:
    ///  - "02:33"               (the next 24h occurrence in the user's local TZ)
    ///  - "21:33 on 3 May"      (a specific upcoming weekly date)
    /// Returns the UTC instant or null if the format doesn't match.
    /// </summary>
    private static DateTime? ParseResetUtc(string raw)
    {
        var local = TimeZoneInfo.Local;
        var nowLocal = DateTime.Now;

        // "HH:MM on D Mon" — weekly variant.
        var withDate = Regex.Match(raw, @"^(?<time>\d{1,2}:\d{2})\s*on\s*(?<day>\d{1,2})\s*(?<mon>[A-Za-z]+)$", RegexOptions.IgnoreCase);
        if (withDate.Success)
        {
            if (DateTime.TryParseExact(
                    withDate.Groups["time"].Value, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                && int.TryParse(withDate.Groups["day"].Value, out var day)
                && TryParseMonth(withDate.Groups["mon"].Value, out var month))
            {
                var year = nowLocal.Year;
                var candidate = new DateTime(year, month, day, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);
                if (candidate < nowLocal.AddDays(-1)) candidate = candidate.AddYears(1);
                return TimeZoneInfo.ConvertTimeToUtc(candidate, local);
            }
            return null;
        }

        // Plain "HH:MM" — next future occurrence today/tomorrow.
        if (DateTime.TryParseExact(raw, new[] { "H:mm", "HH:mm", "h:mmtt", "hh:mmtt" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var hm))
        {
            var candidate = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, hm.Hour, hm.Minute, 0, DateTimeKind.Unspecified);
            if (candidate <= nowLocal) candidate = candidate.AddDays(1);
            return TimeZoneInfo.ConvertTimeToUtc(candidate, local);
        }

        return null;
    }

    private static bool TryParseMonth(string s, out int month)
    {
        var formats = new[] { "MMM", "MMMM" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            month = dt.Month;
            return true;
        }
        month = 0;
        return false;
    }
}
