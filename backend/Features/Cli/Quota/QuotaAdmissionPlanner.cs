using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// What the pre-launch quota check decided for one candidate card.
/// </summary>
public enum QuotaAdmissionOutcome
{
    /// <summary>Launch on the requested/primary model - quota is healthy.</summary>
    LaunchPrimary,
    /// <summary>Launch on the AGT-2040 fallback because the primary is (or is about to be) capped.</summary>
    LaunchFallback,
    /// <summary>Do not launch: every viable model is exhausted. Wait quietly for the next reset.</summary>
    Wait,
    /// <summary>Only reduce parallelism: a projected breach with no fallback while a slot is already busy.</summary>
    Throttle,
}

/// <summary>Where an admission decision will be consumed.</summary>
public enum QuotaExecutionPath
{
    CodingRun,
    ReviewAspect,
    PipelineStep,
    OrchestratorChat,
}

/// <summary>Coarse expected subscription cost used only for wait-versus-switch.</summary>
public enum QuotaExpectedCostClass
{
    Cheap,
    Standard,
    Expensive,
}

public sealed record QuotaAdmissionContext(
    QuotaExecutionPath ExecutionPath,
    QuotaExpectedCostClass ExpectedCostClass,
    string? TaskType = null)
{
    public static QuotaAdmissionContext ForTask(
        QuotaExecutionPath path,
        string? taskType,
        string? thinkingLevel)
    {
        var thinking = thinkingLevel?.Trim().ToLowerInvariant();
        var cost = thinking is "xhigh" or "ultra" or "max"
            ? QuotaExpectedCostClass.Expensive
            : path is QuotaExecutionPath.ReviewAspect or QuotaExecutionPath.PipelineStep
              || (string.Equals(TaskTypes.Normalize(taskType), TaskTypes.Chore, StringComparison.OrdinalIgnoreCase)
                  && thinking is null or "minimal" or "low" or "medium")
                ? QuotaExpectedCostClass.Cheap
                : QuotaExpectedCostClass.Standard;
        return new QuotaAdmissionContext(path, cost, TaskTypes.Normalize(taskType));
    }
}

/// <summary>
/// The pre-launch quota decision for a card, plus the numbers behind it.
/// </summary>
public sealed record QuotaAdmissionPlan(
    QuotaAdmissionOutcome Outcome,
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool IsFallback,
    string Reason,
    DateTime? NextResetAt,
    QuotaProjection? Projection,
    QuotaProjectionWarning? ProjectionWarning = null,
    bool NearbyResetWait = false,
    QuotaProjection? FallbackProjection = null,
    string? RouteSource = null,
    string? EquivalentTier = null,
    QuotaAdmissionContext? Context = null)
{
    /// <summary>True when the runner should proceed to a launch (primary or fallback).</summary>
    public bool ShouldLaunch => Outcome is QuotaAdmissionOutcome.LaunchPrimary or QuotaAdmissionOutcome.LaunchFallback;

    /// <summary>True when the card should be held back this tick (wait or throttle).</summary>
    public bool IsDeferred => Outcome is QuotaAdmissionOutcome.Wait or QuotaAdmissionOutcome.Throttle;
}

/// <summary>
/// The algorithmic pre-launch admission check the operator asked for (AGT-2055):
/// "Die Last-Steuerung soll ein ALGORITHMUS sein, nicht ein CLI-Call." Before a
/// card is admitted, the scheduler evaluates the cached quota snapshots for its
/// target CLI and decides - purely from data, without spawning anything -
/// whether to launch on primary, switch to the AGT-2040 fallback, throttle, or
/// wait for the next reset.
///
/// <para>
/// This planner deliberately <b>reuses</b> the merged AGT-2040 routing map
/// (<see cref="CliQuotaFallbackService"/>) rather than duplicating "which model
/// replaces which": that map IS the configuration. The planner's own job is
/// (a) to feed the router a <i>projection-aware</i> quota view so a primary
/// that is about to breach switches early, and (b) to turn a "no viable model"
/// situation into a quiet, reasoned wait instead of a burned launch.
/// </para>
/// </summary>
public static class QuotaAdmissionPlanner
{
    public static QuotaAdmissionPlan Plan(
        string? requestedCli,
        string? requestedModel,
        string? requestedThinking,
        CliQuotaFallbackService? fallback,
        CliQuotaCapsService caps,
        Func<string?, QuotaSnapshot?> snapshotFor,
        DateTime nowUtc,
        int occupiedSlots,
        ResolvedCliQuotaWaitPolicy? waitPolicy = null,
        QuotaAdmissionContext? context = null)
    {
        var cli = string.IsNullOrWhiteSpace(requestedCli)
            ? CliTypes.Claude
            : requestedCli!.Trim().ToLowerInvariant();
        var primarySnapshot = snapshotFor(cli);
        var projectionWarning = QuotaWindowProjection.FindWarning(primarySnapshot, nowUtc);
        // Compatibility callers predating situation-aware admission retain the
        // old nearby-reset behavior. Every execution path now passes an
        // explicit context and therefore makes the cost-class choice visible.
        var effectiveContext = context ?? new QuotaAdmissionContext(
            QuotaExecutionPath.CodingRun,
            QuotaExpectedCostClass.Cheap);

        QuotaAdmissionPlan Finish(QuotaAdmissionPlan plan)
        {
            return plan with { Context = effectiveContext };
        }

        // Strict = already over the configured cap. Admission = strict OR
        // projected-to-breach-before-reset. The router is fed the admission
        // view so a primary about to hit the wall routes to the fallback now.
        CapEvaluation Strict(string? c) => caps.Evaluate(snapshotFor(c));
        CapEvaluation Admission(string? c)
        {
            var strict = Strict(c);
            if (strict.Blocked) return strict;
            return QuotaWindowProjection.EvaluateProjectedBreach(snapshotFor(c), caps, nowUtc)
                   ?? CapEvaluation.NotBlocked;
        }

        // 1) The opt-in CAR 0.6 wait policy owns the first decision when the
        // primary is already capped and its confirmed reset is nearby. This is
        // intentionally before route resolution: nearby wait -> model switch ->
        // throttle. Unknown, elapsed, or distant resets fail open to the
        // existing fallback/admission route.
        var strictPrimaryEarly = Strict(cli);
        var nearbyReset = BlockingReset(primarySnapshot, nowUtc, caps);
        if (waitPolicy?.Enabled == true
            && strictPrimaryEarly.Blocked
            && !strictPrimaryEarly.Suspicious
            && effectiveContext.ExpectedCostClass == QuotaExpectedCostClass.Cheap
            && nearbyReset?.ResetAt is { } resetAt)
        {
            var remaining = resetAt - nowUtc;
            if (remaining > TimeSpan.Zero && remaining < TimeSpan.FromMinutes(waitPolicy.ThresholdMinutes))
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                return Finish(new QuotaAdmissionPlan(
                    QuotaAdmissionOutcome.Wait,
                    cli,
                    requestedModel,
                    requestedThinking,
                    IsFallback: false,
                    Reason: $"waiting for quota reset {resetAt:HH:mm} UTC, {minutes} min remaining; "
                            + $"{effectiveContext.ExpectedCostClass.ToString().ToLowerInvariant()}-cost work is within the {waitPolicy.ThresholdMinutes} min wait threshold",
                    NextResetAt: resetAt,
                    Projection: QuotaWindowProjection.WorstProjection(primarySnapshot, caps, nowUtc),
                    ProjectionWarning: projectionWarning,
                    NearbyResetWait: true));
            }
        }

        var route = fallback?.Resolve(cli, requestedModel, requestedThinking, Admission);

        // 2) The router switched us to the fallback (primary capped or projected;
        //    a usable fallback exists). Documented model switch before start.
        if (route?.IsFallback == true)
        {
            var primaryReset = BlockingReset(snapshotFor(cli), nowUtc, caps)
                               ?? ResetForEvaluation(snapshotFor(cli), Admission(cli), nowUtc)
                               ?? EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false);
            var fallbackProjection = QuotaWindowProjection.WorstProjection(
                snapshotFor(route.CliType), caps, nowUtc);
            return Finish(new QuotaAdmissionPlan(
                QuotaAdmissionOutcome.LaunchFallback,
                route.CliType,
                route.Model,
                route.ThinkingLevel,
                IsFallback: true,
                Reason: BuildSwitchReason(cli, route, primaryReset, fallbackProjection, effectiveContext),
                NextResetAt: primaryReset?.ResetAt,
                Projection: QuotaWindowProjection.WorstProjection(snapshotFor(cli), caps, nowUtc),
                ProjectionWarning: projectionWarning,
                FallbackProjection: fallbackProjection,
                RouteSource: route.RouteSource,
                EquivalentTier: route.EquivalentTier));
        }

        // Primary path (no fallback taken): resolve the concrete primary model
        // from the route (which may carry the workspace-configured primary).
        var model = route?.Model ?? requestedModel;
        var thinking = route?.ThinkingLevel ?? requestedThinking;
        var strictPrimary = Strict(cli);
        var admissionPrimary = route?.PrimaryCap ?? Admission(cli);

        // 3) Primary is ACTUALLY over cap and no fallback saved us: everything is
        //    exhausted. Wait quietly with a reason and the next reset time.
        if (strictPrimary.Blocked)
        {
            var blockedReset =
                BlockingReset(snapshotFor(cli), nowUtc, caps)
                ?? BlockingReset(snapshotFor(route?.CliType ?? cli), nowUtc, caps)
                ?? EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false);
            var detail = !string.IsNullOrWhiteSpace(route?.Reason) ? route!.Reason : strictPrimary.DescribeReason();
            var reason = AppendReset($"waiting: all quotas exhausted ({detail})", blockedReset);
            return Finish(new QuotaAdmissionPlan(
                QuotaAdmissionOutcome.Wait, cli, model, thinking,
                IsFallback: false, Reason: reason, NextResetAt: blockedReset?.ResetAt,
                Projection: QuotaWindowProjection.WorstProjection(snapshotFor(cli), caps, nowUtc),
                ProjectionWarning: projectionWarning));
        }

        // 4) Primary is only PROJECTED to breach and there is no usable fallback.
        //    Throttle (reduce parallelism) rather than block - but never throttle
        //    to zero: the first/only run always proceeds, else nothing ever runs.
        if (admissionPrimary.Blocked)
        {
            var proj = QuotaWindowProjection.WorstProjection(snapshotFor(cli), caps, nowUtc);
            var reset = EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false);
            var throttle = occupiedSlots > 0;
            var verb = throttle ? "throttling" : "launching (projection-flagged)";
            return Finish(new QuotaAdmissionPlan(
                throttle ? QuotaAdmissionOutcome.Throttle : QuotaAdmissionOutcome.LaunchPrimary,
                cli, model, thinking, IsFallback: false,
                Reason: AppendReset($"{verb}: {admissionPrimary.DescribeReason()}", reset),
                NextResetAt: reset?.ResetAt, Projection: proj, ProjectionWarning: projectionWarning));
        }

        // 5) Healthy: launch on primary.
        return Finish(new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.LaunchPrimary, cli, model, thinking,
            IsFallback: false,
            Reason: projectionWarning is null ? "launch: quota ok" : $"launch: quota projection ignored ({projectionWarning.Reason})",
            NextResetAt: null,
            Projection: QuotaWindowProjection.WorstProjection(snapshotFor(cli), caps, nowUtc),
            ProjectionWarning: projectionWarning));
    }

    /// <summary>
    /// One-line "with the numbers" description of an admission decision for the
    /// load-distribution feed (AGT-2055, requirement 7): burn rate, remaining
    /// budget and remaining time behind the umschichten / drosseln / normal
    /// call. Falls back to the outcome + next reset when no window could be
    /// projected (e.g. a snapshot with no reset time to extrapolate from).
    /// </summary>
    public static string DescribeLoadNumbers(QuotaAdmissionPlan plan)
    {
        var p = plan?.Projection;
        var warning = plan?.ProjectionWarning;
        if (warning is not null)
            return $"projection ignored: {warning.Reason}; used {warning.CurrentUsedPct:0.#}% -> suspect {warning.ProjectedUsedPct:0.#}%; " +
                   $"resetAt {warning.ResetAt:o}, assumed start {warning.AssumedStartAt:o}, elapsed fraction {warning.ElapsedFraction:0.###}";
        if (p is null)
        {
            var outcome = plan?.Outcome.ToString() ?? "unknown";
            return plan?.NextResetAt is { } reset
                ? $"{outcome}; next reset {reset:HH:mm} UTC"
                : outcome;
        }

        var budgetLeft = Math.Max(0d, p.CapPct - p.CurrentUsedPct);
        var primary =
            $"burn {p.BurnRatePctPerHour:0.#}%/h, used {p.CurrentUsedPct:0.#}% -> projected {p.ProjectedUsedPct:0.#}% " +
            $"(cap {p.CapPct}%), {budgetLeft:0.#}% budget left, {p.HoursRemaining:0.#}h to reset";
        if (plan?.FallbackProjection is not { } fallback) return primary;
        var fallbackBudgetLeft = Math.Max(0d, fallback.CapPct - fallback.CurrentUsedPct);
        return $"{primary}; fallback burn {fallback.BurnRatePctPerHour:0.#}%/h, " +
               $"{fallbackBudgetLeft:0.#}% budget left, {fallback.HoursRemaining:0.#}h to reset";
    }

    private static string BuildSwitchReason(
        string primaryCli,
        CliRouteDecision route,
        QuotaWindow? primaryReset,
        QuotaProjection? fallbackProjection,
        QuotaAdmissionContext context)
    {
        var why = !string.IsNullOrWhiteSpace(route.Reason) ? route.Reason : route.PrimaryCap.DescribeReason();
        var reset = primaryReset?.ResetAt is { } resetAt
            ? $", primary reset {resetAt:HH:mm} UTC"
            : string.Empty;
        var fallbackHeadroom = fallbackProjection is null
            ? string.Empty
            : $", fallback has {Math.Max(0d, fallbackProjection.CapPct - fallbackProjection.CurrentUsedPct):0.#}% "
              + $"budget left at {fallbackProjection.BurnRatePctPerHour:0.#}%/h";
        var cost = context.ExpectedCostClass.ToString().ToLowerInvariant();
        return $"model switched pre-launch: {primaryCli} -> {route.CliType}/{route.Model ?? "<default>"}; "
               + $"{cost}-cost work, reason: {why}{reset}{fallbackHeadroom}";
    }

    /// <summary>Earliest future-resetting window in a snapshot (optionally only over-cap windows).</summary>
    private static QuotaWindow? EarliestReset(
        QuotaSnapshot? snapshot, DateTime nowUtc, CliQuotaCapsService caps, bool blockedOnly)
    {
        if (snapshot?.Windows == null) return null;
        QuotaWindow? best = null;
        foreach (var w in snapshot.Windows)
        {
            if (w.ResetAt is null) continue;
            if (w.ResetAt.Value <= nowUtc) continue;
            if (blockedOnly)
            {
                if (w.UsedPct is null) continue;
                if (w.UsedPct.Value < caps.GetCap(snapshot.CliType, w.Label)) continue;
            }
            if (best is null || w.ResetAt < best.ResetAt) best = w;
        }
        return best;
    }

    /// <summary>
    /// The provider becomes usable only after every currently capped window
    /// has reset. With overlapping session and weekly limits this is the last,
    /// not the first, reset among the blocking windows.
    /// </summary>
    private static QuotaWindow? BlockingReset(
        QuotaSnapshot? snapshot, DateTime nowUtc, CliQuotaCapsService caps)
    {
        if (snapshot?.Windows == null) return null;
        QuotaWindow? best = null;
        foreach (var window in snapshot.Windows)
        {
            if (window.ResetAt is not { } resetAt || resetAt <= nowUtc) continue;
            if (window.UsedPct is not { } usedPct) continue;
            if (usedPct < caps.GetCap(snapshot.CliType, window.Label)) continue;
            if (best is null || resetAt > best.ResetAt) best = window;
        }
        return best;
    }

    private static QuotaWindow? ResetForEvaluation(
        QuotaSnapshot? snapshot,
        CapEvaluation evaluation,
        DateTime nowUtc)
    {
        if (!evaluation.Blocked || evaluation.ResetAt is not { } resetAt || resetAt <= nowUtc)
            return null;
        return snapshot?.Windows.FirstOrDefault(window =>
                   string.Equals(window.Label, evaluation.WindowLabel, StringComparison.OrdinalIgnoreCase)
                   && window.ResetAt == resetAt)
               ?? new QuotaWindow
               {
                   Label = evaluation.WindowLabel ?? string.Empty,
                   ResetAt = resetAt,
               };
    }

    private static string AppendReset(string reason, QuotaWindow? resetWindow)
    {
        if (resetWindow?.ResetAt is null) return reason;
        var human = !string.IsNullOrWhiteSpace(resetWindow.ResetLabel)
            ? resetWindow.ResetLabel!
            : resetWindow.ResetAt.Value.ToString("HH:mm 'UTC'");
        return $"{reason}, next reset {human}";
    }
}
