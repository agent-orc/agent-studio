using System.Diagnostics;

namespace AgentStudio.TestSupport.Scenario;

public enum ScenarioStepStatus
{
    Passed,
    Failed,
    Skipped,
}

public sealed record ScenarioStepResult(
    string Id,
    string Title,
    ScenarioStepStatus Status,
    TimeSpan Duration,
    string? Evidence = null,
    string? FailureMessage = null);

public sealed record ScenarioRunResult(string Level, IReadOnlyList<ScenarioStepResult> Steps, TimeSpan TotalDuration)
{
    public bool Success => Steps.All(step => step.Status == ScenarioStepStatus.Passed);
}

/// <summary>
/// Runs a scenario's ordered steps against a target-specific executor, stopping
/// at the first failure (each step depends on the state the previous one left
/// behind, so continuing past a failure would only produce confusing secondary
/// failures) and marking the remaining steps skipped rather than silently
/// omitting them from the report.
/// </summary>
public static class ScenarioRunner
{
    public static async Task<ScenarioRunResult> RunAsync(
        IReadOnlyList<ScenarioStepDefinition> steps,
        string level,
        Func<ScenarioStepDefinition, Task<string?>> executeStep)
    {
        var results = new List<ScenarioStepResult>();
        var total = Stopwatch.StartNew();
        var failed = false;
        foreach (var step in steps)
        {
            if (failed)
            {
                results.Add(new ScenarioStepResult(step.Id, step.Title, ScenarioStepStatus.Skipped, TimeSpan.Zero));
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var evidence = await executeStep(step);
                results.Add(new ScenarioStepResult(
                    step.Id, step.Title, ScenarioStepStatus.Passed, stopwatch.Elapsed, evidence));
            }
            catch (Exception exception)
            {
                failed = true;
                results.Add(new ScenarioStepResult(
                    step.Id,
                    step.Title,
                    ScenarioStepStatus.Failed,
                    stopwatch.Elapsed,
                    FailureMessage: exception.ToString()));
            }
        }

        return new ScenarioRunResult(level, results, total.Elapsed);
    }
}
