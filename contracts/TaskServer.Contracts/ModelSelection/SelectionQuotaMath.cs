using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// Pure quota arithmetic of the model-selection planner (MS-5): cap
/// evaluation and the forward-looking burn-rate projection (AGT-2055,
/// requirement 6). <c>CliQuotaCapsService.Evaluate</c> and
/// <c>QuotaWindowProjection</c> in the monolith delegate here, so the
/// monolith, Task Server and runner judge a quota window the same way.
///
/// <para>
/// The projection is a deliberately simple linear extrapolation from the
/// window's start: given the window length (inferred from its label), the
/// moment it resets and the fraction already elapsed, the current used
/// percentage is extrapolated to the end of the window. It never fires in the
/// first few percent of a window, and windows whose length cannot be inferred
/// are skipped rather than guessed.
/// </para>
/// </summary>
public static class SelectionQuotaMath
{
    /// <summary>
    /// Below this elapsed fraction we refuse to project: extrapolating from a
    /// sliver of a window produces wildly unstable numbers (5% of a 5-hour
    /// window is 15 minutes).
    /// </summary>
    private const double MinElapsedFraction = 0.05;
    private const double MaxProjectedToUsedRatio = 4d;
    private const double SuspiciousEarlyFraction = 0.25;
    public static readonly TimeSpan StartAnchorTolerance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Evaluate whether <paramref name="snapshot"/> currently exceeds any
    /// configured cap for its CLI. Returns the most-overshooting window so the
    /// caller can produce a useful "blocked because..." message.
    /// </summary>
    public static CapEvaluation EvaluateCap(QuotaSnapshot? snapshot, IQuotaCapTable caps)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.CliType))
            return CapEvaluation.NotBlocked;

        // Conservative admission for unconfirmed snapshots (AGT-2064). A snapshot
        // flagged suspicious - a downward glitch a confirmation probe has not yet
        // agreed with, or one a live usage-limit error contradicted - is treated
        // as over-cap regardless of how green its numbers look: we would rather
        // hold a launch than fire onto a CLI that may really be at its limit. The
        // hold clears the moment a re-probe produces a trusted snapshot.
        if (snapshot.Suspicious)
        {
            var worstSuspect = snapshot.Windows?
                .Where(w => w.UsedPct is not null)
                .OrderByDescending(w => w.UsedPct)
                .FirstOrDefault();
            return new CapEvaluation(
                Blocked: true,
                CliType: snapshot.CliType,
                WindowLabel: worstSuspect?.Label ?? "quota",
                CapPct: worstSuspect?.Label is { } wl ? caps.GetCap(snapshot.CliType, wl) : QuotaCapDefaults.CapPct,
                UsedPct: worstSuspect?.UsedPct ?? 100d,
                Suspicious: true,
                SuspiciousReason: snapshot.SuspiciousReason);
        }

        if (snapshot.Windows == null || snapshot.Windows.Count == 0)
            return CapEvaluation.NotBlocked;

        CapEvaluation? worst = null;
        foreach (var w in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(w.Label) || w.UsedPct is null) continue;
            var cap = caps.GetCap(snapshot.CliType, w.Label);
            if (w.UsedPct.Value < cap) continue;
            var ev = new CapEvaluation(
                Blocked: true,
                CliType: snapshot.CliType,
                WindowLabel: w.Label,
                CapPct: cap,
                UsedPct: w.UsedPct.Value);
            if (worst is null || ev.UsedPct - ev.CapPct > worst.UsedPct - worst.CapPct)
                worst = ev;
        }
        return worst ?? CapEvaluation.NotBlocked;
    }

    /// <summary>
    /// Project one window to the end of its current period. Returns null when
    /// the window cannot be projected (no reset time, no inferable length, no
    /// usage, or too early in the window to be meaningful).
    /// </summary>
    public static QuotaProjection? Project(QuotaWindow window, DateTime nowUtc, int capPct)
    {
        if (window == null || window.UsedPct is null || window.ResetAt is null) return null;
        var length = InferWindowLength(window.Label);
        if (length is null) return null;

        var reset = window.ResetAt.Value;
        var impliedStart = reset - length.Value;
        var start = window.ObservedStartAt ?? impliedStart;
        if (!string.IsNullOrWhiteSpace(window.ProjectionSuspiciousReason)) return null;
        if (window.ObservedStartAt is not null
            && nowUtc < start + length.Value
            && (impliedStart - start).Duration() > StartAnchorTolerance)
            return null;
        var elapsed = nowUtc - start;
        if (elapsed <= TimeSpan.Zero) return null;                 // window not started yet (clock skew)
        if (elapsed > length.Value) elapsed = length.Value;         // stale snapshot past its reset

        var fraction = elapsed.TotalHours / length.Value.TotalHours;
        if (fraction < MinElapsedFraction) return null;             // too early to trust the slope

        var used = window.UsedPct.Value;
        var projected = used / fraction;                            // "if this pace holds to reset"
        if (used > 0d
            && projected / used > MaxProjectedToUsedRatio
            && fraction <= SuspiciousEarlyFraction)
            return null;
        var burnPerHour = used / elapsed.TotalHours;
        var hoursRemaining = Math.Max(0d, (reset - nowUtc).TotalHours);

        return new QuotaProjection(
            WindowLabel: window.Label,
            CurrentUsedPct: used,
            ProjectedUsedPct: projected,
            BurnRatePctPerHour: burnPerHour,
            HoursRemaining: hoursRemaining,
            CapPct: capPct,
            ResetAt: reset,
            // Only a window that is currently UNDER the cap but projected to
            // cross it is a "wall ahead". An already-over-cap window is handled
            // by the strict cap check, not the projection.
            BreachesBeforeReset: used < capPct && projected >= capPct,
            AssumedStartAt: start,
            ElapsedFraction: fraction);
    }

    /// <summary>Explain why a snapshot was deliberately excluded from projection.</summary>
    public static QuotaProjectionWarning? FindWarning(QuotaSnapshot? snapshot, DateTime nowUtc)
    {
        if (snapshot?.Windows == null) return null;
        foreach (var window in snapshot.Windows)
        {
            if (window.UsedPct is not double used || window.ResetAt is not DateTime reset) continue;
            var length = InferWindowLength(window.Label);
            if (length is null) continue;
            var impliedStart = reset - length.Value;
            var start = window.ObservedStartAt ?? impliedStart;
            var elapsed = nowUtc - start;
            if (elapsed <= TimeSpan.Zero) continue;
            var fraction = Math.Min(1d, elapsed.TotalHours / length.Value.TotalHours);
            var projected = used / fraction;
            var anchorConflict = window.ObservedStartAt is not null
                && nowUtc < start + length.Value
                && (impliedStart - start).Duration() > StartAnchorTolerance;
            var ratioConflict = used > 0d
                && projected / used > MaxProjectedToUsedRatio
                && fraction <= SuspiciousEarlyFraction;
            var reason = window.ProjectionSuspiciousReason
                ?? (anchorConflict ? $"{window.Label} resetAt conflicts with the observed window start" : null)
                ?? (ratioConflict ? $"{window.Label} projected/used ratio exceeds {MaxProjectedToUsedRatio:0.#} near window start" : null);
            if (reason is not null)
                return new(window.Label, reason, reset, start, fraction, used, projected);
        }
        return null;
    }

    /// <summary>
    /// Evaluate a whole snapshot for a projected (not-yet-breached) cap cross.
    /// Returns the most-overshooting projected window as a
    /// <see cref="CapEvaluation"/> with <see cref="CapEvaluation.Projected"/>
    /// set, or null when nothing is projected to breach.
    /// </summary>
    public static CapEvaluation? EvaluateProjectedBreach(
        QuotaSnapshot? snapshot, IQuotaCapTable caps, DateTime nowUtc)
    {
        if (snapshot?.Windows == null || snapshot.Windows.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(snapshot.CliType)) return null;

        CapEvaluation? worst = null;
        double worstOvershoot = 0d;
        foreach (var w in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(w.Label)) continue;
            var cap = caps.GetCap(snapshot.CliType, w.Label);
            var proj = Project(w, nowUtc, cap);
            if (proj is null || !proj.BreachesBeforeReset) continue;
            var overshoot = proj.ProjectedUsedPct - proj.CapPct;
            if (worst is null || overshoot > worstOvershoot)
            {
                worstOvershoot = overshoot;
                worst = new CapEvaluation(
                    Blocked: true,
                    CliType: snapshot.CliType,
                    WindowLabel: w.Label,
                    CapPct: cap,
                    UsedPct: w.UsedPct ?? 0d,
                    Projected: true,
                    ResetAt: w.ResetAt);
            }
        }
        return worst;
    }

    /// <summary>
    /// Return the worst (highest projected) window projection for a snapshot,
    /// for logging/event payloads. Null when nothing is projectable.
    /// </summary>
    public static QuotaProjection? WorstProjection(
        QuotaSnapshot? snapshot, IQuotaCapTable caps, DateTime nowUtc)
    {
        if (snapshot?.Windows == null || string.IsNullOrWhiteSpace(snapshot.CliType)) return null;
        QuotaProjection? worst = null;
        foreach (var w in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(w.Label)) continue;
            var proj = Project(w, nowUtc, caps.GetCap(snapshot.CliType, w.Label));
            if (proj is null) continue;
            if (worst is null || proj.ProjectedUsedPct > worst.ProjectedUsedPct) worst = proj;
        }
        return worst;
    }

    /// <summary>
    /// Infer the length of a quota window from its label. CLIs name their
    /// windows in human terms ("5-hour", "Weekly", "Monthly"); we map those to
    /// a duration so the elapsed fraction can be computed. Unknown shapes
    /// return null and are skipped by the projection (never guessed).
    /// </summary>
    public static TimeSpan? InferWindowLength(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var l = label.ToLowerInvariant();
        if (l.Contains("5-hour") || l.Contains("5 hour") || l.Contains("5h") || l.Contains("session"))
            return TimeSpan.FromHours(5);
        if (l.Contains("week") || l.Contains("7-day") || l.Contains("7 day") || l.Contains("7d"))
            return TimeSpan.FromDays(7);
        if (l.Contains("month"))
            return TimeSpan.FromDays(30);
        if (l.Contains("daily") || l.Contains("24-hour") || l.Contains("24 hour") || l.Contains("24h"))
            return TimeSpan.FromHours(24);
        if (l.Contains("hour"))
            return TimeSpan.FromHours(1);
        return null;
    }

    /// <summary>Earliest future-resetting window in a snapshot (optionally only over-cap windows).</summary>
    public static QuotaWindow? EarliestReset(
        QuotaSnapshot? snapshot, DateTime nowUtc, IQuotaCapTable caps, bool blockedOnly)
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
}
