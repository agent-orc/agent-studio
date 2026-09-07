using System.Globalization;
using System.Security;
using System.Text;

namespace AgentStudio.Scenario;

/// <summary>
/// JUnit XML for CI. One test suite per scenario, one test case per step, so a
/// failing step names itself in the CI test list instead of hiding inside a
/// shell log.
/// </summary>
public static class ScenarioJUnitReport
{
    public static string Render(ScenarioRunResult result)
    {
        var suiteName = $"{result.ScenarioId}.{ScenarioDocumentLoader.Render(result.Target)}";
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        builder.AppendLine(
            $"<testsuites name=\"{Escape(result.ScenarioId)}\" tests=\"{result.Steps.Count}\" "
            + $"failures=\"{result.Failures}\" skipped=\"{result.SkippedCount}\" "
            + $"time=\"{Seconds(result.Duration)}\">");
        builder.AppendLine(
            $"  <testsuite name=\"{Escape(suiteName)}\" tests=\"{result.Steps.Count}\" "
            + $"failures=\"{result.Failures}\" skipped=\"{result.SkippedCount}\" "
            + $"time=\"{Seconds(result.Duration)}\" "
            + $"timestamp=\"{result.StartedAt.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}\">");
        builder.AppendLine("    <properties>");
        builder.AppendLine(
            $"      <property name=\"target\" value=\"{Escape(ScenarioDocumentLoader.Render(result.Target))}\" />");
        builder.AppendLine(
            $"      <property name=\"level\" value=\"{Escape(ScenarioDocumentLoader.Render(result.Level))}\" />");
        builder.AppendLine(
            $"      <property name=\"serverVersion\" value=\"{Escape(result.ServerVersion)}\" />");
        builder.AppendLine("    </properties>");

        foreach (var step in result.Steps)
        {
            builder.Append(
                $"    <testcase classname=\"{Escape(suiteName)}\" name=\"{Escape(step.Id)}\" "
                + $"time=\"{Seconds(step.Duration)}\"");
            if (step.Status == ScenarioStepStatus.Passed)
            {
                builder.AppendLine(" />");
                continue;
            }
            builder.AppendLine(">");
            if (step.Status == ScenarioStepStatus.Skipped)
            {
                builder.AppendLine($"      <skipped message=\"{Escape(step.Failure ?? "skipped")}\" />");
            }
            else
            {
                builder.AppendLine(
                    $"      <failure message=\"{Escape(step.Failure ?? "assertion failed")}\" "
                    + "type=\"scenario-step\">");
                foreach (var assertion in step.Assertions.Where(item => !item.Satisfied))
                    builder.AppendLine(
                        "        " + Escape(
                            $"{assertion.Fact}: expected {assertion.Expected}, observed {assertion.Actual} "
                            + $"({assertion.Reason})"));
                builder.AppendLine("      </failure>");
            }
            builder.AppendLine("    </testcase>");
        }

        builder.AppendLine("  </testsuite>");
        builder.AppendLine("</testsuites>");
        return builder.ToString();
    }

    private static string Seconds(TimeSpan duration)
        => duration.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}

/// <summary>
/// Markdown scenario report. This is the artifact a deployment card attaches to
/// its status, so the step table carries status, duration, and the evidence
/// link for every step, including the ones that did not run.
/// </summary>
public static class ScenarioMarkdownReport
{
    public static string Render(ScenarioRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {result.Title}");
        builder.AppendLine();
        builder.AppendLine($"- Scenario: `{result.ScenarioId}`");
        builder.AppendLine($"- Target: `{ScenarioDocumentLoader.Render(result.Target)}`");
        builder.AppendLine($"- Level: `{ScenarioDocumentLoader.Render(result.Level)}`");
        builder.AppendLine($"- Server version: `{result.ServerVersion}`");
        builder.AppendLine(
            $"- Started: {result.StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
        builder.AppendLine($"- Duration: {Duration(result.Duration)}");
        builder.AppendLine(
            $"- Result: {(result.Passed ? "passed" : "failed")} "
            + $"({result.PassedCount} passed, {result.Failures} failed, {result.SkippedCount} skipped)");
        builder.AppendLine();
        builder.AppendLine("## Steps");
        builder.AppendLine();
        builder.AppendLine("| # | Step | Status | Duration | Evidence |");
        builder.AppendLine("|---|---|---|---|---|");

        var ordinal = 0;
        foreach (var step in result.Steps)
        {
            ordinal++;
            var evidence = step.Evidence.Count == 0
                ? "-"
                : string.Join(
                    " · ",
                    step.Evidence.Select(path => $"[{Path.GetFileName(path)}]({path})"));
            builder.AppendLine(
                $"| {ordinal} | {Cell(step.Title)}<br>`{step.Id}` | {Status(step)} "
                + $"| {Duration(step.Duration)} | {evidence} |");
        }

        var failed = result.Steps.Where(step => step.Status == ScenarioStepStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failures");
            foreach (var step in failed)
            {
                builder.AppendLine();
                builder.AppendLine($"### {Cell(step.Title)} (`{step.Id}`)");
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(step.Failure))
                {
                    builder.AppendLine("```");
                    builder.AppendLine(step.Failure.Trim());
                    builder.AppendLine("```");
                    builder.AppendLine();
                }
                var unsatisfied = step.Assertions.Where(item => !item.Satisfied).ToList();
                if (unsatisfied.Count == 0) continue;
                builder.AppendLine("| Fact | Expected | Observed | Reason |");
                builder.AppendLine("|---|---|---|---|");
                foreach (var assertion in unsatisfied)
                    builder.AppendLine(
                        $"| `{assertion.Fact}` | `{assertion.Expected}` | `{assertion.Actual}` "
                        + $"| {assertion.Reason} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Observed facts");
        builder.AppendLine();
        builder.AppendLine(
            "Every fact a step observed. `Expected` reads `-` for a fact the scenario "
            + "reports without asserting it.");
        builder.AppendLine();
        builder.AppendLine("| Step | Fact | Expected | Observed |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var step in result.Steps)
        {
            // A document may assert the same fact both exactly and by floor, so
            // group rather than index: a duplicate key must not break a report.
            var asserted = step.Assertions
                .GroupBy(item => item.Fact, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => string.Join(" and ", group.Select(item => item.Expected)),
                    StringComparer.Ordinal);
            foreach (var fact in step.Facts.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                var expected = asserted.TryGetValue(fact, out var value) ? $"`{value}`" : "-";
                builder.AppendLine(
                    $"| `{step.Id}` | `{fact}` | {expected} | `{step.Facts[fact]}` |");
            }
            // An assertion whose fact was never published still has to appear.
            foreach (var assertion in step.Assertions.Where(item => !step.Facts.ContainsKey(item.Fact)))
                builder.AppendLine(
                    $"| `{step.Id}` | `{assertion.Fact}` | `{assertion.Expected}` "
                    + $"| `{assertion.Actual}` |");
        }

        return builder.ToString();
    }

    private static string Status(ScenarioStepResult step) => step.Status switch
    {
        ScenarioStepStatus.Passed => "passed",
        ScenarioStepStatus.Failed => "failed",
        _ => step.Disposition == ScenarioStepDisposition.SkippedByTarget
            ? "skipped (target)"
            : "skipped (level)",
    };

    private static string Duration(TimeSpan duration)
        => duration == TimeSpan.Zero
            ? "-"
            : $"{duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s";

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
