namespace AgentStudio.Pipeline;

/// <summary>Selects T2, the integration gate level, from the strategy, the reviewed delivery and the integration movement.</summary>
public static class PostMergeIntegrationPolicy
{
    /// <summary>
    /// The pre-merge level of a post-merge strategy (AGT-4020).
    /// <list type="bullet">
    /// <item><c>fast</c>: always <see cref="TestExecutionLevels.CompileOnly"/>.
    /// The merged tree has to compile and merge without conflict; no test
    /// project runs before the merge. What the card touched is tested by the
    /// merge-window run after integration (base suite plus affected suites).</item>
    /// <item><c>balanced</c> (and any other post-merge caller): <see cref="Level"/>,
    /// which keeps a work-package run in front of the merge only when develop
    /// moved over a path of the delivery, the merge was replayed or resolved,
    /// or the review evidence cannot prove a disjoint merge.</item>
    /// </list>
    /// </summary>
    public static string ForStrategy(string? strategy, ReviewVerificationRecord? review, string? preMergeTip,
        IReadOnlyList<string>? deliveryPaths, IReadOnlyList<string>? developPaths,
        bool replayedOrResolved)
        => TestingStrategies.Normalize(strategy) == TestingStrategies.Fast
            ? TestExecutionLevels.CompileOnly
            : Level(review, preMergeTip, deliveryPaths, developPaths, replayedOrResolved);

    /// <summary>
    /// Whether <see cref="ForStrategy"/> needs the changed-path sets of the
    /// delivery and of develop's movement. Fast decides without them, so the
    /// integration path does not compute two merge-base diffs per card.
    /// </summary>
    public static bool NeedsPathEvidence(string? strategy)
        => TestingStrategies.Normalize(strategy) != TestingStrategies.Fast;

    /// <summary>The balanced rule (AGT-3314): rerun the affected T1 plan only for shared movement or an uncertain merge.</summary>
    public static string Level(ReviewVerificationRecord? review, string? preMergeTip,
        IReadOnlyList<string>? deliveryPaths, IReadOnlyList<string>? developPaths,
        bool replayedOrResolved)
    {
        if (review is null
            || !string.Equals(review.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(review.BuildTestGate, ReviewBuildTestGateClasses.Passed,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(review.IntegrationTipSha)
            || string.IsNullOrWhiteSpace(preMergeTip) || replayedOrResolved)
            return TestExecutionLevels.WorkPackage;
        if (string.Equals(review.IntegrationTipSha, preMergeTip,
                StringComparison.OrdinalIgnoreCase))
            return TestExecutionLevels.CompileOnly;
        if (deliveryPaths is null || developPaths is null)
            return TestExecutionLevels.WorkPackage;
        var delivery = deliveryPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return developPaths.Any(delivery.Contains)
            ? TestExecutionLevels.WorkPackage : TestExecutionLevels.CompileOnly;
    }
}
