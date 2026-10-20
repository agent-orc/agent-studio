using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-phase watchdog budget matrix. Pure-function library,
/// runs in &lt;1 s on every default <c>dotnet test</c>. The phase-aware
/// budgets are the load-bearing reason ADR-0013 was worth doing - the
/// original silence-only model treats all silences alike, this one
/// reports phase + budget so the user (and the orchestrator) see why.
/// </summary>
public class PhaseAwareWatchdogTests
{
    private static readonly WatchdogConfig Cfg = new(
        Enabled: true,
        WarmUpGraceSeconds: 0,
        QuietSeconds: 30,
        SuspiciousSeconds: 60,
        HungSeconds: 120,
        TickIntervalSeconds: 5);

    [Fact]
    public void Disabled_AlwaysHealthy()
    {
        var disabled = Cfg with { Enabled = false };
        Assert.Equal(WatchdogState.Healthy,
            PhaseAwareWatchdog.DecideState(silenceSeconds: 9999, runAgeSeconds: 9999,
                phase: RunPhase.OutputDelta, config: disabled));
    }

    [Fact]
    public void WarmUp_KeepsHealthy_RegardlessOfPhase()
    {
        var warmup = Cfg with { WarmUpGraceSeconds = 30 };
        Assert.Equal(WatchdogState.Healthy,
            PhaseAwareWatchdog.DecideState(silenceSeconds: 60, runAgeSeconds: 5,
                phase: RunPhase.PromptConsumed, config: warmup));
    }

    [Fact]
    public void Spawning_TightBudget_HungAt60s()
    {
        // PhaseBudget.For(Spawning) = (30s, 60s)
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(35, 100, RunPhase.Spawning, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.Spawning, Cfg));
    }

    [Fact]
    public void PromptConsumed_StaysQuietUntil300s_ThenSuspiciousThenHungAt1200s()
    {
        // The "init then silence" hang sits here. The 2026-06-09 Extra-High
        // calibration widened this from (120s, 420s) to (300s, 1200s):
        // Codex at xhigh reasons silently for many minutes before its first
        // turn frame, so it stays in PromptConsumed and emits no stdout; the
        // old 420 s kill auto-cancelled healthy xhigh runs mid-think
        // (ASS-1670: killed at 423 s while still reasoning, zero work).
        // Locked so a regression cannot quietly re-tighten it.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(45, 100, RunPhase.PromptConsumed, Cfg));
        // 250 s is still only Quiet under the widened 300 s Suspicious band.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(250, 300, RunPhase.PromptConsumed, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(305, 400, RunPhase.PromptConsumed, Cfg));
        // 423 s - the ASS-1670 false-positive kill point - is now merely
        // Suspicious, never Hung.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(423, 500, RunPhase.PromptConsumed, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.PromptConsumed, Cfg));
    }

    [Fact]
    public void ToolExecuting_RealisticSilence_IsSuspiciousNotKilled()
    {
        // PhaseBudget.For(ToolExecuting) = (300s, 1200s) since the 2026-06
        // Codex-stability re-tune. The card established that ~600 s of
        // stdout silence during a single long tool/reasoning turn is
        // *realistic healthy work*, so the watchdog must NOT kill there
        // (symptom A). Lock that contract: 600 s is Suspicious (loud,
        // visible) but never Hung; a kill only fires past 1200 s.
        Assert.Equal(WatchdogState.Quiet, // global Quiet at 30s still applies
            PhaseAwareWatchdog.DecideState(100, 200, RunPhase.ToolExecuting, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(350, 400, RunPhase.ToolExecuting, Cfg));
        // The realistic-work boundary: visible, not killed.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(600, 700, RunPhase.ToolExecuting, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.ToolExecuting, Cfg));
    }

    private static IConfiguration ConfigFrom(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void PhaseBudgetTable_FromConfig_NoSection_UsesHardcodedDefaults()
    {
        var table = PhaseBudgetTable.FromConfig(ConfigFrom());
        var tool = table.For(RunPhase.ToolExecuting);
        Assert.Equal(300, tool.SuspiciousSeconds);
        Assert.Equal(1200, tool.HungSeconds);
    }

    [Fact]
    public void PhaseBudgetTable_FromConfig_OverridesPerPhaseAndPerField()
    {
        // Operator widens ToolExecuting Hung only; Suspicious must keep its
        // default. Phase keys are case-insensitive.
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:Phase:ToolExecuting:HungSeconds", "2000")));
        var tool = table.For(RunPhase.ToolExecuting);
        Assert.Equal(300, tool.SuspiciousSeconds);   // unchanged default
        Assert.Equal(2000, tool.HungSeconds);        // overridden

        // A phase with no override keeps its full default.
        var spawn = table.For(RunPhase.Spawning);
        Assert.Equal(30, spawn.SuspiciousSeconds);
        Assert.Equal(60, spawn.HungSeconds);
    }

    [Fact]
    public void PhaseBudgetTable_FromConfig_UnknownPhaseKey_Ignored()
    {
        // Forward-compatible: config for a phase this build doesn't know
        // must not throw, and known phases stay at defaults.
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:Phase:NotARealPhase:HungSeconds", "5")));
        Assert.Equal(1200, table.For(RunPhase.ToolExecuting).HungSeconds);
    }

    [Fact]
    public void DecideState_HonorsConfiguredBudgetOverride()
    {
        // With ToolExecuting widened to Hung=2000, the previously-fatal
        // 1250 s silence is now merely Suspicious; the kill moves to 2000 s.
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:Phase:ToolExecuting:HungSeconds", "2000")));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.ToolExecuting, Cfg, table));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(2050, 2200, RunPhase.ToolExecuting, Cfg, table));
    }

    [Fact]
    public void WaitingOrDeadPhases_NeverEscalate()
    {
        // NeedsInput legitimately blocks on a human / orchestrator reply;
        // Exited / Killed mean the process is already gone (the live
        // watchdog tick never reaches them). Even at huge silence these
        // stay non-escalating so the watchdog cannot double-dispose a run
        // that is correctly parked or already terminated.
        foreach (var phase in new[] {
            RunPhase.NeedsInput, RunPhase.Exited, RunPhase.Killed })
        {
            Assert.Equal(WatchdogState.Quiet, // global Quiet still applies
                PhaseAwareWatchdog.DecideState(35, 100, phase, Cfg));
            Assert.Equal(WatchdogState.Quiet,
                PhaseAwareWatchdog.DecideState(9000, 10000, phase, Cfg));
        }
    }

    [Fact]
    public void TurnFinishedPhases_HardReap_KillAt600s()
    {
        // ASS-757 / ASS-776: a process that emits its terminal turn
        // frame (TurnCompleted / TurnFailed) but never exits used to pin the
        // coding seat forever under the old 9999/9999s budget. These phases
        // now carry a bounded hard-reap budget (120s, 600s): an early
        // Suspicious warning at 2 min, a kill backstop at 10 min. Lock the
        // contract so a regression cannot quietly re-disable the reap.
        foreach (var phase in new[] { RunPhase.TurnCompleted, RunPhase.TurnFailed })
        {
            // Within the warning band: visible, not yet killed.
            Assert.Equal(WatchdogState.Quiet,
                PhaseAwareWatchdog.DecideState(45, 100, phase, Cfg));
            Assert.Equal(WatchdogState.Suspicious,
                PhaseAwareWatchdog.DecideState(125, 200, phase, Cfg));
            // 91s - the silence in the observed wedge log line - is still
            // only Quiet; the kill backstop is what eventually frees the seat.
            Assert.Equal(WatchdogState.Quiet,
                PhaseAwareWatchdog.DecideState(91, 200, phase, Cfg));
            // Past 600s the wedged run is reaped (the runner kills the
            // process tree and the seat is freed).
            Assert.Equal(WatchdogState.Hung,
                PhaseAwareWatchdog.DecideState(605, 9999, phase, Cfg));
        }
    }

    [Fact]
    public void TurnCompleted_HardReap_HonorsConfiguredOverride()
    {
        // The hard-reap backstop is tunable like every other phase budget,
        // so an operator can widen (or tighten) it per CLI without a build.
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:Phase:TurnCompleted:HungSeconds", "900")));
        Assert.Equal(900, table.For(RunPhase.TurnCompleted).HungSeconds);
        Assert.Equal(120, table.For(RunPhase.TurnCompleted).SuspiciousSeconds); // default kept
        // The previously-fatal 605s is now merely Suspicious; kill moves to 900s.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(605, 9999, RunPhase.TurnCompleted, Cfg, table));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(905, 9999, RunPhase.TurnCompleted, Cfg, table));
    }

    [Fact]
    public void Unknown_HasDefensiveButNotInfiniteBudget()
    {
        // PhaseBudget.For(Unknown) = (60s, 240s). Adapter could not
        // classify the CLI's output - we still want a kill eventually
        // so a buggy adapter does not pin the runner forever.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.Unknown, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(245, 300, RunPhase.Unknown, Cfg));
    }

    [Fact]
    public void FormatBudgetReason_ReadableAndIncludesPhasePlusBudget()
    {
        var msg = PhaseAwareWatchdog.FormatBudgetReason(RunPhase.PromptConsumed, 70);
        Assert.Contains("phase=PromptConsumed", msg);
        Assert.Contains("silence=70s", msg);
        Assert.Contains("allowed=300/1200s", msg);
    }

    [Fact]
    public void LongOp_WidensToolExecutingBudget_NotKilledAtPhaseCeiling()
    {
        // ASS-665: while a known long-op (ng serve / build / poll loop) is the
        // in-flight tool, silence that would normally hit the ToolExecuting
        // kill (1200s) must NOT kill - the long-op budget (default Hung=1800s)
        // applies via Math.max. Without the long-op flag the same silence is
        // Hung; with it, merely Suspicious.
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.ToolExecuting, Cfg,
                PhaseBudgetTable.Default, longOpActive: false));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.ToolExecuting, Cfg,
                PhaseBudgetTable.Default, longOpActive: true));
        // The long-op still has a ceiling: past 1800s it is killed.
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(1850, 2000, RunPhase.ToolExecuting, Cfg,
                PhaseBudgetTable.Default, longOpActive: true));
    }

    [Fact]
    public void LongOp_NeverReducesAWiderPhaseBudget()
    {
        // EffectiveBudget takes the max per field. SessionInitializing already
        // tolerates Hung=600s; the long-op Suspicious is 300s (wider than the
        // phase's 120s) but its Hung (1800s) is what matters here. A phase
        // whose own budget is wider than the long-op in some field keeps the
        // wider value - the long-op can only widen, never tighten.
        var eff = PhaseAwareWatchdog.EffectiveBudget(RunPhase.SessionInitializing, PhaseBudgetTable.Default, longOpActive: true);
        Assert.Equal(300, eff.SuspiciousSeconds);   // max(120, 300)
        Assert.Equal(1800, eff.HungSeconds);        // max(600, 1800)

        // With no long-op, EffectiveBudget is exactly the phase budget.
        var plain = PhaseAwareWatchdog.EffectiveBudget(RunPhase.SessionInitializing, PhaseBudgetTable.Default, longOpActive: false);
        Assert.Equal(120, plain.SuspiciousSeconds);
        Assert.Equal(600, plain.HungSeconds);
    }

    [Fact]
    public void PhaseBudgetTable_FromConfig_OverridesLongOpBudget()
    {
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:LongOp:HungSeconds", "3600")));
        Assert.Equal(300, table.LongOp.SuspiciousSeconds);  // default kept
        Assert.Equal(3600, table.LongOp.HungSeconds);       // overridden

        // No section -> hardcoded long-op default (300/1800).
        var def = PhaseBudgetTable.FromConfig(ConfigFrom());
        Assert.Equal(300, def.LongOp.SuspiciousSeconds);
        Assert.Equal(1800, def.LongOp.HungSeconds);
    }

    [Fact]
    public void FormatBudgetReason_LongOp_TagsAndReportsWidenedBudget()
    {
        var msg = PhaseAwareWatchdog.FormatBudgetReason(RunPhase.ToolExecuting, 700,
            PhaseBudgetTable.Default, longOpActive: true);
        Assert.Contains("phase=ToolExecuting", msg);
        Assert.Contains("long-op", msg);
        Assert.Contains("allowed=300/1800s", msg);
    }

    [Fact]
    public void SessionInitializing_BudgetIs120And600()
    {
        // Pattern analysis 2026-05-06 originally moved this phase to
        // (60s, 120s) after kills clustered at 31-33s. The 2026-06
        // mass-false-positive survey then showed that budget *still* too
        // tight: healthy `claude -r` resumes of large sessions emit NO
        // stdout while they read+replay the session JSONL and contact the
        // API, and the watchdog was auto-cancelling them at 122s. Resume
        // of a big session can take *minutes*, so the kill threshold now
        // sits at 600s, well past the longest realistic init; the
        // stdout-independent session-file heartbeat is the primary
        // liveness signal here (see ClaudeSessionHeartbeat wiring). Locked
        // so a regression cannot quietly re-tighten it.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(45, 100, RunPhase.SessionInitializing, Cfg));
        // 65s used to be Suspicious under the old 60s threshold; now Quiet.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.SessionInitializing, Cfg));
        // 122s - the reported false-positive kill point - is now merely
        // Suspicious, never Hung.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(122, 383, RunPhase.SessionInitializing, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(125, 200, RunPhase.SessionInitializing, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(605, 700, RunPhase.SessionInitializing, Cfg));
    }
}
