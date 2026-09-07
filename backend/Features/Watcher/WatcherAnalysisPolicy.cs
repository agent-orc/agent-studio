namespace AgentStudio.Watcher;

/// <summary>Which model route, if any, a case earns under section 5 of the dossier.</summary>
public enum WatcherAnalysisRoute
{
    /// <summary>Deterministic code and pure policy. The default, and the only route for bookkeeping classes.</summary>
    None,

    /// <summary>Mini/high, bounded read-only compression of an oversized evidence pack.</summary>
    Compression,

    /// <summary>Sol/medium, the strong-analysis floor for a genuinely ambiguous root cause.</summary>
    StrongAnalysis,
}

/// <summary>What the sweep decided to spend, and why. The reason is recorded on the proposal.</summary>
public sealed record WatcherAnalysisPlan(WatcherAnalysisRoute Route, string Reason)
{
    public bool NeedsModel => Route != WatcherAnalysisRoute.None;
}

/// <summary>
/// Pure model-economy policy. Cost follows uncertainty, and only after the
/// correctness floor is established: no model is used for detection, counting,
/// fingerprints, thresholds, or admission.
/// </summary>
/// <remarks>
/// Repetition and hygiene are bookkeeping. The detector already knows the
/// fingerprint, the count, and the affected cards, so a model would add
/// nothing but cost. Contradiction and drift are the two classes where the
/// signal is a disagreement rather than a diagnosis, which is exactly the
/// "declared uncertainty" the dossier reserves strong analysis for.
/// </remarks>
public static class WatcherAnalysisPolicy
{
    public static WatcherAnalysisPlan Plan(WatcherCase watcherCase, int evidenceChars)
    {
        ArgumentNullException.ThrowIfNull(watcherCase);

        if (evidenceChars > WatcherDefaults.MaxEvidenceValueChars * WatcherDefaults.MaxEvidenceItems)
        {
            return new WatcherAnalysisPlan(
                WatcherAnalysisRoute.Compression,
                $"evidence pack of {evidenceChars} characters exceeds the bounded pack limit");
        }

        return watcherCase.DetectorClass switch
        {
            WatcherDetectorClasses.Contradiction => new WatcherAnalysisPlan(
                WatcherAnalysisRoute.StrongAnalysis,
                "two sources disagree and no single source is authoritative"),
            WatcherDetectorClasses.Drift => new WatcherAnalysisPlan(
                WatcherAnalysisRoute.StrongAnalysis,
                "the version change is correlated with the failure but causation is not proven"),
            _ => new WatcherAnalysisPlan(
                WatcherAnalysisRoute.None,
                $"the {watcherCase.DetectorClass} class is decided by counting, so no model call is warranted"),
        };
    }

    /// <summary>
    /// Bounded token estimate used to admit a call against the contingent
    /// before it is made. The dossier's planning envelope: an E-call is about
    /// 11k tokens, an S-call about 44k.
    /// </summary>
    public static long EstimatedTokens(WatcherAnalysisRoute route) => route switch
    {
        WatcherAnalysisRoute.Compression => 11_000,
        WatcherAnalysisRoute.StrongAnalysis => 44_000,
        _ => 0,
    };
}

/// <summary>Result of one bounded analysis call, ready to fold into a proposal.</summary>
public sealed record WatcherAnalysis(string Note, WatcherAnalysisReceipt Receipt);

/// <summary>
/// The model seam. W1 and W2 ship with an implementation that declines every
/// call, so shadow mode spends nothing; a later slice can supply a real
/// analyzer without touching the sweep.
/// </summary>
public interface IWatcherAnalyst
{
    Task<WatcherAnalysis?> AnalyseAsync(
        WatcherCase watcherCase,
        WatcherAnalysisPlan plan,
        CancellationToken ct);
}

/// <summary>
/// Records the route the policy chose and makes no call. This is the honest
/// shadow-mode default: the case still says which route it would have earned,
/// and the contingent is never touched.
/// </summary>
public sealed class DecliningWatcherAnalyst : IWatcherAnalyst
{
    public Task<WatcherAnalysis?> AnalyseAsync(
        WatcherCase watcherCase,
        WatcherAnalysisPlan plan,
        CancellationToken ct)
        => Task.FromResult<WatcherAnalysis?>(null);
}
