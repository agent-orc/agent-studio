using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewCachedBaselinePolicyTests
{
    [Theory]
    // AGT-3430 shape: candidate red on a cache hit, clean repeat green,
    // baseline red on a cache hit.
    [InlineData(true, 1, false, true, false, true)]
    [InlineData(true, 1, true, false, false, true)]
    [InlineData(true, 2, true, true, false, true)]
    // The baseline ran from scratch: its red is the branch's own.
    [InlineData(true, 1, false, false, false, false)]
    // The clean repeat was red too: today's diagnosis decides.
    [InlineData(false, 1, false, true, false, false)]
    // A green or abnormal baseline proves nothing about a cache.
    [InlineData(true, 0, false, true, false, false)]
    [InlineData(true, 124, false, true, false, false)]
    [InlineData(true, 127, false, true, false, false)]
    [InlineData(true, 137, true, true, false, false)]
    // At most one clean baseline repeat per attempt.
    [InlineData(true, 1, true, true, true, false)]
    public void Clean_baseline_repeat_runs_only_for_a_red_cached_baseline_behind_a_green_clean_repeat(
        bool cleanRepeatPassed, int baselineExit, bool resultReused, bool dependencyCacheHit,
        bool alreadyRan, bool expected)
        => Assert.Equal(expected, ReviewCachedBaselinePolicy.ShouldRepeat(new CachedBaselineFacts(
            cleanRepeatPassed, baselineExit, resultReused, dependencyCacheHit, alreadyRan)));

    [Theory]
    [InlineData(0, null, false, CleanBaselineOutcome.CacheAtFault)]
    [InlineData(1, null, false, CleanBaselineOutcome.IntegrationBranchRed)]
    [InlineData(2, null, false, CleanBaselineOutcome.IntegrationBranchRed)]
    [InlineData(1, null, true, CleanBaselineOutcome.Inconclusive)]
    [InlineData(124, null, false, CleanBaselineOutcome.Inconclusive)]
    [InlineData(127, null, false, CleanBaselineOutcome.Inconclusive)]
    [InlineData(137, null, false, CleanBaselineOutcome.Inconclusive)]
    [InlineData(-1, null, false, CleanBaselineOutcome.Inconclusive)]
    [InlineData(0, "timeout", false, CleanBaselineOutcome.Inconclusive)]
    [InlineData(1, "stalled", false, CleanBaselineOutcome.Inconclusive)]
    public void Clean_baseline_result_decides_between_cache_and_integration_branch(
        int exitCode, string? signal, bool testResultMissing, CleanBaselineOutcome expected)
        => Assert.Equal(expected, ReviewCachedBaselinePolicy.Classify(exitCode, signal, testResultMissing));

    [Fact]
    public void Classification_names_the_cache_that_served_the_red_baseline()
    {
        Assert.Equal(ReviewCachedBaselinePolicy.BaselineResultCacheDefect,
            ReviewCachedBaselinePolicy.Classification(baselineResultReused: true));
        Assert.Equal(ReviewCachedBaselinePolicy.DependencyCacheDefect,
            ReviewCachedBaselinePolicy.Classification(baselineResultReused: false));
        Assert.True(ReviewCachedBaselinePolicy.IsCacheDefect(ReviewCachedBaselinePolicy.DependencyCacheDefect));
        Assert.True(ReviewCachedBaselinePolicy.IsCacheDefect(ReviewCachedBaselinePolicy.BaselineResultCacheDefect));
        Assert.False(ReviewCachedBaselinePolicy.IsCacheDefect(DeliveryFailureDiagnosis.Environment));
        Assert.False(ReviewCachedBaselinePolicy.IsCacheDefect(null));
    }

    /// <summary>
    /// AGT-3429 reruns a preparation command over a restored cache unless its
    /// scopes cover it, so a rerun with a restored scope still counts as cached
    /// state; only a scope that missed the cache is a clean baseline.
    /// </summary>
    [Theory]
    [InlineData(true, "hit", true)]
    [InlineData(false, "hit", true)]
    [InlineData(false, "miss", false)]
    public void Baseline_preparation_ran_on_cached_dependencies_when_a_scope_was_restored(
        bool skipped, string scopeState, bool expected)
    {
        var preparation = new ReviewCommandEvidenceDto(
            "prepare-1", "preparation", "sh", [], "sha", "head", "tree",
            default, default, 0, null, "", "",
            Phase: "preparation", WorkspaceRole: "baseline-0123456789ab",
            DependencyCacheHit: skipped,
            DependencyCache: [new ReviewDependencyCacheEvidenceDto(
                "", scopeState, "lock-hash-match", "hash", ["package-lock.json"], InstallRan: !skipped)]);

        Assert.Equal(expected, ReviewCachedBaselinePolicy.RanOnCachedDependencies(preparation));
    }
}
