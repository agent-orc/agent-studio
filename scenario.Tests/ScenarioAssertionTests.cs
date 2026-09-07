using Xunit;

namespace AgentStudio.Scenario.Tests;

public sealed class ScenarioAssertionTests
{
    private static ScenarioExpectation Exact(string fact, ScenarioFactValue value)
        => new(
            new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal) { [fact] = value },
            new Dictionary<string, long>(StringComparer.Ordinal));

    private static ScenarioExpectation Floor(string fact, long value)
        => new(
            new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal),
            new Dictionary<string, long>(StringComparer.Ordinal) { [fact] = value });

    private static Dictionary<string, ScenarioFactValue> Observed(
        string fact,
        ScenarioFactValue value)
        => new(StringComparer.Ordinal) { [fact] = value };

    private static readonly Dictionary<string, ScenarioFactValue> Nothing =
        new(StringComparer.Ordinal);

    [Fact]
    public void An_equal_typed_value_is_satisfied()
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(
            Exact("task.state", ScenarioFactValue.Text("6-completed")),
            Observed("task.state", ScenarioFactValue.Text("6-completed"))));

        Assert.True(outcome.Satisfied);
        Assert.Equal(ScenarioAssertionReasons.Satisfied, outcome.Reason);
    }

    [Fact]
    public void A_different_value_of_the_same_kind_reports_a_value_mismatch()
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(
            Exact("task.state", ScenarioFactValue.Text("6-completed")),
            Observed("task.state", ScenarioFactValue.Text("4-auto-review"))));

        Assert.False(outcome.Satisfied);
        Assert.Equal(ScenarioAssertionReasons.ValueMismatch, outcome.Reason);
        Assert.Equal("text:4-auto-review", outcome.Actual);
    }

    [Fact]
    public void A_missing_fact_is_a_failure_and_never_a_silent_pass()
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(
            Exact("task.state", ScenarioFactValue.Text("6-completed")),
            Nothing));

        Assert.False(outcome.Satisfied);
        Assert.Equal(ScenarioAssertionReasons.FactMissing, outcome.Reason);
        Assert.Equal("<missing>", outcome.Actual);
    }

    [Fact]
    public void A_text_two_does_not_satisfy_a_numeric_two()
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(
            Exact("run.count", ScenarioFactValue.Number(2)),
            Observed("run.count", ScenarioFactValue.Text("2"))));

        Assert.False(outcome.Satisfied);
        Assert.Equal(ScenarioAssertionReasons.KindMismatch, outcome.Reason);
    }

    [Fact]
    public void A_text_true_does_not_satisfy_a_boolean_true()
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(
            Exact("backup.created", ScenarioFactValue.Boolean(true)),
            Observed("backup.created", ScenarioFactValue.Text("true"))));

        Assert.False(outcome.Satisfied);
        Assert.Equal(ScenarioAssertionReasons.KindMismatch, outcome.Reason);
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, 1, false)]
    public void A_floor_accepts_anything_at_or_above_it(long observed, long floor, bool satisfied)
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(
            Floor("artifacts.count", floor),
            Observed("artifacts.count", ScenarioFactValue.Number(observed))));

        Assert.Equal(satisfied, outcome.Satisfied);
        if (!satisfied) Assert.Equal(ScenarioAssertionReasons.BelowFloor, outcome.Reason);
    }

    [Fact]
    public void A_floor_over_a_non_numeric_fact_is_a_kind_mismatch()
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(
            Floor("task.state", 1),
            Observed("task.state", ScenarioFactValue.Text("6-completed"))));

        Assert.False(outcome.Satisfied);
        Assert.Equal(ScenarioAssertionReasons.KindMismatch, outcome.Reason);
    }

    [Fact]
    public void A_floor_over_a_missing_fact_is_a_failure()
    {
        var outcome = Assert.Single(ScenarioAssertions.Evaluate(Floor("events.total", 1), Nothing));

        Assert.False(outcome.Satisfied);
        Assert.Equal(ScenarioAssertionReasons.FactMissing, outcome.Reason);
    }

    [Fact]
    public void Outcomes_are_ordered_by_fact_name_so_a_report_is_stable()
    {
        var expectation = new ScenarioExpectation(
            new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal)
            {
                ["z.fact"] = ScenarioFactValue.Number(1),
                ["a.fact"] = ScenarioFactValue.Number(1),
            },
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["m.floor"] = 1,
                ["b.floor"] = 1,
            });

        var outcomes = ScenarioAssertions.Evaluate(expectation, Nothing);

        Assert.Equal(["a.fact", "z.fact", "b.floor", "m.floor"], outcomes.Select(item => item.Fact));
    }

    [Fact]
    public void Facts_that_are_observed_but_not_expected_are_reported_not_asserted()
    {
        var observed = new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal)
        {
            ["task.state"] = ScenarioFactValue.Text("6-completed"),
            ["status.result"] = ScenarioFactValue.Text("Partial"),
        };

        var outcomes = ScenarioAssertions.Evaluate(
            Exact("task.state", ScenarioFactValue.Text("6-completed")),
            observed);

        Assert.True(Assert.Single(outcomes).Satisfied);
    }

    [Theory]
    [InlineData(ScenarioFactKind.Number, "7")]
    [InlineData(ScenarioFactKind.Boolean, "true")]
    [InlineData(ScenarioFactKind.Text, "value")]
    public void Rendering_is_invariant_so_a_report_is_identical_on_every_host(
        ScenarioFactKind kind,
        string expected)
    {
        var value = kind switch
        {
            ScenarioFactKind.Number => ScenarioFactValue.Number(7),
            ScenarioFactKind.Boolean => ScenarioFactValue.Boolean(true),
            _ => ScenarioFactValue.Text("value"),
        };

        Assert.Equal(expected, value.Render());
    }
}
