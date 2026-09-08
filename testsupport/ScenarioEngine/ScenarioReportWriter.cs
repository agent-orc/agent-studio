using System.Globalization;
using System.Security;
using System.Text;

namespace AgentStudio.TestSupport.Scenario;

/// <summary>
/// Writes the two artifacts a deployment card or release gate attaches to its
/// status: a JUnit XML file (for CI to parse pass/fail) and a Markdown step
/// table (for a human to read what ran and where the evidence is).
/// </summary>
public static class ScenarioReportWriter
{
    public static string WriteJUnit(ScenarioRunResult result, string target, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"scenario-{target}-{result.Level}.junit.xml");
        var failures = result.Steps.Count(step => step.Status == ScenarioStepStatus.Failed);
        var skipped = result.Steps.Count(step => step.Status == ScenarioStepStatus.Skipped);
        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.AppendLine(
            $"<testsuite name=\"deployment-regression-scenario.{target}.{result.Level}\" " +
            $"tests=\"{result.Steps.Count}\" failures=\"{failures}\" skipped=\"{skipped}\" " +
            $"time=\"{result.TotalDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}\">");
        foreach (var step in result.Steps)
        {
            xml.AppendLine(
                $"  <testcase classname=\"deployment-regression-scenario.{target}\" " +
                $"name=\"{Escape(step.Id)}: {Escape(step.Title)}\" " +
                $"time=\"{step.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}\">");
            if (step.Status == ScenarioStepStatus.Failed)
                xml.AppendLine(
                    $"    <failure message=\"{Escape(FirstLine(step.FailureMessage))}\">{Escape(step.FailureMessage ?? string.Empty)}</failure>");
            if (step.Status == ScenarioStepStatus.Skipped)
                xml.AppendLine("    <skipped/>");
            xml.AppendLine("  </testcase>");
        }
        xml.AppendLine("</testsuite>");
        File.WriteAllText(path, xml.ToString());
        return path;
    }

    public static string WriteMarkdown(ScenarioRunResult result, string target, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"scenario-{target}-{result.Level}.md");
        var evidenceDirectoryName = $"scenario-{target}-{result.Level}-evidence";
        var evidenceDirectory = Path.Combine(outputDirectory, evidenceDirectoryName);
        Directory.CreateDirectory(evidenceDirectory);
        var markdown = new StringBuilder();
        markdown.AppendLine($"# Deployment regression scenario: {target} / {result.Level}");
        markdown.AppendLine();
        markdown.AppendLine(result.Success ? "**Result: PASSED**" : "**Result: FAILED**");
        markdown.AppendLine();
        markdown.AppendLine(
            $"Total duration: {result.TotalDuration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");
        markdown.AppendLine();
        markdown.AppendLine("| Step | Status | Duration (s) | Evidence |");
        markdown.AppendLine("|---|---|---|---|");
        foreach (var step in result.Steps)
        {
            var evidence = step.Status == ScenarioStepStatus.Failed
                ? FirstLine(step.FailureMessage)
                : step.Evidence ?? "-";
            var evidenceFileName = $"{step.Id}.txt";
            File.WriteAllText(
                Path.Combine(evidenceDirectory, evidenceFileName),
                $"step={step.Id}{Environment.NewLine}" +
                $"status={step.Status}{Environment.NewLine}" +
                $"durationSeconds={step.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}{Environment.NewLine}" +
                $"evidence={step.Evidence ?? string.Empty}{Environment.NewLine}" +
                $"failure={step.FailureMessage ?? string.Empty}{Environment.NewLine}");
            markdown.AppendLine(
                $"| {step.Id}: {step.Title} | {step.Status} | " +
                $"{step.Duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} | " +
                $"{MarkdownCell(evidence)} ([evidence]({evidenceDirectoryName}/{evidenceFileName})) |");
        }
        File.WriteAllText(path, markdown.ToString());
        return path;
    }

    private static string FirstLine(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Split('\n', 2)[0];

    private static string MarkdownCell(string text)
        => text.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Escape(string? text) => SecurityElement.Escape(text ?? string.Empty) ?? string.Empty;
}
