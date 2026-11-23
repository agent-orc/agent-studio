namespace AgentStudio.OrchestrationDecisions;

public enum RedTestPointVerdict { Pass, Fail, Mixed }
public sealed record RedTestRunPair(RedTestResult First, RedTestResult Second);
/// <summary>A tested first-parent point with the independently measured runs of its parent.</summary>
public sealed record RedTestBisectPoint(string Sha, RedTestRunPair Runs, RedTestRunPair ParentRuns);
public sealed record RedTestBisectDecision(RedTestClassification Classification, string? AttributedMerge = null);

public static class BisectVerdictPolicy
{
    public static RedTestPointVerdict Verdict(RedTestRunPair runs) => runs switch
    {
        { First: RedTestResult.Fail, Second: RedTestResult.Fail } => RedTestPointVerdict.Fail,
        { First: RedTestResult.Pass, Second: RedTestResult.Pass } => RedTestPointVerdict.Pass,
        _ => RedTestPointVerdict.Mixed,
    };

    /// <summary>Points may arrive in search order. A control failure attributes no merge.</summary>
    public static RedTestBisectDecision Decide(IReadOnlyList<RedTestResult> controls,
        IReadOnlyList<RedTestBisectPoint> points)
    {
        if (controls.Count > 2) throw new ArgumentException("At most two control results are allowed.", nameof(controls));
        if (controls.Contains(RedTestResult.Fail)) return new(RedTestClassification.Environment);
        if (points.Any(p => Verdict(p.Runs) == RedTestPointVerdict.Mixed
            || Verdict(p.ParentRuns) == RedTestPointVerdict.Mixed))
            return new(RedTestClassification.Flaky);
        if (controls.Count != 2 || controls.Any(r => r != RedTestResult.Pass))
            return new(RedTestClassification.Inconclusive);
        var boundary = points.FirstOrDefault(p => Verdict(p.Runs) == RedTestPointVerdict.Fail
            && Verdict(p.ParentRuns) == RedTestPointVerdict.Pass);
        return boundary is null ? new(RedTestClassification.Ambiguous)
            : new(RedTestClassification.Regression, boundary.Sha);
    }
}
