namespace AgentStudio.Pipeline;

/// <summary>Selects T2 from the reviewed delivery and the integration movement.</summary>
public static class PostMergeIntegrationPolicy
{
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
