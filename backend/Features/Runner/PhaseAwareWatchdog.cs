using System.Linq;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Per-phase silence budgets. Different phases tolerate different silences:
/// the spawn -&gt; first-event window must be tight (a fast handshake) while
/// a tool-execution window may legitimately last several minutes (a Bash
/// build, a slow grep over a large repo). The original silence-only
/// watchdog ([Watchdog]) treats all silences the same; that produced
/// false positives during long tool runs and false negatives on the
/// "init then nothing" hang shape.
///
/// <para>
/// The numbers below are deliberate: they map to user-observable
/// behaviour rather than to any one CLI's internal pacing. Adjust via
/// <c>Watchdog:Phase:&lt;phase&gt;:&lt;Suspicious|Hung&gt;Seconds</c> in
/// configuration when a per-CLI calibration is needed.
/// </para>
/// </summary>
public sealed record PhaseBudget(
    double SuspiciousSeconds,
    double HungSeconds)
{
    public static PhaseBudget For(RunPhase phase) => phase switch
    {
        // Spawn handshake should be near-instant. Anything past 30 s
        // means the CLI binary is wedged or the OS pipe is broken; past
        // 60 s the runner kills it.
        RunPhase.Spawning             => new PhaseBudget(SuspiciousSeconds: 30,  HungSeconds: 60),
        // Session init / `claude -r` resume is the slowest cold-start
        // phase and produces NO stdout for the whole window: the CLI
        // reads + replays a (possibly large) session JSONL, contacts the
        // Anthropic API, and only then emits its first frame. The 2026-06
        // mass-false-positive survey showed the old (60 s, 120 s) budget
        // auto-cancelling healthy runs at 122 s while they were still
        // legitimately resuming a big session - reading the session file
        // on disk the whole time. Resume of a large session can take
        // *minutes*, so the kill threshold must sit well past the longest
        // realistic init. The stdout-independent session-file heartbeat
        // (see ClaudeSessionHeartbeat wiring) is the primary liveness
        // signal here; this wide budget is the backstop that still kills
        // a genuinely wedged init that is also writing nothing to disk.
        RunPhase.SessionInitializing  => new PhaseBudget(SuspiciousSeconds: 120, HungSeconds: 600),
        // After SessionStarted but before TurnStarted, the CLI has the
        // prompt and is contacting the model. The original "init then
        // silence" hang sits here, but the same API backpressure that
        // stretches SessionInitializing stretches this window too, and
        // the 2026-06 survey saw kills clustered at 183 s under the old
        // (60 s, 180 s) budget. Widen the kill threshold to several
        // minutes so a slow-but-alive turn-start no longer reads as hung;
        // the early Suspicious warning still surfaces the stall loudly.
        //
        // 2026-06-09 Extra-High (xhigh) calibration: Codex at xhigh reasons
        // SILENTLY for many minutes BEFORE it emits its first turn frame, so
        // it stays in PromptConsumed the whole time and emits no OutputDelta.
        // The 420 s kill auto-cancelled healthy xhigh runs mid-think (observed
        // ASS-1670: killed at 423 s in PromptConsumed while still reasoning,
        // zero work produced). As of ASS-1671 the real liveness fix is wired:
        // Codex reasoning items now map to CliRunEvent.Heartbeat in
        // CodexEventAdapter, so each reasoning frame resets the silence clock
        // (Heartbeat is an IsActivitySignal) and a healthy xhigh think no
        // longer relies on this wide backstop alone. This wide budget stays as
        // the backstop that still kills a genuinely wedged pre-turn run that
        // emits no frames at all (not even reasoning); the Suspicious warning
        // still fires early (5 min) so a real stall stays loud, not silent.
        RunPhase.PromptConsumed       => new PhaseBudget(SuspiciousSeconds: 300, HungSeconds: 1200),
        // Inside a turn we expect output deltas every few seconds; under
        // xhigh, Codex can interleave long silent reasoning between deltas,
        // so the old (60 s, 180 s) budget killed alive runs mid-turn. The
        // early Suspicious warning still fires at 3 min; the kill backstop
        // sits at 10 min to cover an xhigh reasoning pause between deltas.
        RunPhase.TurnInProgress       => new PhaseBudget(SuspiciousSeconds: 180, HungSeconds: 600),
        RunPhase.OutputDelta          => new PhaseBudget(SuspiciousSeconds: 180, HungSeconds: 600),
        // Tool execution legitimately runs longer than ordinary turns
        // (Bash builds, grep over big repos, web fetches) and - for Codex -
        // a single long reasoning/tool turn can stay stdout-silent for
        // minutes at a stretch. The consolidated Codex-stability card
        // (2026-06) established that ~600 s of silence in ToolExecuting is
        // *realistic healthy work*, not a hang; the previous HungSeconds=600
        // therefore killed runs at the exact boundary of normal behaviour
        // (symptom A: watchdog kills with no genuine hang). Widen the kill
        // threshold well past that realistic ceiling while keeping an early,
        // visible Suspicious warning so the kill path stays "loud, not
        // silent". Operators can re-calibrate per CLI via
        // Watchdog:Phase:ToolExecuting:{Suspicious,Hung}Seconds.
        RunPhase.ToolExecuting        => new PhaseBudget(SuspiciousSeconds: 300, HungSeconds: 1200),
        // Turn-finished states. A CLI that emits its terminal turn frame
        // (claude-code `result:*`, codex `turn.completed`, a `turn.failed`)
        // is expected to exit its process within seconds; the sentinel-stop
        // and the OS exit handler take it from there. The ORIGINAL profile
        // gave these phases an effectively-infinite budget (9999/9999s) on
        // the theory that "the runner is about to finalize anyway" - but a
        // process that emits the frame and then NEVER exits (no further
        // output, no OS exit) is precisely the shape that pins the coding
        // seat forever: TurnCompleted is reached, the watchdog is disabled,
        // and the run sits `exec=running` indefinitely (observed ASS-757:
        // a reissue wedged >2.5h in TurnCompleted, log showed
        // `[phase=TurnCompleted silence=91s allowed=9999/9999s]`). So these
        // two phases now carry a bounded HARD-REAP budget: an early visible
        // Suspicious warning at 2 min, a kill backstop at 10 min. The kill
        // flows through the same `cli.Stop(RunStopReason.Watchdog)` path as
        // any other hang, so the seat is freed and the existing reissue/
        // recovery policy applies. Operators can re-tune per CLI via
        // Watchdog:Phase:{TurnCompleted,TurnFailed}:{Suspicious,Hung}Seconds.
        // ASS-776.
        RunPhase.TurnCompleted        => new PhaseBudget(SuspiciousSeconds: 120, HungSeconds: 600),
        RunPhase.TurnFailed           => new PhaseBudget(SuspiciousSeconds: 120, HungSeconds: 600),
        // Genuinely-waiting / already-dead states keep the watchdog's hand
        // stayed. NeedsInput legitimately blocks on a human (manual mode) or
        // the orchestrator's reply, so a wide silence there is expected, not a
        // hang. Exited/Killed mean the process is already gone, so the
        // watchdog tick (which only runs while exec=running) never reaches
        // them; the wide budget just documents the intent.
        RunPhase.NeedsInput           => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        RunPhase.Exited               => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        RunPhase.Killed               => new PhaseBudget(SuspiciousSeconds: 9999, HungSeconds: 9999),
        // Adapter could not classify. Use the most defensive budget so
        // a CLI we cannot read still gets killed eventually, but with
        // enough margin that an experimental CLI is not killed mid-turn.
        RunPhase.Unknown              => new PhaseBudget(SuspiciousSeconds: 60, HungSeconds: 240),
        _                              => new PhaseBudget(SuspiciousSeconds: 60, HungSeconds: 180)
    };
}

/// <summary>
/// Fully-resolved per-phase budget set used by <see cref="PhaseAwareWatchdog"/>.
/// Starts from the hardcoded profile in <see cref="PhaseBudget.For(RunPhase)"/>
/// and applies optional per-phase, per-field overrides read from
/// <c>Watchdog:Phase:&lt;phase&gt;:&lt;Suspicious|Hung&gt;Seconds</c>.
///
/// <para>
/// This is the config knob the <see cref="PhaseBudget"/> doc-comment has
/// always promised. Until now it was documented but never wired, so an
/// operator who needed to widen e.g. ToolExecuting for a model that
/// legitimately runs long tools had to recompile. Making the profile
/// data-driven lets the "real hang" vs "lots of work" separation be tuned
/// per CLI without a build - the core ask of the Codex-stability card.
/// </para>
/// </summary>
public sealed class PhaseBudgetTable
{
    private readonly IReadOnlyDictionary<RunPhase, PhaseBudget> _budgets;

    private PhaseBudgetTable(IReadOnlyDictionary<RunPhase, PhaseBudget> budgets, PhaseBudget longOp)
    {
        _budgets = budgets;
        LongOp = longOp;
    }

    /// <summary>
    /// Resolve the budget for a phase, falling back to the hardcoded
    /// default for any phase not present in the table.
    /// </summary>
    public PhaseBudget For(RunPhase phase) =>
        _budgets.TryGetValue(phase, out var b) ? b : PhaseBudget.For(phase);

    /// <summary>
    /// Silence budget applied (via <see cref="PhaseAwareWatchdog.EffectiveBudget"/>)
    /// while the in-flight tool is a known long-running operation
    /// (<see cref="LongRunningOperationDetector"/>) - a dev-server start,
    /// build, test run, or poll loop. It only ever <em>widens</em> the phase
    /// budget (the watchdog takes the max of the two), so a legitimate wait
    /// on a server/compile is not killed as a hang while a genuinely wedged
    /// run still hits the long-op ceiling. Override via
    /// <c>Watchdog:LongOp:&lt;Suspicious|Hung&gt;Seconds</c>.
    /// </summary>
    public PhaseBudget LongOp { get; }

    /// <summary>
    /// Default long-op silence budget: an early visible Suspicious warning at
    /// 5 min, kill backstop at 30 min. Wide enough to cover an Angular cold
    /// compile plus a dev-server-up poll loop, bounded so a truly wedged
    /// long-op is still terminated.
    /// </summary>
    public static PhaseBudget DefaultLongOp { get; } = new(SuspiciousSeconds: 300, HungSeconds: 1800);

    /// <summary>The hardcoded profile with no configuration overrides applied.</summary>
    public static PhaseBudgetTable Default { get; } = new(BuildDefaults(), DefaultLongOp);

    private static Dictionary<RunPhase, PhaseBudget> BuildDefaults() =>
        Enum.GetValues<RunPhase>().ToDictionary(p => p, PhaseBudget.For);

    /// <summary>
    /// Build a table from configuration. Every phase starts at its
    /// hardcoded default; a <c>Watchdog:Phase:&lt;phase&gt;</c> sub-section
    /// may override either or both of <c>SuspiciousSeconds</c> /
    /// <c>HungSeconds</c>. Unknown phase keys are ignored so config written
    /// for a phase this build does not know is forward-compatible rather
    /// than fatal.
    /// </summary>
    public static PhaseBudgetTable FromConfig(IConfiguration cfg)
    {
        var map = BuildDefaults();
        var section = cfg.GetSection("Watchdog:Phase");
        if (section.Exists())
        {
            foreach (var child in section.GetChildren())
            {
                if (!Enum.TryParse<RunPhase>(child.Key, ignoreCase: true, out var phase))
                    continue;
                var baseline = map.TryGetValue(phase, out var b) ? b : PhaseBudget.For(phase);
                map[phase] = new PhaseBudget(
                    SuspiciousSeconds: child.GetValue("SuspiciousSeconds", baseline.SuspiciousSeconds),
                    HungSeconds:       child.GetValue("HungSeconds", baseline.HungSeconds));
            }
        }

        var longOp = DefaultLongOp;
        var longOpSection = cfg.GetSection("Watchdog:LongOp");
        if (longOpSection.Exists())
        {
            longOp = new PhaseBudget(
                SuspiciousSeconds: longOpSection.GetValue("SuspiciousSeconds", DefaultLongOp.SuspiciousSeconds),
                HungSeconds:       longOpSection.GetValue("HungSeconds", DefaultLongOp.HungSeconds));
        }

        return new PhaseBudgetTable(map, longOp);
    }
}

/// <summary>
/// Phase-aware extension of <see cref="Watchdog"/>. Same pure-function
/// shape; the difference is the budget comes from <see cref="PhaseBudget.For(RunPhase)"/>
/// rather than a single global threshold.
///
/// <para>
/// The original watchdog still works for CLIs we have not yet adapted
/// to <see cref="CliRunEvent"/>; this one takes over when the runner has
/// a known phase. The per-phase reasoning makes the chat meta message
/// dramatically more useful: instead of "agent silent 60 s" the user
/// sees "agent silent 60 s during ToolExecuting (allowed: 180 s) - the
/// tool may legitimately be running" or "agent silent 120 s during
/// PromptConsumed (allowed: 120 s) - we have not seen a turn start;
/// likely stuck on the API or the CLI's session DB".
/// </para>
/// </summary>
public static class PhaseAwareWatchdog
{
    /// <summary>
    /// Compute the watchdog state for a run that has been silent for
    /// <paramref name="silenceSeconds"/> and is currently in <paramref name="phase"/>.
    /// The warm-up grace is anchored on run start (same as the legacy
    /// watchdog) - we never escalate during warmup, regardless of phase.
    /// </summary>
    public static WatchdogState DecideState(
        double silenceSeconds,
        double runAgeSeconds,
        RunPhase phase,
        WatchdogConfig config)
        => DecideState(silenceSeconds, runAgeSeconds, phase, config, PhaseBudgetTable.Default);

    /// <inheritdoc cref="DecideState(double,double,RunPhase,WatchdogConfig)"/>
    /// <param name="budgets">
    /// Resolved per-phase budgets (defaults plus any
    /// <c>Watchdog:Phase:*</c> configuration overrides). Pass
    /// <see cref="PhaseBudgetTable.Default"/> for the hardcoded profile.
    /// </param>
    public static WatchdogState DecideState(
        double silenceSeconds,
        double runAgeSeconds,
        RunPhase phase,
        WatchdogConfig config,
        PhaseBudgetTable budgets)
        => DecideState(silenceSeconds, runAgeSeconds, phase, config, budgets, longOpActive: false);

    /// <inheritdoc cref="DecideState(double,double,RunPhase,WatchdogConfig,PhaseBudgetTable)"/>
    /// <param name="longOpActive">
    /// True when the in-flight tool is a known long-running operation
    /// (<see cref="LongRunningOperationDetector"/>). The phase budget is then
    /// widened to <see cref="EffectiveBudget"/> - the max of the phase budget
    /// and <see cref="PhaseBudgetTable.LongOp"/> - so a legitimate wait on a
    /// dev-server/compile is not killed as a hang. It can only widen, never
    /// tighten, the budget.
    /// </param>
    public static WatchdogState DecideState(
        double silenceSeconds,
        double runAgeSeconds,
        RunPhase phase,
        WatchdogConfig config,
        PhaseBudgetTable budgets,
        bool longOpActive)
    {
        if (!config.Enabled) return WatchdogState.Healthy;
        if (runAgeSeconds < config.WarmUpGraceSeconds) return WatchdogState.Healthy;

        var budget = EffectiveBudget(phase, budgets, longOpActive);
        if (silenceSeconds >= budget.HungSeconds)       return WatchdogState.Hung;
        if (silenceSeconds >= budget.SuspiciousSeconds) return WatchdogState.Suspicious;
        // Quiet level still uses the global QuietSeconds for a soft
        // first-warning - per-phase Quiet would over-fragment the UI
        // signal without adding diagnostic value.
        if (silenceSeconds >= config.QuietSeconds)      return WatchdogState.Quiet;
        return WatchdogState.Healthy;
    }

    /// <summary>
    /// The silence budget actually used for a phase. With no long-op in
    /// flight this is just <see cref="PhaseBudgetTable.For"/>. While a known
    /// long-op runs, each field is the max of the phase budget and the
    /// long-op budget - so the tolerance only ever widens, and a phase that
    /// already tolerates more than the long-op budget (e.g. SessionInitializing)
    /// keeps its wider value.
    /// </summary>
    public static PhaseBudget EffectiveBudget(RunPhase phase, PhaseBudgetTable budgets, bool longOpActive)
    {
        var budget = budgets.For(phase);
        if (!longOpActive) return budget;
        var lo = budgets.LongOp;
        return new PhaseBudget(
            SuspiciousSeconds: Math.Max(budget.SuspiciousSeconds, lo.SuspiciousSeconds),
            HungSeconds:       Math.Max(budget.HungSeconds, lo.HungSeconds));
    }

    /// <summary>
    /// One-line summary the runner inserts into the chat meta line so
    /// the user sees WHY a state change happened. Budgets are baked in
    /// so the message reads as evidence, not policy.
    /// </summary>
    public static string FormatBudgetReason(RunPhase phase, double silenceSeconds)
        => FormatBudgetReason(phase, silenceSeconds, PhaseBudgetTable.Default);

    /// <inheritdoc cref="FormatBudgetReason(RunPhase,double)"/>
    public static string FormatBudgetReason(RunPhase phase, double silenceSeconds, PhaseBudgetTable budgets)
        => FormatBudgetReason(phase, silenceSeconds, budgets, longOpActive: false);

    /// <inheritdoc cref="FormatBudgetReason(RunPhase,double)"/>
    /// <param name="longOpActive">When true the reported budget is the
    /// widened <see cref="EffectiveBudget"/> and the string carries a
    /// <c>long-op</c> tag so the chat note shows why the budget was wider.</param>
    public static string FormatBudgetReason(RunPhase phase, double silenceSeconds, PhaseBudgetTable budgets, bool longOpActive)
    {
        var budget = EffectiveBudget(phase, budgets, longOpActive);
        var longOpTag = longOpActive ? " long-op" : "";
        return $"phase={phase}{longOpTag} silence={silenceSeconds:F0}s allowed={budget.SuspiciousSeconds:F0}/{budget.HungSeconds:F0}s";
    }
}
