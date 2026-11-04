using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// Forward-looking burn-rate projection over a CLI's quota windows (AGT-2055,
/// requirement 6). The projection math lives in the shared
/// <see cref="SelectionQuotaMath"/> of the model-selection planner (MS-5);
/// this class keeps the monolith's window-anchoring step and its call shape. A snapshot only tells us the <i>current</i> usage; the
/// operator wants the scheduler to see the wall coming - "aus Burn-Rate +
/// Restfenster PROJIZIEREN, ob das Budget vor dem Reset reisst" - so it can
/// switch models or throttle <b>before</b> a window is actually exhausted.
///
/// <para>
/// The math is a deliberately simple linear extrapolation from the window's
/// start: given the window length (inferred from its label), the moment it
/// resets (<see cref="QuotaWindow.ResetAt"/>) and the fraction already elapsed,
/// we extrapolate the current used-percent to the end of the window. There is
/// no per-CLI usage history to build a smarter rate from, and a linear "if this
/// pace holds" is exactly the intuition an operator reasons with. It is
/// intentionally conservative: it never fires in the first few percent of a
/// window (too little signal), and windows whose length cannot be inferred are
/// simply skipped rather than guessed.
/// </para>
/// </summary>
public static class QuotaWindowProjection
{
    private static readonly TimeSpan StartAnchorTolerance = SelectionQuotaMath.StartAnchorTolerance;
    private static readonly TimeSpan WeeklyLength = TimeSpan.FromDays(7);

    /// <summary>
    /// Project one window to the end of its current period. Returns null when
    /// the window cannot be projected (no reset time, no inferable length, no
    /// usage, or too early in the window to be meaningful).
    /// </summary>
    public static QuotaProjection? Project(QuotaWindow window, DateTime nowUtc, int capPct)
        => SelectionQuotaMath.Project(window, nowUtc, capPct);

    /// <summary>
    /// Carry the first observed start of an active reset cycle forward. A reset
    /// timestamp that moves while the anchored cycle is still active is retained
    /// for display but marked unusable for projection until a reset is observed.
    /// <para>
    /// A weekly window can reset early. A usage drop of more than
    /// <see cref="QuotaForecastPolicy.ResetDropPct"/> together with a later reset
    /// time counts as that observed reset and re-anchors the window. This relies on
    /// <see cref="QuotaService"/> running <see cref="QuotaPlausibilityGate"/> first,
    /// so a weekly decrease only gets here after a second probe confirmed it. A reset
    /// time that moves without such a drop (the AGT-2107 poisoned reset) stays suspicious.
    /// </para>
    /// </summary>
    public static QuotaSnapshot AnchorWindowStarts(
        QuotaSnapshot? previous, QuotaSnapshot candidate, DateTime nowUtc)
    {
        if (candidate.Windows.Count == 0) return candidate;
        var windows = candidate.Windows.Select(current =>
        {
            var length = InferWindowLength(current.Label);
            if (length is null || current.ResetAt is null) return current;
            var prior = previous?.Windows.FirstOrDefault(w =>
                string.Equals(w.Label?.Trim(), current.Label?.Trim(), StringComparison.OrdinalIgnoreCase));
            var priorStart = prior?.ObservedStartAt
                ?? (prior?.ResetAt is { } priorReset ? priorReset - length.Value : null);
            if (priorStart is null || priorStart.Value + length.Value <= nowUtc)
            {
                return current with
                {
                    ObservedStartAt = current.ResetAt.Value - length.Value,
                    ProjectionSuspiciousReason = null,
                };
            }

            var impliedStart = current.ResetAt.Value - length.Value;
            if (IsObservedEarlyWeeklyReset(length.Value, prior, current, impliedStart, priorStart.Value))
            {
                return current with
                {
                    ObservedStartAt = impliedStart,
                    ProjectionSuspiciousReason = null,
                };
            }

            var shifted = (impliedStart - priorStart.Value).Duration() > StartAnchorTolerance;
            return current with
            {
                ObservedStartAt = priorStart,
                ProjectionSuspiciousReason = shifted
                    ? $"{current.Label} resetAt moved before the anchored window reset was observed"
                    : null,
            };
        }).ToList();
        return candidate with { Windows = windows };
    }

    private static bool IsObservedEarlyWeeklyReset(
        TimeSpan length, QuotaWindow? prior, QuotaWindow current, DateTime impliedStart, DateTime priorStart)
        => length == WeeklyLength
            && prior?.UsedPct is double priorUsed
            && current.UsedPct is double currentUsed
            && priorUsed - currentUsed > QuotaForecastPolicy.ResetDropPct
            && impliedStart > priorStart;

    /// <summary>Explain why a snapshot was deliberately excluded from projection.</summary>
    public static QuotaProjectionWarning? FindWarning(QuotaSnapshot? snapshot, DateTime nowUtc)
        => SelectionQuotaMath.FindWarning(snapshot, nowUtc);

    /// <summary>
    /// Evaluate a whole snapshot for a projected (not-yet-breached) cap cross.
    /// Returns the most-overshooting projected window as a
    /// <see cref="CapEvaluation"/> with <see cref="CapEvaluation.Projected"/>
    /// set, or null when nothing is projected to breach.
    /// </summary>
    public static CapEvaluation? EvaluateProjectedBreach(
        QuotaSnapshot? snapshot, CliQuotaCapsService caps, DateTime nowUtc)
        => SelectionQuotaMath.EvaluateProjectedBreach(snapshot, caps, nowUtc);

    /// <summary>
    /// Return the worst (highest projected) window projection for a snapshot,
    /// for logging/event payloads. Null when nothing is projectable.
    /// </summary>
    public static QuotaProjection? WorstProjection(
        QuotaSnapshot? snapshot, CliQuotaCapsService caps, DateTime nowUtc)
        => SelectionQuotaMath.WorstProjection(snapshot, caps, nowUtc);

    /// <summary>Infer the length of a quota window from its label ("5-hour", "Weekly"); null when unknown.</summary>
    public static TimeSpan? InferWindowLength(string? label) => SelectionQuotaMath.InferWindowLength(label);
}

// QuotaProjection and QuotaProjectionWarning live in the contracts package
// (ModelSelection/QuotaDecisionContracts.cs).
