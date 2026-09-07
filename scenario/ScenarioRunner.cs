using System.Diagnostics;

namespace AgentStudio.Scenario;

/// <summary>
/// Executes a plan against a started target. Ordering is the document order;
/// the first failure stops the run, because a later step reads what an earlier
/// one recorded and would otherwise report a cascade of derived failures.
/// </summary>
public static class ScenarioRunner
{
    public static async Task<ScenarioRunResult> RunAsync(
        ScenarioDocument document,
        IReadOnlyList<PlannedScenarioStep> plan,
        ScenarioRunContext context,
        ScenarioTargetKind target,
        ScenarioLevel level,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var results = new List<ScenarioStepResult>(plan.Count);
        var total = Stopwatch.StartNew();
        var stopped = false;

        foreach (var planned in plan)
        {
            if (planned.Disposition != ScenarioStepDisposition.Run)
            {
                results.Add(ScenarioStepResult.Skipped(planned));
                continue;
            }
            if (stopped)
            {
                results.Add(ScenarioStepResult.Skipped(new PlannedScenarioStep(
                    planned.Step,
                    ScenarioStepDisposition.SkippedByLevel,
                    "not reached: an earlier step failed")));
                continue;
            }

            var result = await ExecuteAsync(context, planned.Step, ct);
            results.Add(result);
            Report(result);
            if (result.Status == ScenarioStepStatus.Failed) stopped = true;
        }

        total.Stop();
        return new ScenarioRunResult(
            document.Id,
            document.Title,
            target,
            level,
            context.ServerVersion,
            startedAt,
            total.Elapsed,
            results);
    }

    private static async Task<ScenarioStepResult> ExecuteAsync(
        ScenarioRunContext context,
        ScenarioStep step,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var action = await ScenarioActions.ExecuteAsync(context, step, ct);
            watch.Stop();
            var assertions = ScenarioAssertions.Evaluate(step.Expect, action.Facts);
            var unsatisfied = assertions.Count(assertion => !assertion.Satisfied);
            return new ScenarioStepResult(
                step.Id,
                step.Title,
                step.Action,
                unsatisfied == 0 ? ScenarioStepStatus.Passed : ScenarioStepStatus.Failed,
                ScenarioStepDisposition.Run,
                watch.Elapsed,
                action.Facts,
                assertions,
                action.Evidence,
                unsatisfied == 0
                    ? null
                    : $"{unsatisfied} of {assertions.Count} assertions were not satisfied.");
        }
        catch (Exception exception) when (
            exception is ScenarioStepException
                or ScenarioTargetException
                or HttpRequestException
                or IOException
                or TaskCanceledException)
        {
            watch.Stop();
            return new ScenarioStepResult(
                step.Id,
                step.Title,
                step.Action,
                ScenarioStepStatus.Failed,
                ScenarioStepDisposition.Run,
                watch.Elapsed,
                new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal),
                [],
                [],
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Report(ScenarioStepResult result)
    {
        var status = result.Status == ScenarioStepStatus.Passed ? "pass" : "FAIL";
        Console.WriteLine(
            $"[{status}] {result.Id} ({result.Duration.TotalSeconds:0.00} s) - {result.Title}");
        if (result.Status != ScenarioStepStatus.Failed) return;
        if (!string.IsNullOrWhiteSpace(result.Failure))
            Console.WriteLine($"       {result.Failure}");
        foreach (var assertion in result.Assertions.Where(item => !item.Satisfied))
            Console.WriteLine(
                $"       {assertion.Fact}: expected {assertion.Expected}, "
                + $"observed {assertion.Actual} ({assertion.Reason})");
    }
}
