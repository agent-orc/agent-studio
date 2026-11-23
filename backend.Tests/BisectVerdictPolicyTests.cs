using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BisectVerdictPolicyTests
{
    [Theory]
    [InlineData(RedTestResult.Pass, RedTestResult.Pass, RedTestPointVerdict.Pass)]
    [InlineData(RedTestResult.Fail, RedTestResult.Fail, RedTestPointVerdict.Fail)]
    [InlineData(RedTestResult.Pass, RedTestResult.Fail, RedTestPointVerdict.Mixed)]
    [InlineData(RedTestResult.Fail, RedTestResult.Pass, RedTestPointVerdict.Mixed)]
    [InlineData(RedTestResult.Unknown, RedTestResult.Pass, RedTestPointVerdict.Mixed)]
    [InlineData(RedTestResult.Fail, RedTestResult.Unknown, RedTestPointVerdict.Mixed)]
    public void Double_run_verdict(RedTestResult first, RedTestResult second, RedTestPointVerdict expected)
        => Assert.Equal(expected, BisectVerdictPolicy.Verdict(new(first, second)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Control_requires_two_passes_and_never_attributes_a_failing_last_green(int controls)
    {
        RedTestBisectPoint[] points = [new("suspect", new(RedTestResult.Fail, RedTestResult.Fail), new(RedTestResult.Pass, RedTestResult.Pass))];
        var passes = Enumerable.Repeat(RedTestResult.Pass, controls).ToArray();
        var decision = BisectVerdictPolicy.Decide(passes, points);
        Assert.Equal(controls == 2 ? RedTestClassification.Regression : RedTestClassification.Inconclusive, decision.Classification);
        if (controls != 2) Assert.Null(decision.AttributedMerge);
        var failure = BisectVerdictPolicy.Decide([RedTestResult.Fail], points);
        Assert.Equal(RedTestClassification.Environment, failure.Classification);
        Assert.Null(failure.AttributedMerge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_results_stop_search_even_when_another_point_looks_like_a_boundary(bool parentMixed)
    {
        var pass = new RedTestRunPair(RedTestResult.Pass, RedTestResult.Pass);
        var fail = new RedTestRunPair(RedTestResult.Fail, RedTestResult.Fail);
        var mixed = new RedTestRunPair(RedTestResult.Pass, RedTestResult.Fail);
        var decision = BisectVerdictPolicy.Decide([RedTestResult.Pass, RedTestResult.Pass],
            [new("apparent-boundary", fail, pass), new("mixed", parentMixed ? fail : mixed, parentMixed ? mixed : pass)]);
        Assert.Equal(RedTestClassification.Flaky, decision.Classification);
        Assert.Null(decision.AttributedMerge);
    }

    [Fact]
    public void Too_many_control_results_are_rejected()
        => Assert.Throws<ArgumentException>(() => BisectVerdictPolicy.Decide(
            [RedTestResult.Pass, RedTestResult.Pass, RedTestResult.Pass], []));
}
