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

/// <summary>Expected subscription cost of one launch.</summary>
public enum QuotaExpectedCostClass
{
    Cheap,
    Standard,
    Expensive,
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
    QuotaExpectedCostClass ExpectedCost = QuotaExpectedCostClass.Cheap,
    string? ExecutionPath = null,
    string? RequestedCliType = null,
    string? RequestedModel = null,
    string? RequestedThinkingLevel = null)
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
        QuotaExpectedCostClass? expectedCost = null,
        string? executionPath = null)
    {
        var cli = string.IsNullOrWhiteSpace(requestedCli)
            ? CliTypes.Claude
            : requestedCli!.Trim().ToLowerInvariant();
        var cost = expectedCost ?? QuotaExpectedCostClassifier.For(
            taskType: null,
            taskMode: null,
            requestedModel,
            requestedThinking,
            executionPath);
        var primarySnapshot = snapshotFor(cli);
        var projectionWarning = QuotaWindowProjection.FindWarning(primarySnapshot, nowUtc);

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
        //
        // Situation awareness (AGT-2751, requirement 4): a nearby reset is
        // only worth a quiet wait when the card is cheap. An expensive card
        // (explicit high/xhigh/ultra/max reasoning) switches to an
        // equal-strength provider immediately instead of sitting in the queue
        // until the reset, even inside the configured wait threshold.
        var strictPrimaryEarly = Strict(cli);
        var nearbyReset = EarliestReset(primarySnapshot, nowUtc, caps, blockedOnly: true);
        if (waitPolicy?.Enabled == true
            && strictPrimaryEarly.Blocked
            && !strictPrimaryEarly.Suspicious
            && cost == QuotaExpectedCostClass.Cheap
            && nearbyReset?.ResetAt is { } resetAt)
        {
            var remaining = resetAt - nowUtc;
            if (remaining > TimeSpan.Zero && remaining < TimeSpan.FromMinutes(waitPolicy.ThresholdMinutes))
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
                return Complete(new QuotaAdmissionPlan(
                    QuotaAdmissionOutcome.Wait,
                    cli,
                    requestedModel,
                    requestedThinking,
                    IsFallback: false,
                    Reason: $"waiting for quota reset {resetAt:HH:mm} UTC, {minutes} min remaining (threshold {waitPolicy.ThresholdMinutes} min)",
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
            return Complete(new QuotaAdmissionPlan(
                QuotaAdmissionOutcome.LaunchFallback,
                route.CliType,
                route.Model,
                route.ThinkingLevel,
                IsFallback: true,
                Reason: BuildSwitchReason(cli, route),
                NextResetAt: EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false)?.ResetAt,
                Projection: QuotaWindowProjection.WorstProjection(snapshotFor(cli), caps, nowUtc),
                ProjectionWarning: projectionWarning));
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
                EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: true)
                ?? EarliestReset(snapshotFor(route?.CliType ?? cli), nowUtc, caps, blockedOnly: true)
                ?? EarliestReset(snapshotFor(cli), nowUtc, caps, blockedOnly: false);
            var detail = !string.IsNullOrWhiteSpace(route?.Reason) ? route!.Reason : strictPrimary.DescribeReason();
            var reason = AppendReset($"waiting: all quotas exhausted ({detail})", blockedReset);
            return Complete(new QuotaAdmissionPlan(
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
            return Complete(new QuotaAdmissionPlan(
                throttle ? QuotaAdmissionOutcome.Throttle : QuotaAdmissionOutcome.LaunchPrimary,
                cli, model, thinking, IsFallback: false,
                Reason: AppendReset($"{verb}: {admissionPrimary.DescribeReason()}", reset),
                NextResetAt: reset?.ResetAt, Projection: proj, ProjectionWarning: projectionWarning));
        }

        // 5) Healthy: launch on primary.
        return Complete(new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.LaunchPrimary, cli, model, thinking,
            IsFallback: false,
            Reason: projectionWarning is null ? "launch: quota ok" : $"launch: quota projection ignored ({projectionWarning.Reason})",
            NextResetAt: null,
            Projection: QuotaWindowProjection.WorstProjection(snapshotFor(cli), caps, nowUtc),
            ProjectionWarning: projectionWarning));

        QuotaAdmissionPlan Complete(QuotaAdmissionPlan plan) => plan with
        {
            ExpectedCost = cost,
            ExecutionPath = executionPath,
            RequestedCliType = cli,
            RequestedModel = requestedModel,
            RequestedThinkingLevel = requestedThinking,
        };
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
        return
            $"burn {p.BurnRatePctPerHour:0.#}%/h, used {p.CurrentUsedPct:0.#}% -> projected {p.ProjectedUsedPct:0.#}% " +
            $"(cap {p.CapPct}%), {budgetLeft:0.#}% budget left, {p.HoursRemaining:0.#}h to reset";
    }

    private static string BuildSwitchReason(string primaryCli, CliRouteDecision route)
    {
        var why = !string.IsNullOrWhiteSpace(route.Reason) ? route.Reason : route.PrimaryCap.DescribeReason();
        return $"model switched pre-launch: {primaryCli} -> {route.CliType}/{route.Model ?? "<default>"}, reason: {why}";
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

    private static string AppendReset(string reason, QuotaWindow? resetWindow)
    {
        if (resetWindow?.ResetAt is null) return reason;
        var human = !string.IsNullOrWhiteSpace(resetWindow.ResetLabel)
            ? resetWindow.ResetLabel!
            : resetWindow.ResetAt.Value.ToString("HH:mm 'UTC'");
        return $"{reason}, next reset {human}";
    }
}

/// <summary>Pure expected-cost classification shared by every launch path.</summary>
public static class QuotaExpectedCostClassifier
{
    public static QuotaExpectedCostClass ForTask(TaskInfo task)
        => For(task.TaskType, task.Mode, task.Model, task.ThinkingLevel, "coding-card");

    public static QuotaExpectedCostClass For(
        string? taskType,
        string? taskMode,
        string? model,
        string? thinkingLevel,
        string? executionPath)
    {
        var thinking = thinkingLevel?.Trim().ToLowerInvariant();
        if (thinking is CliThinkingLevels.High or CliThinkingLevels.XHigh
            or CliThinkingLevels.Ultra or CliThinkingLevels.Max)
            return QuotaExpectedCostClass.Expensive;

        if (model?.Contains("mini", StringComparison.OrdinalIgnoreCase) == true
            || model?.Contains("luna", StringComparison.OrdinalIgnoreCase) == true
            || model?.Contains("haiku", StringComparison.OrdinalIgnoreCase) == true
            || thinking == "low")
            return QuotaExpectedCostClass.Cheap;

        if (executionPath?.Contains("chat", StringComparison.OrdinalIgnoreCase) == true
            || executionPath?.Contains("review", StringComparison.OrdinalIgnoreCase) == true
            || executionPath?.Contains("pipeline", StringComparison.OrdinalIgnoreCase) == true)
            return QuotaExpectedCostClass.Cheap;

        if (string.Equals(taskType, TaskTypes.Bug, StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskMode, TaskModes.Coding, StringComparison.OrdinalIgnoreCase))
            return QuotaExpectedCostClass.Standard;

        return QuotaExpectedCostClass.Cheap;
    }
}
