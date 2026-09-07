using System.Text.RegularExpressions;

namespace AgentStudio.Shared;

/// <summary>
/// Typed classification of how a single CLI run ended. Produced by
/// <see cref="AgentOutcomeAnalyzer.Analyze"/> from the run's exit status,
/// duration, and output buffer. Consumed by <see cref="RunOutcomePolicy"/>
/// to decide whether the orchestrator should accept the agent's report,
/// re-issue with stronger framing, or surface a meta message to the user.
/// </summary>
public enum AgentOutcomeKind
{
    /// <summary>Agent reports the task is complete.</summary>
    Done,
    /// <summary>Agent reports it cannot proceed.</summary>
    Blocked,
    /// <summary>Agent is waiting for user input or asking a question.</summary>
    NeedsInput,
    /// <summary>Agent is mid-task (run was cut short while still working).</summary>
    Progress,
    /// <summary>The agent explicitly reported that there was no work to do.</summary>
    NoOp,
    /// <summary>Could not classify - no sentinel match and no clear heuristic signal.</summary>
    Unknown
}

/// <summary>
/// Concrete issue class attached to a run outcome. This is separate from
/// <see cref="AgentOutcomeKind"/>: the kind says what the agent appeared
/// to say; the issue says why the orchestrator did not trust or complete
/// the normal terminal-sentinel contract.
/// </summary>
public enum RunIssueKind
{
    None,
    PermissionBlocked,
    WatchdogTimeout,
    MissingTerminalSentinel,
    HeuristicDone,
    /// <summary>
    /// The agent CLI process died hard (a Windows <c>Process.Kill</c> hands
    /// back <c>exitCode &lt; 0</c>) before it could reach a terminal sentinel,
    /// and the run left no commits. This is an infra crash, not an agent
    /// decision: the orchestrator must surface it for review/retry rather than
    /// re-issuing from scratch. Replaces the old <c>ClassifierUnknown</c> for
    /// the hard-process-death half of that bucket (drive-to-conclusion model).
    /// </summary>
    InfraCrash,
    /// <summary>
    /// The run failed with a real, substantial agent turn whose text the
    /// deterministic contract could not map to a terminal verdict (no sentinel,
    /// no commits, but not a hard process death either). The orchestrator can
    /// draw no honest conclusion, so it stops and hands the task to the user
    /// instead of accepting a meaningless marker. Replaces the old
    /// <c>ClassifierUnknown</c> for the logical-failure half of that bucket.
    /// </summary>
    OrchestratorInconclusive,
    NoAgentOutput,
    /// <summary>
    /// The CLI process exited almost immediately with no agent turn. Unlike an
    /// explicit <c>[[TASK_NOOP]]</c>, this is not an agent judgement that there
    /// was nothing to do; it is a failed-start shape (spawn/env/quota/immediate
    /// exit) that must remain visible and retry/escalate through runner policy.
    /// </summary>
    EmptyFastExit,
    /// <summary>
    /// The agent CLI itself failed before any real agent turn could happen:
    /// it could not launch, or its <c>--resume</c> target was rejected
    /// (e.g. codex exits non-zero in ~0s, claude prints "No conversation
    /// found with session ID"). The run finished <c>failed</c> with no
    /// classifiable agent reply - only a CLI error fragment. This is a
    /// recoverable host/CLI condition, NOT an agent decision and NOT an
    /// unclassifiable agent reply. The runner has already marked the
    /// session chain for Recovery, so the policy routes this to the
    /// rebuild-from-disk path instead of surfacing a terminal
    /// <see cref="OrchestratorInconclusive"/> FAILURE.
    /// </summary>
    CliLaunchFailed,
    /// <summary>
    /// An OS / sandbox / host-permission error blocked the agent from
    /// making progress. Recognised in-stream by
    /// <see cref="AgentEnvironmentDetector"/>, which writes a synthetic
    /// <c>[environment-blocker]</c> marker the analyzer matches here.
    /// Distinct from <see cref="PermissionBlocked"/>: the agent cannot
    /// recover via a soft intervention, only the user can.
    /// </summary>
    EnvironmentBlocker,
    /// <summary>
    /// Legacy Codex silent-completion marker. Codex now completes through
    /// process exit with <c>exec --experimental-json</c>; this issue kind is
    /// kept so archived runs and transitional markers still render cleanly.
    /// </summary>
    SilentCompletion,
    /// <summary>
    /// The agent CLI rejected the request because the prompt / accumulated
    /// conversation context exceeded the model's input window ("Prompt too
    /// long", "context length exceeded", an HTTP 413 / request-too-large).
    /// This is NON-RETRYABLE by re-issue: resending the same (or larger)
    /// context overflows identically, which is exactly the endless-reissue
    /// loop this issue class exists to break. The orchestrator routes it
    /// straight to human review on first detection (like
    /// <see cref="EnvironmentBlocker"/>), never spending a retry.
    /// </summary>
    ContextOverflow,
    /// <summary>
    /// The per-task circuit breaker tripped: the same task produced
    /// <c>N</c> consecutive failed runs without any progress (no new commit
    /// / diff between attempts). Synthetic - set by the runner's breaker,
    /// not by <see cref="AgentOutcomeAnalyzer"/> - to stop an endless
    /// reissue/leave-in-progress loop and park the task in human review.
    /// </summary>
    Quarantined,
    /// <summary>
    /// The runner verified genuine git damage during the worker window: a
    /// protected remote branch changed with worker push evidence, or HEAD no
    /// longer descends from the run-start commit. A normal linear worker commit
    /// is an informational cleanup finding and does not use this issue kind.
    /// </summary>
    AgentGitViolation,
    /// <summary>
    /// The agent CLI rejected the run because the requested MODEL is invalid
    /// for this account/CLI: an <c>invalid_request_error</c> / HTTP 400 whose
    /// message says the model is not supported / does not exist (e.g. codex-cli
    /// with a ChatGPT account rejecting <c>gpt-5-codex</c>: "The 'gpt-5-codex'
    /// model is not supported when using Codex with a ChatGPT account"). This
    /// is NON-RETRYABLE by re-issue - resending the same model spawns straight
    /// back into the same 400 - and it is NOT an unclassifiable agent reply
    /// (the old orchestrator-inconclusive mislabel). The orchestrator routes it
    /// to human review with a <c>model-invalid</c> reason so the card clearly
    /// says the configured model must be changed. See AGT-1941 / the codex
    /// spawn-failure signature (AGT-1928/1929/1930/1936).
    /// </summary>
    ModelInvalid,
    /// <summary>
    /// The agent CLI rejected the run because the account's usage / session /
    /// rate-limit budget is exhausted ("You've hit your session limit", a
    /// <c>Rate limit · … · rejected</c> marker, "usage limit reached"). This is
    /// a TRANSIENT provider condition, not an agent decision and not an
    /// unclassifiable reply: it clears when the quota window resets. Typed
    /// distinctly so the escalation reason is an honest <c>quota-exhausted</c>
    /// (with the reset time when the CLI reported it) instead of the misleading
    /// <c>orchestrator-inconclusive</c>, and so the runner exempts it from the
    /// per-task no-progress quarantine streak. This is the AGT-1918/1919/1920
    /// signature (claude-sonnet-5 five-hour session limit on 2026-07-07).
    /// </summary>
    QuotaExhausted,
    /// <summary>
    /// A TRANSIENT environmental fault that is neither the task's code nor a
    /// provider account problem: a host file lock (the MSBuild <c>MSB3021</c> /
    /// <c>MSB3026</c> / <c>MSB3027</c> copy-lock family, "the process cannot
    /// access the file … because it is being used by another process") or a
    /// network glitch (DNS "could not resolve host" / "temporary failure in name
    /// resolution", <c>ECONNRESET</c> / <c>ETIMEDOUT</c> / <c>EAI_AGAIN</c>,
    /// "connection reset by peer", a 502/503/504 gateway blip). Unlike
    /// <see cref="EnvironmentBlocker"/> (host-permission / sandbox faults the
    /// agent cannot resolve and that only a human can fix) this class CLEARS on
    /// its own on a retry: the lock releases, the network recovers. So it is
    /// RETRYABLE - the orchestrator retries it with exponential backoff instead
    /// of escalating, and only routes it to human review (flagged
    /// <c>environmental</c>) once the bounded retry budget is spent. This is the
    /// MSB302x-lock / network signature from the post-processing outcome-taxonomy
    /// (AGT-1944; belege AGT-1945/1929/1930 backend-restart cycles).
    /// </summary>
    EnvironmentalTransient,
    /// <summary>
    /// The agent CLI could not launch because its OAuth session was expired and
    /// the token refresh failed ("OAuth session expired and could not be
    /// refreshed"). This is the AGT-2066 "token roulette" launch signature: a
    /// dead/rotated refresh token that no re-issue can revive, and - because
    /// every parallel run shares the same operator credential - one that fails
    /// EVERY launch identically until the credential is re-authenticated
    /// centrally. It is NON-RETRYABLE (unlike <see cref="CliLaunchFailed"/>,
    /// which rebuilds from disk and retries): retrying only burns launch budgets
    /// on a credential that is still dead. Typed distinctly so the orchestrator
    /// STOPS immediately with a clear re-auth instruction (the WÄCHTER / breaker
    /// half of the AGT-2066 fix) instead of the 17-cards-in-minutes cascade the
    /// 2026-07-10 incident produced, and so it is not mislabelled as a generic
    /// recoverable launch failure.
    /// </summary>
    AuthRefreshFailed
}

/// <summary>
/// Deterministic, side-effect-free description of how a CLI run ended.
/// <see cref="MatchedSentinel"/> is the load-bearing flag: when it is true
/// the orchestrator treats the result as authoritative; when it is false
/// the orchestrator falls back to heuristics and is required to surface a
/// warning so the user can see that the deterministic contract did not match.
/// </summary>
public sealed record AgentOutcome(
    AgentOutcomeKind Kind,
    string? Summary,
    bool MatchedSentinel,
    string? SentinelKeyword,
    string? Reason,
    int AgentTextChars,
    int OutputLineCount,
    double DurationSeconds)
{
    public RunIssueKind IssueKind { get; init; } = RunIssueKind.None;
}

/// <summary>
/// Pure analyzer that turns a finished CLI run into an <see cref="AgentOutcome"/>.
///
/// <para>
/// <b>Why this exists.</b> The product previously relied on prompt wording
/// to steer recovery and continuation behavior, then trusted whatever the
/// agent said back. When the agent silently no-op'd a follow-up after a
/// session loss (4.6 s exit, no real work, "task done"), the orchestrator
/// had no way to disagree. The analyzer pulls that decision into hardcoded
/// signal extraction so post-run policy can react deterministically.
/// </para>
///
/// <para>
/// <b>Signal hierarchy.</b>
/// <list type="number">
///   <item>Hard sentinels: bracket-tagged tokens the agent contract asks for
///   (<c>[[TASK_DONE]]</c>, <c>[[TASK_BLOCKED:&lt;reason&gt;]]</c>,
///   <c>[[TASK_NEEDS_INPUT:&lt;reason&gt;]]</c>, <c>[[TASK_NOOP]]</c>).
///   These are authoritative. The agent contract is documented in
///   <c>docs/system/contracts/agent-task.md</c>.</item>
///   <item>Empty fast exit: empty output buffer or no agent text plus a
///   sub-threshold duration. The CLI exited before an agent turn produced
///   reviewable output; this is a failed-start issue, not a no-op.</item>
///   <item>Heuristic regex: same shape as the frontend's
///   <c>agent-outcome.util.ts</c>. Used as a fallback so we never return
///   <see cref="AgentOutcomeKind.Unknown"/> when the text is informative.
///   Fallback matches must set <see cref="AgentOutcome.MatchedSentinel"/>
///   to false so the policy layer can warn.</item>
/// </list>
/// </para>
/// </summary>
public static class AgentOutcomeAnalyzer
{
    /// <summary>Sub-threshold duration below which a run with no agent text is treated as a failed start.</summary>
    public const double NoOpDurationThresholdSeconds = 10.0;

    /// <summary>
    /// Sub-threshold duration below which a <em>failed</em> run with no real
    /// agent turn is treated as a CLI launch / resume failure rather than an
    /// unclassifiable agent reply. A genuine agent turn takes meaningfully
    /// longer than a CLI that rejects its launch/resume arguments and exits
    /// immediately (the observed shape is ~0.0s, exit != 0).
    /// </summary>
    public const double CliLaunchFailureDurationThresholdSeconds = 3.0;

    /// <summary>
    /// Largest agent-text length still consistent with "no real agent turn"
    /// when a short CLI launch/resume error fragment leaked onto the agent
    /// stream. Above this we assume an actual turn happened and let the
    /// failed-with-text path classify it instead.
    /// </summary>
    private const int CliLaunchFailureMaxAgentTextChars = 200;

    /// <summary>
    /// Analyze a completed run. <paramref name="lines"/> is the full output
    /// buffer (the same shape <c>cli-output.log</c> persists). Status is the
    /// final <see cref="CliExecution.Status"/>; duration is the wall-clock
    /// run time in seconds.
    /// </summary>
    public static AgentOutcome Analyze(
        IReadOnlyList<CliOutputLine> lines,
        string status,
        double durationSeconds,
        int? exitCode = null)
    {
        lines ??= Array.Empty<CliOutputLine>();
        var agentText = JoinAgentText(lines);
        var rawText = JoinRawText(lines);
        var lineCount = lines.Count;

        // 1) Hard sentinels - authoritative. Walk from the end so a final
        //    sentinel beats earlier transient ones.
        var sentinel = FindLastSentinel(agentText);
        if (sentinel != null)
        {
            return new AgentOutcome(
                Kind: sentinel.Value.Kind,
                Summary: sentinel.Value.Summary,
                MatchedSentinel: true,
                SentinelKeyword: sentinel.Value.Keyword,
                Reason: sentinel.Value.Reason,
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds);
        }

        var failed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

        // Environment blockers win over permission-denied: the runtime
        // recogniser only fires on host-level failures the agent has no
        // way to resolve (sandbox config, logon session, ACL), and the
        // base class has already killed the process. Route as a typed
        // EnvironmentBlocker so the policy layer does not waste a soft
        // intervention asking the agent to "try again with available
        // permissions".
        var envDiagnosis = ExtractEnvironmentBlockerDiagnosis(rawText);
        if (envDiagnosis != null)
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: envDiagnosis,
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "environment blocker detected in CLI output",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.EnvironmentBlocker };
        }

        // Codex silent-completion: the per-tick detector wrote a
        // [codex-silent-completion] marker and asked the base class to stop
        // the process. The marker is the only signal we trust here (gated
        // the same way as [environment-blocker]); a false positive is
        // impossible without the live detector having fired. Classify as
        // AgentOutcomeKind.Done because the on-disk evidence usually shows
        // the work happened, but keep MatchedSentinel=false so the chat
        // surface plus auto-review still highlight the missing sign-off.
        var silentDiagnosis = ExtractSilentCompletionDiagnosis(rawText);
        if (silentDiagnosis != null)
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Done,
                Summary: silentDiagnosis,
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "codex silent-completion marker observed in CLI output",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.SilentCompletion };
        }

        if (IsPermissionBlocked(rawText))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: "Tool permission failure prevented the agent from using the requested command or path.",
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "permission denied during tool execution",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.PermissionBlocked };
        }

        if (IsWatchdogTimeout(rawText))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: "Run was killed by the watchdog after a silence timeout.",
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "watchdog timeout",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.WatchdogTimeout };
        }

        // 1.4) Context-overflow. A failed run whose output carries a
        //    prompt-too-long / context-length / request-too-large signal is
        //    NON-RETRYABLE: re-issuing the same (or a larger) context
        //    overflows identically. This is the exact shape behind the
        //    endless-reissue loop (a "Prompt too long" failure was being
        //    classified as classifier-unknown and re-issued forever). Gated
        //    on `failed` so an agent quoting one of these phrases mid-success
        //    is unaffected, and checked BEFORE the CLI-launch / heuristic
        //    paths so it wins over the generic classifier-unknown route.
        if (failed && IsContextOverflow(rawText))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: "The agent CLI rejected the run because the prompt/context exceeded the model's input window (prompt too long / context length). Re-issuing would overflow identically.",
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "context overflow detected in CLI output",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.ContextOverflow };
        }

        // 1.42) Model-invalid. A failed run whose output carries a provider
        //    model-rejection (invalid_request_error / HTTP 400 "model is not
        //    supported / does not exist") is NON-RETRYABLE: the requested model
        //    is wrong for this account/CLI, so re-issuing spawns into the same
        //    400. This is the codex ChatGPT-account signature (gpt-5-codex
        //    rejected). Typed so the escalation reason is a clear "model-invalid"
        //    instead of the catch-all orchestrator-inconclusive, and checked
        //    BEFORE the CLI-launch / heuristic paths so it wins. Gated on
        //    `failed` so an agent quoting one of these phrases mid-success is
        //    unaffected.
        if (failed && IsModelInvalid(rawText))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: BuildModelInvalidSummary(rawText),
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "model rejected by the provider (invalid_request / model not supported)",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.ModelInvalid };
        }

        // 1.45) Quota / session-limit exhaustion. A failed run whose output
        //    carries a usage/session/rate-limit-exhausted signal ("You've hit
        //    your session limit", a `Rate limit · … · rejected` marker) is a
        //    TRANSIENT provider condition that clears when the quota window
        //    resets - not an unclassifiable agent reply. Typed so the escalation
        //    reason is an honest "quota-exhausted" (carrying the reported reset
        //    time) instead of orchestrator-inconclusive, and so the runner's
        //    rate-limit cooldown / quarantine exemption treat it as transient.
        //    Gated on `failed`, and checked BEFORE the CLI-launch / heuristic
        //    paths so it is not swallowed by them. NOTE: claude emits a benign
        //    `● Rate limit · … · allowed` telemetry marker on healthy runs too,
        //    so the needles below match only the EXHAUSTED shapes.
        if (failed && IsQuotaExhausted(rawText))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: BuildQuotaExhaustedSummary(rawText),
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "provider usage/session/rate limit exhausted",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.QuotaExhausted };
        }

        // 1.48) Transient environmental fault. A failed run whose output carries
        //    a host file-lock (MSB302x copy-lock family / "being used by another
        //    process") or a network glitch (DNS failure, ECONNRESET/ETIMEDOUT,
        //    502/503/504) signal is TRANSIENT: unlike an EnvironmentBlocker the
        //    condition clears on its own, so re-running the same task after a
        //    short backoff usually succeeds. Typed distinctly so the runner
        //    retries it with backoff instead of escalating, and so it does not
        //    accrue toward the per-task no-progress quarantine streak. Gated on
        //    `failed`, and checked BEFORE the CLI-launch / heuristic paths so it
        //    is not swallowed by them. Kept below quota/model so a provider
        //    account problem is not mislabelled as a transient blip.
        if (failed && IsEnvironmentalTransient(rawText))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: BuildEnvironmentalTransientSummary(rawText),
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "transient environmental fault (host file lock / network) detected in CLI output",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.EnvironmentalTransient };
        }

        // 1.49) OAuth-refresh launch failure (AGT-2066 WÄCHTER). A failed run
        //    whose output carries the "OAuth session expired and could not be
        //    refreshed" signature is the token-roulette launch death: a dead /
        //    rotated refresh token no re-issue can revive, shared across every
        //    parallel run, so it fails EVERY launch identically until the
        //    operator re-authenticates centrally. Checked BEFORE the generic
        //    CliLaunchFailed branch (1.5) - which would otherwise swallow this
        //    fast, short failure and RETRY it - so the breaker wins: the policy
        //    stops immediately with a re-auth instruction instead of burning
        //    launch budgets (17 cards in minutes, incident 2026-07-10). Gated on
        //    `failed` so an agent quoting the phrase in a healthy turn is unaffected.
        if (failed && IsAuthRefreshFailure(rawText))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: BuildAuthRefreshFailureSummary(rawText),
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "OAuth session expired and could not be refreshed before an agent turn (AGT-2066)",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.AuthRefreshFailed };
        }

        // 1.5) CLI launch / resume failure. A failed run that produced no
        //    real agent turn - it died almost instantly (~0s) or the only
        //    output is a recognised CLI launch/resume error fragment (e.g.
        //    codex rejected the --resume target and exited 2 in 0.0s) - is a
        //    host/CLI failure, not an unclassifiable agent reply. Routed to
        //    the typed CliLaunchFailed issue so the policy rebuilds from disk
        //    via Recovery instead of dead-ending in a terminal
        //    classifier-unknown FAILURE. Gated on `failed` so a healthy run
        //    that merely mentions a session id in its prose is unaffected.
        if (failed && IsCliLaunchOrResumeFailure(rawText, agentText, durationSeconds))
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: BuildCliLaunchFailureSummary(rawText),
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "CLI launch/resume failed before an agent turn produced classifiable output",
                AgentTextChars: agentText.Length,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.CliLaunchFailed };
        }

        // 2) Empty fast exit - the CLI exited without producing anything the
        //    user can review. This used to masquerade as NoOp, but an actual
        //    no-op is an explicit agent verdict. Empty+fast is a failed-start
        //    shape: spawn/env/quota/immediate-exit until proven otherwise.
        if (!failed && agentText.Length == 0 && durationSeconds < NoOpDurationThresholdSeconds)
        {
            return new AgentOutcome(
                Kind: AgentOutcomeKind.Unknown,
                Summary: BuildEmptyFastExitSummary(rawText, status, durationSeconds, exitCode, lineCount),
                MatchedSentinel: false,
                SentinelKeyword: null,
                Reason: "CLI exited before producing an agent turn",
                AgentTextChars: 0,
                OutputLineCount: lineCount,
                DurationSeconds: durationSeconds)
            { IssueKind = RunIssueKind.EmptyFastExit };
        }

        // 3) Heuristic regex fallback over the tail of the agent text. Mirrors
        //    the frontend's classifier so the orchestrator and the UI agree
        //    on what "done" / "blocked" / "needs-input" mean today.
        var (heuristicKind, heuristicSummary) = HeuristicClassify(agentText);
        var issue = ResolveIssueKind(heuristicKind, agentText.Length, failed, exitCode);
        return new AgentOutcome(
            Kind: heuristicKind,
            Summary: heuristicSummary,
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: heuristicKind == AgentOutcomeKind.Unknown
                ? "no sentinel matched, heuristic also inconclusive"
                : "no sentinel matched, heuristic fallback",
            AgentTextChars: agentText.Length,
            OutputLineCount: lineCount,
            DurationSeconds: durationSeconds)
        { IssueKind = issue };
    }

    // Sentinel format: [[TASK_<KEYWORD>]] or [[TASK_<KEYWORD>:reason text]].
    // Kept loose on whitespace, separators, and case so a model that emits
    // the marker as `[[ TASK DONE ]]`, `[[TASK-DONE]]`, or with spaces around
    // the reason separator still matches. The actual keyword set is small and
    // explicit.
    //
    // Public so callers that need to scan a buffer for live decision sentinels
    // (the continuous-decision scanner, post-run policy, supervisor parsing)
    // share one grammar. ADR-0002 anchors the deterministic-orchestration
    // philosophy on a single sentinel regex; this is the single source of truth
    // referenced from AGENTS.md and docs/system/contracts/agent-task.md.
    public static readonly Regex SentinelRegex = new(
        @"\[\[\s*TASK[\s_-]*(?<keyword>DONE|BLOCKED|NEEDS[\s_-]*INPUT|NOOP)\s*(?::\s*(?<reason>[^\]]*?))?\s*\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Tolerant fallback for the real near-miss sentinel forms agents emit when
    /// they do not reproduce the canonical <c>[[TASK_DONE]]</c> shape exactly:
    /// a single bracket pair (<c>[TASK_DONE]</c>), no brackets at all
    /// (<c>TASK_DONE</c>), or the token wrapped in markdown decoration
    /// (<c>**[[TASK_DONE]]**</c>, <c>> [[TASK_BLOCKED: …]]</c>, a list bullet).
    /// This is the load-bearing widening for the systemic
    /// <c>missing-terminal-sentinel</c> / <c>classifier-unknown</c> failure mode:
    /// a malformed-but-unambiguous sign-off should be treated as authoritative,
    /// not dropped to the heuristic layer.
    ///
    /// <para>
    /// False positives are contained by anchoring the whole token to a single
    /// line (<see cref="RegexOptions.Multiline"/> <c>^…$</c>): the only
    /// non-bracket text allowed around the token is markdown/quote decoration
    /// and whitespace, so prose like "the task is done" or "I'll emit TASK_DONE
    /// when finished" cannot match (those lines carry other words). The strict
    /// double-bracket <see cref="SentinelRegex"/> is tried first and stays the
    /// canonical contract; this regex only runs when it found nothing. The
    /// public <see cref="SentinelRegex"/> is intentionally left strict so the
    /// live-stream decision scanner and supervisor parsing are unaffected.
    /// </para>
    /// </summary>
    private static readonly Regex TolerantSentinelRegex = new(
        @"^[`*_>\-\s]*\[{0,2}\s*TASK[\s_-]*(?<keyword>DONE|BLOCKED|NEEDS[\s_-]*INPUT|NOOP)\s*(?::\s*(?<reason>[^\]\r\n]*?))?\s*\]{0,2}[`*_\s]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static (AgentOutcomeKind Kind, string Keyword, string? Reason, string Summary)? FindLastSentinel(string agentText)
    {
        if (string.IsNullOrEmpty(agentText)) return null;

        // Strict, canonical [[TASK_…]] form wins and is checked first.
        var match = LastMatchOrNull(SentinelRegex.Matches(agentText));
        // Fall back to the tolerant line-anchored form so a single-bracket or
        // bare-token sign-off still counts as an authoritative sentinel.
        match ??= LastMatchOrNull(TolerantSentinelRegex.Matches(agentText));
        if (match == null) return null;

        var keyword = Regex.Replace(match.Groups["keyword"].Value, @"[\s_-]+", "_").ToUpperInvariant();
        var reason = match.Groups["reason"].Success ? match.Groups["reason"].Value.Trim() : null;
        if (string.IsNullOrWhiteSpace(reason)) reason = null;
        return keyword switch
        {
            "DONE"        => (AgentOutcomeKind.Done, keyword, reason, "Agent emitted [[TASK_DONE]]."),
            "BLOCKED"     => (AgentOutcomeKind.Blocked, keyword, reason, $"Agent emitted [[TASK_BLOCKED]]{(reason != null ? $": {reason}" : "")}."),
            "NEEDS_INPUT" => (AgentOutcomeKind.NeedsInput, keyword, reason, $"Agent emitted [[TASK_NEEDS_INPUT]]{(reason != null ? $": {reason}" : "")}."),
            "NOOP"        => (AgentOutcomeKind.NoOp, keyword, reason, "Agent emitted [[TASK_NOOP]]."),
            _             => null
        };
    }

    private static Match? LastMatchOrNull(MatchCollection matches)
        => matches.Count == 0 ? null : matches[^1];

    // The done/blocked/needs-input shapes mirror the real tail-of-reply prose
    // claude and codex produce when they finish without a parseable sentinel.
    // Widened past the original verb list (ASS-643) so the common
    // summary-style sign-offs ("Summary of changes", "Here's what I did",
    // "I've refactored…", "all tests pass", a leading ✓/✅) classify as Done
    // instead of dropping to Unknown -> classifier-unknown.
    private static readonly Regex DonePattern = new(
        @"\b(committ?ed|merged|landed|shipped|deployed|fixed|resolved|implemented|completed|finished|done|ready\s+for\s+review|verif(?:ied|ication)|validated|tests?\s+(?:run|pass(?:ed|ing)?|green)|build\s+(?:succeeds?|passes|green)|changed|updated|added|created|wrote|refactored|removed|renamed|migrated|configured|replaced|extracted|introduced|documented|here'?s\s+what|summary\s+of\s+(?:changes|the\s+changes)|i'?ve\s+(?:made|added|implemented|updated|fixed|created|refactored|removed)|i\s+have\s+(?:made|added|implemented|updated|fixed)|successfully)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DoneCheckmarkPattern = new(
        @"[✓✔✅]",
        RegexOptions.Compiled);

    private static readonly Regex BlockedPattern = new(
        @"\b(cannot\s+(?:proceed|continue|find|access|determine|complete)|could\s+not\s+(?:proceed|continue|complete)|blocked\s+by|i'?m\s+blocked|unable\s+to|do(?:\s+not|n'?t)\s+have\s+(?:access|permission)|no\s+permission\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NeedsInputPattern = new(
        @"\b(?:please\s+(?:provide|share|paste|attach|specify|clarify|confirm|let\s+me\s+know)|which\s+(?:one|file|option|approach)|do\s+you\s+want|should\s+I|would\s+you\s+like|what\s+would\s+you\s+like|how\s+would\s+you\s+like|let\s+me\s+know|i'?ll\s+wait\s+for|waiting\s+for\s+your)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProgressPattern = new(
        @"\b(starting|working|investigating|reading|searching|exploring|analy[sz]ing|building|running)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (AgentOutcomeKind Kind, string Summary) HeuristicClassify(string agentText)
    {
        if (string.IsNullOrWhiteSpace(agentText))
            return (AgentOutcomeKind.Unknown, "No agent text to classify.");

        var tail = TailLines(agentText, 6);
        var endsWithQuestion = tail.TrimEnd().EndsWith("?", StringComparison.Ordinal);
        if (endsWithQuestion || NeedsInputPattern.IsMatch(tail))
            return (AgentOutcomeKind.NeedsInput, "Agent appears to be waiting for input (heuristic).");
        var doneTail = TailLines(agentText, 8);
        if (DonePattern.IsMatch(doneTail) || DoneCheckmarkPattern.IsMatch(doneTail))
            return (AgentOutcomeKind.Done, "Agent text suggests the task is done (heuristic).");
        if (BlockedPattern.IsMatch(tail))
            return (AgentOutcomeKind.Blocked, "Agent text suggests the task is blocked (heuristic).");
        if (ProgressPattern.IsMatch(tail))
            return (AgentOutcomeKind.Progress, "Agent text suggests it is mid-task (heuristic).");
        // B (operator directive 2026-06-08, broken-commit-pipeline incident):
        // do NOT strand a substantial reply on Unknown. A run that produced a
        // real reply but no parseable verdict (a shape this codex-tuned matcher
        // misses for claude/other CLIs) has almost always done work. The safe,
        // CLI-agnostic default is to treat it as Done and let it flow to review
        // — where the now-non-optional commit step captures the work and a
        // human/orchestrator has the final say — instead of spinning an
        // inconclusive reissue loop that leaves the work uncommitted in
        // the worktree. Only a short, contentless reply stays Unknown.
        if (agentText.Trim().Length >= 400)
            return (AgentOutcomeKind.Done, "Substantial reply without a parseable verdict; treating as done for review (heuristic).");
        return (AgentOutcomeKind.Unknown, "Agent text did not match any known shape.");
    }

    private static RunIssueKind ResolveIssueKind(AgentOutcomeKind kind, int agentTextChars, bool failed, int? exitCode)
    {
        if (agentTextChars == 0) return failed ? RunIssueKind.NoAgentOutput : RunIssueKind.None;
        // A failed run with real agent text and no sentinel (sentinels return
        // early in Analyze) splits two ways instead of the old blanket
        // ClassifierUnknown: a hard process death (exitCode < 0, the Windows
        // Process.Kill artifact) is an InfraCrash; anything else is an honest
        // OrchestratorInconclusive logical failure. Both stop and surface for
        // review; neither is ever silently accepted (drive-to-conclusion model).
        if (failed)
            return exitCode is < 0 ? RunIssueKind.InfraCrash : RunIssueKind.OrchestratorInconclusive;
        return kind switch
        {
            AgentOutcomeKind.Done    => RunIssueKind.MissingTerminalSentinel,
            AgentOutcomeKind.Unknown => RunIssueKind.MissingTerminalSentinel,
            _                        => RunIssueKind.None
        };
    }

    /// <summary>
    /// Pull the diagnosis text out of the synthetic
    /// <c>[environment-blocker] &lt;diagnosis&gt;</c> system line written by
    /// <c>GenericCliExecutionService.CheckEnvironmentBlocker</c>. Returns null
    /// when the run did not trip the detector. The marker is the only
    /// signal the analyzer trusts here: the underlying needles (codex
    /// sandbox text, EPERM, etc.) can appear inside an agent's own prose
    /// (paste of an error message, post-mortem). Gating on the synthetic
    /// marker means a false positive is impossible without the runtime
    /// detector itself firing.
    /// </summary>
    private static string? ExtractEnvironmentBlockerDiagnosis(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return null;
        const string marker = "[environment-blocker]";
        var idx = rawText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var afterMarker = rawText[(idx + marker.Length)..];
        var newline = afterMarker.IndexOf('\n');
        var slice = newline >= 0 ? afterMarker[..newline] : afterMarker;
        var trimmed = slice.Trim();
        return trimmed.Length == 0 ? "environment blocker detected" : trimmed;
    }

    /// <summary>
    /// Pull the diagnosis text out of the synthetic
    /// <c>[codex-silent-completion] &lt;diagnosis&gt;</c> system line written
    /// by <c>ProjectRunner.TickSilentCompletion</c> when Codex went stale
    /// after a successful tool call. Same gating shape as
    /// <see cref="ExtractEnvironmentBlockerDiagnosis"/>: a false positive is
    /// impossible without the runtime detector having fired and added the
    /// marker line itself.
    /// </summary>
    private static string? ExtractSilentCompletionDiagnosis(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return null;
        const string marker = "[codex-silent-completion]";
        var idx = rawText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var afterMarker = rawText[(idx + marker.Length)..];
        var newline = afterMarker.IndexOf('\n');
        var slice = newline >= 0 ? afterMarker[..newline] : afterMarker;
        var trimmed = slice.Trim();
        return trimmed.Length == 0 ? "Codex stopped after final tool call without a closing sentinel." : trimmed;
    }

    private static bool IsPermissionBlocked(string text)
        => !string.IsNullOrWhiteSpace(text)
           && (text.Contains("Permission denied and could not request permission from user", StringComparison.OrdinalIgnoreCase)
               || text.Contains("could not request permission from user", StringComparison.OrdinalIgnoreCase));

    private static bool IsWatchdogTimeout(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Operator-friendly form: `[watchdog-timeout] "..." (cli): auto-cancelled after Ns of silence.`
        if (text.Contains("[watchdog-timeout]", StringComparison.OrdinalIgnoreCase)) return true;
        // Legacy `[watchdog] Killed after Ns of silence.` shape still matches
        // on archived logs so historical jobs classify the same way.
        return text.Contains("[watchdog]", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("Killed after", StringComparison.OrdinalIgnoreCase)
                || text.Contains("auto-cancelled after", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Recognised CLI launch / resume error fragments. These are emitted by
    /// the CLI itself or by the runner's capture-fail decision message, never
    /// by an agent mid-task, so a match is a definitive launch/resume-failure
    /// signal regardless of duration. Kept short and specific to avoid
    /// matching an agent's own prose.
    /// </summary>
    private static readonly string[] CliLaunchFailureNeedles =
    {
        "rejected the resume target",
        "rebuild from disk",
        "no conversation found with session id",
        "session id not found",
    };

    /// <summary>
    /// A <em>failed</em> run is a CLI launch / resume failure when either a
    /// recognised CLI error fragment is present (definitive) or the run died
    /// almost instantly without producing a real agent turn (no/short agent
    /// text plus sub-threshold duration). Callers must only invoke this when
    /// the run status is failed.
    /// </summary>
    private static bool IsCliLaunchOrResumeFailure(string rawText, string agentText, double durationSeconds)
    {
        if (HasCliLaunchFailureNeedle(rawText)) return true;
        return durationSeconds < CliLaunchFailureDurationThresholdSeconds
            && agentText.Length < CliLaunchFailureMaxAgentTextChars;
    }

    /// <summary>
    /// Phrases an agent CLI / model API emits when the prompt or accumulated
    /// conversation context is larger than the model's input window. Kept
    /// specific so a match (combined with a <c>failed</c> status) is an
    /// unambiguous context-overflow signal rather than incidental prose. The
    /// canonical claude form is the bare "Prompt too long" carried in the
    /// failing <c>result</c> frame; the others cover codex/gemini and the
    /// underlying provider API messages.
    /// </summary>
    private static readonly string[] ContextOverflowNeedles =
    {
        "prompt too long",
        "prompt is too long",
        "input is too long",
        "context length exceeded",
        "maximum context length",
        "context window exceeded",
        "exceeds the context window",
        "exceeds the maximum context",
        "too many tokens",
        "request too large",
        "payload too large",
        "context_length_exceeded",
        "prompt_too_long",
    };

    /// <summary>
    /// True when the run output carries a recognised context-overflow signal.
    /// Callers must only invoke this for a <c>failed</c> run so an agent that
    /// merely discusses token limits in a healthy turn does not trip it.
    /// </summary>
    private static bool IsContextOverflow(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (var needle in ContextOverflowNeedles)
        {
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasCliLaunchFailureNeedle(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (var needle in CliLaunchFailureNeedles)
        {
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Phrases a CLI emits when it cannot launch because its OAuth session has
    /// expired and the token refresh failed. The canonical claude form is the
    /// exact incident line "OAuth session expired and could not be refreshed";
    /// the shorter fragments cover the codex/gemini and provider variants ("could
    /// not be refreshed", "failed to refresh"/"unable to refresh" the token/
    /// session/credentials). Kept specific - and gated on a <c>failed</c> status
    /// at the call site - so an agent quoting one of these phrases in a healthy
    /// turn does not trip the breaker. This is the AGT-2066 token-roulette
    /// launch signature.
    /// </summary>
    private static readonly string[] AuthRefreshFailureNeedles =
    {
        "oauth session expired and could not be refreshed",
        "session expired and could not be refreshed",
        "could not be refreshed",
        "oauth token expired",
        "failed to refresh the oauth",
        "failed to refresh oauth token",
        "unable to refresh the oauth",
        "credentials could not be refreshed",
        "token refresh failed",
    };

    /// <summary>
    /// True when the run output carries a recognised OAuth-refresh-failure
    /// signal. Callers must only invoke this for a <c>failed</c> run so an agent
    /// that merely discusses token refresh in a healthy turn does not trip it.
    /// </summary>
    private static bool IsAuthRefreshFailure(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (var needle in AuthRefreshFailureNeedles)
        {
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string BuildAuthRefreshFailureSummary(string rawText)
    {
        var detail = ExtractFirstMatchingLine(rawText, "could not be refreshed")
                     ?? ExtractFirstMatchingLine(rawText, "refresh");
        var head = detail != null
            ? $"The agent CLI could not launch: its OAuth session expired and the token refresh failed (\"{detail}\")."
            : "The agent CLI could not launch: its OAuth session expired and the token refresh failed.";
        return head
            + " This is NON-RETRYABLE by re-issue and shared across every parallel run: the credential"
            + " is dead until it is re-authenticated centrally, so further launches would fail identically."
            + " Re-auth the CLI from the operator shell (refresh ~/.claude/.credentials.json), then re-queue (AGT-2066).";
    }

    /// <summary>
    /// Phrases a CLI / provider emits when the requested MODEL is rejected as
    /// invalid for the account or CLI. Kept specific so a match (combined with a
    /// <c>failed</c> status) is an unambiguous model-rejection rather than
    /// incidental prose. The canonical codex ChatGPT-account form is
    /// <c>invalid_request_error</c> + "model is not supported when using Codex";
    /// the others cover the OpenAI/Anthropic "model does not exist / not found"
    /// and generic unsupported-model API messages.
    /// </summary>
    private static readonly string[] ModelInvalidNeedles =
    {
        "is not supported when using",
        "model is not supported",
        "not a supported model",
        "unsupported model",
        "model_not_found",
        "does not exist or you do not have access",
        "invalid model",
        "unknown model",
    };

    /// <summary>
    /// True when the run output carries a recognised model-rejection signal.
    /// Callers must only invoke this for a <c>failed</c> run so an agent that
    /// merely discusses model names in a healthy turn does not trip it. The
    /// bare <c>invalid_request_error</c> token also counts, but only when it
    /// co-occurs with a "model" mention, so an unrelated 400 (bad param) is not
    /// mislabelled as a model problem.
    /// </summary>
    private static bool IsModelInvalid(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (var needle in ModelInvalidNeedles)
        {
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return rawText.Contains("invalid_request_error", StringComparison.OrdinalIgnoreCase)
            && rawText.Contains("model", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildModelInvalidSummary(string rawText)
    {
        var detail = ExtractProviderErrorMessage(rawText);
        return detail != null
            ? $"The agent CLI rejected the run: the configured model is invalid for this account/CLI. {detail} Change the model on the task; re-issuing the same model will fail identically."
            : "The agent CLI rejected the run because the configured model is invalid/unsupported for this account or CLI. Change the model on the task; re-issuing the same model will fail identically.";
    }

    /// <summary>
    /// Phrases a CLI / provider emits when the account's usage / session /
    /// rate-limit budget is exhausted. Kept to the EXHAUSTED shapes so the
    /// benign per-run <c>● Rate limit · … · allowed</c> telemetry claude prints
    /// on healthy runs does not trip it. Callers must only invoke this for a
    /// <c>failed</c> run.
    /// </summary>
    private static readonly string[] QuotaExhaustedNeedles =
    {
        "hit your session limit",
        "session limit reached",
        "usage limit reached",
        "you've reached your usage limit",
        "quota exceeded",
        "rate limit exceeded",
        "rate_limit_exceeded",
        "· rejected ·",
        "status=rejected",
        "insufficient_quota",
    };

    private static bool IsQuotaExhausted(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (var needle in QuotaExhaustedNeedles)
        {
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string BuildQuotaExhaustedSummary(string rawText)
    {
        var reset = ExtractFirstMatchingLine(rawText, "session limit")
                    ?? ExtractFirstMatchingLine(rawText, "usage limit")
                    ?? ExtractFirstMatchingLine(rawText, "rate limit");
        return reset != null
            ? $"The agent CLI rejected the run: the account's usage/session budget is exhausted. \"{reset}\" This is transient - re-queue after the quota window resets."
            : "The agent CLI rejected the run because the account's usage/session/rate-limit budget is exhausted. This is transient - re-queue after the quota window resets.";
    }

    /// <summary>
    /// Host file-lock signatures. The MSBuild copy-lock family (MSB3021 "Unable
    /// to copy file", MSB3026/MSB3027 "Could not copy … exceeded retry count")
    /// and the underlying Windows sharing-violation text all mean a build output
    /// was momentarily locked by a lingering process (an antivirus scan, a
    /// still-exiting test host, a parallel build). The lock releases on its own,
    /// so a retry after a short backoff clears it. Kept specific so a match is an
    /// unambiguous transient signal, not incidental prose.
    /// </summary>
    private static readonly string[] HostFileLockNeedles =
    {
        "msb3021",
        "msb3026",
        "msb3027",
        "being used by another process",
        "the process cannot access the file",
        "used by another process",
        "sharing violation",
    };

    /// <summary>
    /// Network-glitch signatures. DNS resolution failures, reset/timed-out
    /// sockets, and transient 5xx gateway responses are host/provider blips that
    /// clear on their own, so a retry usually succeeds. Kept specific (concrete
    /// error codes / phrases, not the bare word "network") so a match combined
    /// with a <c>failed</c> status is an unambiguous transient signal.
    /// </summary>
    private static readonly string[] NetworkGlitchNeedles =
    {
        "temporary failure in name resolution",
        "could not resolve host",
        "name or service not known",
        "getaddrinfo enotfound",
        "getaddrinfo eai_again",
        "eai_again",
        "econnreset",
        "connection reset by peer",
        "etimedout",
        "connection timed out",
        "enetunreach",
        "network is unreachable",
        "econnrefused",
        "connection refused",
        "socket hang up",
        "tls handshake timeout",
        "502 bad gateway",
        "503 service unavailable",
        "504 gateway time-out",
        "504 gateway timeout",
    };

    /// <summary>
    /// Torn-down-/tmp signatures (AGT-2750). A <c>PrivateTmp=true</c> unit
    /// restart deletes the running worker's private <c>/tmp</c> mount while
    /// <c>KillMode=process</c> deliberately leaves it running. MSBuild's node
    /// communication pipe and NuGet's global mutex mkdtemp both fail against
    /// the deleted mount roughly an hour later, well after the daemon that
    /// tore the mount down has forgotten it restarted. Kept specific (exact
    /// MSBuild/NuGet error codes and the literal dotnet mkdtemp prefix) so a
    /// match is an unambiguous host/unit-restart signal, not a real build
    /// break in the change under test.
    /// </summary>
    private static readonly string[] TmpMountTeardownNeedles =
    {
        "msb1025",
        "socketexception (99)",
        "mkdtemp(\"/tmp/.dotnet.",
    };

    /// <summary>
    /// True when the run output carries a recognised transient environmental
    /// signal (host file lock, network glitch, or torn-down /tmp mount).
    /// Callers must only invoke this for a <c>failed</c> run so an agent that
    /// merely quotes one of these phrases in a healthy turn does not trip it.
    /// </summary>
    private static bool IsEnvironmentalTransient(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (var needle in HostFileLockNeedles)
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var needle in NetworkGlitchNeedles)
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var needle in TmpMountTeardownNeedles)
            if (rawText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string BuildEnvironmentalTransientSummary(string rawText)
    {
        var isLock = HostFileLockNeedles.Any(n => rawText.Contains(n, StringComparison.OrdinalIgnoreCase));
        var isTmpTeardown = !isLock
            && TmpMountTeardownNeedles.Any(n => rawText.Contains(n, StringComparison.OrdinalIgnoreCase));
        var kind = isLock
            ? "a host file lock (MSB302x / file-in-use)"
            : isTmpTeardown
                ? "a torn-down /tmp mount (unit restart mid-build; MSB1025 / SocketException (99) / NuGet mkdtemp ENOENT)"
                : "a network glitch (DNS / reset / gateway)";
        var detail = ExtractFirstMatchingLine(
                rawText,
                isLock ? "process cannot access" : isTmpTeardown ? "msb1025" : "resolve")
            ?? ExtractFirstMatchingLine(rawText, isLock ? "msb30" : isTmpTeardown ? "mkdtemp" : "econn");
        return detail != null
            ? $"The run failed on {kind}: \"{detail}\" This is transient - the orchestrator retries it with backoff before escalating."
            : $"The run failed on {kind}. This is transient - the orchestrator retries it with backoff before escalating.";
    }

    /// <summary>Pull a provider error <c>message</c> value out of a JSON-ish
    /// error frame (<c>"message":"…"</c>), for the human-readable summary.</summary>
    private static string? ExtractProviderErrorMessage(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            rawText, "\"message\"\\s*:\\s*\"(?<msg>[^\"]{1,300})\"");
        return m.Success ? m.Groups["msg"].Value.Trim() : null;
    }

    private static string? ExtractFirstMatchingLine(string text, string needle)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = line.Trim();
                return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "...";
            }
        }
        return null;
    }

    private static string BuildCliLaunchFailureSummary(string rawText)
        => HasCliLaunchFailureNeedle(rawText)
            ? "The agent CLI rejected the resume target; rebuilding from disk on the next attempt."
            : "The agent CLI failed to launch or resume before producing any agent output; rebuilding from disk on the next attempt.";

    private static string BuildEmptyFastExitSummary(
        string rawText,
        string status,
        double durationSeconds,
        int? exitCode,
        int outputLineCount)
    {
        var details = new List<string>
        {
            $"status={NormalizeDetail(status, "unknown")}",
            $"exitCode={exitCode?.ToString() ?? "unknown"}",
            $"duration={durationSeconds:F1}s",
            $"outputLines={outputLineCount}"
        };

        var marker = DetectEmptyFastExitMarker(rawText);
        if (marker != null) details.Add($"marker={marker}");

        var stderr = ExtractFirstNonEmptyLine(rawText);
        if (stderr != null) details.Add($"firstOutput={stderr}");

        return "The agent CLI exited almost immediately without producing an agent turn; treating this as a failed start, not as [[TASK_NOOP]]. "
             + string.Join("; ", details) + ".";
    }

    private static string NormalizeDetail(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? ExtractFirstNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            return trimmed.Length <= 160 ? trimmed : trimmed[..160] + "...";
        }
        return null;
    }

    private static string? DetectEmptyFastExitMarker(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        if (rawText.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("rate-limit", StringComparison.OrdinalIgnoreCase))
            return "quota-or-rate-limit";
        if (rawText.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("CreateProcessAsUser", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("EPERM", StringComparison.OrdinalIgnoreCase))
            return "sandbox-or-host";
        if (rawText.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || rawText.Contains("login", StringComparison.OrdinalIgnoreCase))
            return "auth";
        return null;
    }

    /// <summary>
    /// Joins the parts of the buffer that look like agent (assistant) text.
    /// We exclude lines from the <c>system</c> stream (taskboard markers and
    /// orchestrator meta messages), the <c>user</c> stream (the user's own
    /// follow-ups echoed into the log), and <c>stderr</c> (process diagnostics)
    /// so the analysis only sees what the agent itself produced.
    /// </summary>
    private static string JoinAgentText(IReadOnlyList<CliOutputLine> lines)
    {
        var parts = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (line == null) continue;
            var stream = line.Stream ?? string.Empty;
            if (string.Equals(stream, "system", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "user", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "orchestrator", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(line.Text)) parts.Add(line.Text);
        }
        return string.Join("\n", parts).Trim();
    }

    private static string JoinRawText(IReadOnlyList<CliOutputLine> lines)
        => string.Join("\n", lines.Where(l => l != null).Select(l => l.Text ?? string.Empty)).Trim();

    private static string TailLines(string text, int count)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n');
        var startIndex = Math.Max(0, lines.Length - count);
        return string.Join("\n", lines, startIndex, lines.Length - startIndex);
    }
}
