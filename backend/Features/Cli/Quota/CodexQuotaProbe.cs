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
        => AgentStudio.CliHosting.SubscriptionQuotaParser.ParseCodex(snap, DateTime.UtcNow)
            .Select(window => new QuotaWindow
            {
                Label = window.Label, UsedPct = window.UsedPct, Unit = "%",
                ResetAt = window.ResetAt, ResetLabel = window.ResetLabel
            }).ToList();

    public static string? ParsePlan(string snap)
        => AgentStudio.CliHosting.SubscriptionQuotaParser.CodexPlan(snap);

}
