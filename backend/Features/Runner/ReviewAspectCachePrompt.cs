using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Moves the evidence common to the four stock review aspects ahead of their
/// instructions. The remote host inserts its authoritative diff at the marker.
/// The marker and layout are versioned by the frozen review plan prompt.
/// </summary>
internal static class ReviewAspectCachePrompt
{
    internal const string MaterialMarker = "<!-- remote-review-shared-material-v1 -->";

    internal static string Compose(string authored, AspectRunInputs inputs)
    {
        var suffix = authored;
        suffix = RemoveProject(suffix);
        suffix = RemoveAcceptance(suffix);
        suffix = RemoveFencedSection(suffix, "Task body (`prompt.md`)", inputs.TaskBody);
        suffix = RemoveFencedSection(suffix, "Diff summary (task branch vs base)", inputs.DiffSummary);
        suffix = RemoveFencedSection(suffix, "results/ folder inventory", string.IsNullOrWhiteSpace(inputs.ResultsInventory)
            ? "No results/ inventory available." : inputs.ResultsInventory);

        var prefix = new StringBuilder();
        prefix.AppendLine("# Review context");
        prefix.AppendLine();
        prefix.AppendLine("## Project / Job");
        prefix.AppendLine();
        prefix.AppendLine($"- **Project:** `{inputs.Project}`");
        prefix.AppendLine($"- **Id:** `{inputs.JobId}`");
        prefix.AppendLine($"- **Title:** {inputs.JobTitle}");
        prefix.AppendLine();
        prefix.AppendLine(string.IsNullOrWhiteSpace(inputs.CardMode)
            ? ReviewCardMode.Describe(null) : inputs.CardMode);
        prefix.AppendLine();
        prefix.AppendLine("## Delivery acceptance scope");
        prefix.AppendLine();
        prefix.AppendLine(string.IsNullOrWhiteSpace(inputs.AcceptanceScope)
            ? RequirementAcceptanceScope.Describe(null, inputs.TaskBody) : inputs.AcceptanceScope);
        AppendEvidence(prefix, "Task body (`prompt.md`)", inputs.TaskBody);
        AppendEvidence(prefix, "Diff summary (task branch vs base)", inputs.DiffSummary);
        AppendEvidence(prefix, "results/ folder inventory", string.IsNullOrWhiteSpace(inputs.ResultsInventory)
            ? "No results/ inventory available." : inputs.ResultsInventory);
        prefix.AppendLine(MaterialMarker);
        prefix.AppendLine();
        return prefix + suffix.TrimStart();
    }

    private static void AppendEvidence(StringBuilder text, string heading, string value)
    {
        text.AppendLine();
        text.AppendLine($"## {heading}");
        text.AppendLine();
        text.AppendLine("```");
        text.AppendLine(value);
        text.AppendLine("```");
    }

    private static string RemoveProject(string prompt)
        => Regex.Replace(prompt,
            @"(?ms)^## Project / Job\r?\n.*?(?=^## )", string.Empty, RegexOptions.CultureInvariant);

    private static string RemoveAcceptance(string prompt)
        => Regex.Replace(prompt,
            @"(?ms)^## Delivery acceptance scope\r?\n.*?(?=^\*\*Scope authority\.)",
            string.Empty, RegexOptions.CultureInvariant);

    private static string RemoveFencedSection(string prompt, string heading, string value)
    {
        var header = "## " + heading + "\n\n```\n";
        var start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return prompt;
        var contentStart = start + header.Length;
        if (!prompt.AsSpan(contentStart).StartsWith(value.AsSpan(), StringComparison.Ordinal))
            return prompt;
        var end = contentStart + value.Length;
        const string close = "\n```\n\n";
        if (!prompt.AsSpan(end).StartsWith(close.AsSpan(), StringComparison.Ordinal))
            return prompt;
        return prompt.Remove(start, end + close.Length - start);
    }
}
