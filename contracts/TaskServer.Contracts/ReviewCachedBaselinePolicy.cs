namespace AgentStudio.TaskServer.Contracts;

/// <summary>What a clean baseline repeat proved about a red cached baseline.</summary>
public enum CleanBaselineOutcome
{
    /// <summary>The clean baseline passed: the cached state produced the red result.</summary>
    CacheAtFault,

    /// <summary>The clean baseline failed as well: the integration branch is red.</summary>
    IntegrationBranchRed,

    /// <summary>The clean baseline did not complete normally or ran no test.</summary>
    Inconclusive,
}

/// <summary>Facts the runner holds when a candidate's clean repeat has run.</summary>
public sealed record CachedBaselineFacts(
    bool CleanRepeatPassed,
    int BaselineExitCode,
    bool BaselineResultReused,
    bool BaselineDependencyCacheHit,
    bool CleanBaselineAlreadyRan);

/// <summary>
/// AGT-3430: a red baseline that ran on cached state does not prove the
/// integration branch red while the candidate's clean repeat of the same
/// command is green. The candidate failed after a dependency-cache hit, its
/// clean repeat passed, and the baseline, also on a cache hit, failed the same
/// way; the review blamed the integration branch for weeks while <c>main</c>
/// was green. Before the card or the branch is blamed the runner repeats the
/// baseline once from a clean state. Green means the cache is at fault, red
/// means the branch really is red.
/// </summary>
public static class ReviewCachedBaselinePolicy
{
    /// <summary>A dependency-cache hit in the baseline's preparation produced the red baseline.</summary>
    public const string DependencyCacheDefect = "DependencyCacheDefect";

    /// <summary>A baseline result reused from the baseline result cache was red.</summary>
    public const string BaselineResultCacheDefect = "BaselineResultCacheDefect";

    /// <summary>Workspace role of the clean baseline repeat's evidence; phase is <c>clean-repeat</c>.</summary>
    public const string WorkspaceRole = "baseline-clean-repeat";

    /// <summary>Evidence phase of the clean baseline repeat.</summary>
    public const string Phase = "clean-repeat";

    public static bool IsCacheDefect(string? classification)
        => classification is DependencyCacheDefect or BaselineResultCacheDefect;

    /// <summary>
    /// True when a preparation command ran on dependency state restored from
    /// the cache. Since AGT-3429 a hit skips the command only when its scopes
    /// cover everything it prepares, so the skip flag alone misses the common
    /// case: the command reran over the restored directories and kept what they
    /// held. A scope that evaluated <c>hit</c> in a fresh clone was restored.
    /// </summary>
    public static bool RanOnCachedDependencies(ReviewCommandEvidenceDto preparation)
        => preparation.DependencyCacheHit
           || (preparation.DependencyCache ?? []).Any(scope => scope.State == "hit");

    /// <summary>
    /// The clean baseline repeat runs only when the candidate's clean repeat
    /// passed, the baseline exited with an ordinary failure, that baseline came
    /// from cached state, and no clean baseline repeat ran in this attempt yet.
    /// The last condition bounds the cost to one extra baseline run per attempt.
    /// </summary>
    public static bool ShouldRepeat(CachedBaselineFacts facts)
        => facts.CleanRepeatPassed
           && OrdinaryFailure(facts.BaselineExitCode)
           && (facts.BaselineResultReused || facts.BaselineDependencyCacheHit)
           && !facts.CleanBaselineAlreadyRan;

    /// <summary>
    /// The classification that names the cache. A reused result is the more
    /// specific cause: no baseline ran in this attempt at all.
    /// </summary>
    public static string Classification(bool baselineResultReused)
        => baselineResultReused ? BaselineResultCacheDefect : DependencyCacheDefect;

    /// <summary>Reads the clean baseline repeat's result.</summary>
    /// <param name="exitCode">Exit code of the clean baseline command.</param>
    /// <param name="signal">Termination signal, if any.</param>
    /// <param name="testResultMissing">
    /// True when a test-failures baseline exited non-zero without naming a
    /// failed test: it never ran the suite and proves nothing (AGT-3089).
    /// </param>
    public static CleanBaselineOutcome Classify(int exitCode, string? signal, bool testResultMissing)
    {
        if (signal is not null) return CleanBaselineOutcome.Inconclusive;
        if (exitCode == 0) return CleanBaselineOutcome.CacheAtFault;
        if (!OrdinaryFailure(exitCode) || testResultMissing) return CleanBaselineOutcome.Inconclusive;
        return CleanBaselineOutcome.IntegrationBranchRed;
    }

    private static bool OrdinaryFailure(int exitCode)
        => exitCode is > 0 and < 128 and not 124 and not 127;
}
