namespace AgentStudio.Scenario;

/// <summary>Why a step is in the plan, or why it is not executed.</summary>
public enum ScenarioStepDisposition
{
    Run,

    /// <summary>The step is not declared for the selected target.</summary>
    SkippedByTarget,

    /// <summary>The step is a full-level step and the run is a smoke run.</summary>
    SkippedByLevel,
}

public sealed record PlannedScenarioStep(
    ScenarioStep Step,
    ScenarioStepDisposition Disposition,
    string Reason);

/// <summary>
/// Pure selection policy. Given a document, a target, and a level, it decides
/// for every step whether it runs and why not otherwise. It touches no clock,
/// no filesystem, and no network, so the target and level matrix is tested
/// directly rather than through a live run.
/// </summary>
public static class ScenarioPlanner
{
    public static IReadOnlyList<PlannedScenarioStep> Plan(
        ScenarioDocument document,
        ScenarioTargetKind target,
        ScenarioLevel level)
        => document.Steps.Select(step => Decide(step, target, level)).ToList();

    private static PlannedScenarioStep Decide(
        ScenarioStep step,
        ScenarioTargetKind target,
        ScenarioLevel level)
    {
        // The target gate wins over the level gate: a step that cannot run on
        // this topology at all must report that reason, not "not in smoke".
        if (!step.Targets.Contains(target))
            return new PlannedScenarioStep(
                step,
                ScenarioStepDisposition.SkippedByTarget,
                $"declared only for {string.Join(", ", step.Targets.Select(ScenarioDocumentLoader.Render))}");

        if (level == ScenarioLevel.Smoke && step.Level == ScenarioLevel.Full)
            return new PlannedScenarioStep(
                step,
                ScenarioStepDisposition.SkippedByLevel,
                "full level only");

        return new PlannedScenarioStep(step, ScenarioStepDisposition.Run, string.Empty);
    }
}
