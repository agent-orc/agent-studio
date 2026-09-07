using System.Text.Json;
using Xunit;

namespace AgentStudio.Scenario.Tests;

public sealed class ScenarioPlannerTests
{
    [Theory]
    // A smoke step declared for the target always runs.
    [InlineData(ScenarioLevel.Smoke, ScenarioLevel.Smoke, true, ScenarioStepDisposition.Run)]
    [InlineData(ScenarioLevel.Full, ScenarioLevel.Smoke, true, ScenarioStepDisposition.Run)]
    // A full step runs only in a full run.
    [InlineData(ScenarioLevel.Full, ScenarioLevel.Full, true, ScenarioStepDisposition.Run)]
    [InlineData(ScenarioLevel.Smoke, ScenarioLevel.Full, true, ScenarioStepDisposition.SkippedByLevel)]
    // The target gate wins: a step that cannot run here reports that reason even
    // when the level would also have excluded it.
    [InlineData(ScenarioLevel.Smoke, ScenarioLevel.Smoke, false, ScenarioStepDisposition.SkippedByTarget)]
    [InlineData(ScenarioLevel.Full, ScenarioLevel.Full, false, ScenarioStepDisposition.SkippedByTarget)]
    [InlineData(ScenarioLevel.Smoke, ScenarioLevel.Full, false, ScenarioStepDisposition.SkippedByTarget)]
    public void The_target_and_level_matrix_decides_every_step(
        ScenarioLevel runLevel,
        ScenarioLevel stepLevel,
        bool declaredForTarget,
        ScenarioStepDisposition expected)
    {
        var document = Document(Step(
            "step",
            stepLevel,
            declaredForTarget
                ? [ScenarioTargetKind.InProc]
                : [ScenarioTargetKind.Compose]));

        var plan = ScenarioPlanner.Plan(document, ScenarioTargetKind.InProc, runLevel);

        Assert.Equal(expected, Assert.Single(plan).Disposition);
    }

    [Fact]
    public void A_skipped_step_carries_the_reason_the_report_shows()
    {
        var document = Document(
            Step("by-target", ScenarioLevel.Smoke, [ScenarioTargetKind.Remote]),
            Step("by-level", ScenarioLevel.Full, [ScenarioTargetKind.InProc]));

        var plan = ScenarioPlanner.Plan(document, ScenarioTargetKind.InProc, ScenarioLevel.Smoke);

        Assert.Contains("remote", plan[0].Reason);
        Assert.Equal("full level only", plan[1].Reason);
    }

    [Fact]
    public void Planning_preserves_document_order_because_later_steps_read_earlier_state()
    {
        var document = Document(
            Step("first", ScenarioLevel.Smoke, [ScenarioTargetKind.InProc]),
            Step("second", ScenarioLevel.Full, [ScenarioTargetKind.InProc]),
            Step("third", ScenarioLevel.Smoke, [ScenarioTargetKind.InProc]));

        var plan = ScenarioPlanner.Plan(document, ScenarioTargetKind.InProc, ScenarioLevel.Full);

        Assert.Equal(["first", "second", "third"], plan.Select(step => step.Step.Id));
    }

    [Fact]
    public void Every_step_appears_in_the_plan_so_the_report_accounts_for_all_of_them()
    {
        var document = Document(
            Step("a", ScenarioLevel.Smoke, [ScenarioTargetKind.InProc]),
            Step("b", ScenarioLevel.Full, [ScenarioTargetKind.Compose]),
            Step("c", ScenarioLevel.Full, [ScenarioTargetKind.Remote]));

        var plan = ScenarioPlanner.Plan(document, ScenarioTargetKind.InProc, ScenarioLevel.Smoke);

        Assert.Equal(document.Steps.Count, plan.Count);
    }

    internal static ScenarioDocument Document(params ScenarioStep[] steps)
        => new(1, "test-scenario", "Test scenario", steps);

    internal static ScenarioStep Step(
        string id,
        ScenarioLevel level,
        IReadOnlyList<ScenarioTargetKind> targets)
        => new(
            id,
            $"Title of {id}",
            level,
            ScenarioActions.TopologyReady,
            targets,
            JsonDocument.Parse("{}").RootElement.Clone(),
            ScenarioExpectation.Empty);
}
