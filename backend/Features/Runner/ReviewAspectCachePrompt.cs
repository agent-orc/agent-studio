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

    internal const string InstructionsMarker = "<!-- remote-review-aspect-instructions-v1 -->";

    internal static string ComposeStock(string authored, AspectRunInputs inputs, string cli,
        string aspect, string? promptOverride)
        => (cli.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || cli.Equals("codex", StringComparison.OrdinalIgnoreCase))
           && aspect is "requirement-fit" or "code-quality" or "documentation-impact" or "tests-and-evidence"
           && string.IsNullOrWhiteSpace(promptOverride)
            ? Compose(authored, inputs) : authored;

    internal static string Compose(string authored, AspectRunInputs inputs)
    {
        var suffix = authored.ReplaceLineEndings("\n");
        suffix = RemoveProject(suffix);
        suffix = RemoveAcceptance(suffix);
        suffix = RemoveFencedSection(suffix, "Task body (`prompt.md`)", inputs.TaskBody);
        suffix = RemoveFencedSection(suffix, "Diff summary (task branch vs base)", inputs.DiffSummary);
        suffix = RemoveFencedSection(suffix, "results/ folder inventory", string.IsNullOrWhiteSpace(inputs.ResultsInventory)
            ? "No results/ inventory available." : inputs.ResultsInventory);

        suffix = RemoveFencedSection(suffix, "Status summary (the agent's own report)", inputs.StatusSummary);
        suffix = RemoveFencedSection(suffix, "Status summary", inputs.StatusSummary);
        if (!string.IsNullOrWhiteSpace(inputs.StatusReportNote))
            suffix = suffix.Replace(inputs.StatusReportNote.ReplaceLineEndings("\n") + "\n", string.Empty,
                StringComparison.Ordinal);
        var withoutActivity = RemoveFencedSection(suffix, "Recent log (last lines)", inputs.RecentLog);
        withoutActivity = RemoveFencedSection(withoutActivity, "Recent log", inputs.RecentLog);
        var consumesActivity = !string.Equals(suffix, withoutActivity, StringComparison.Ordinal);
        suffix = withoutActivity;

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
        AppendEvidence(prefix, "Status summary (the agent's own report)", inputs.StatusSummary);
        if (!string.IsNullOrWhiteSpace(inputs.StatusReportNote))
        {
            prefix.AppendLine();
            prefix.AppendLine(inputs.StatusReportNote);
        }
        prefix.AppendLine(MaterialMarker);
        // A second evidence group serves only the three activity consumers.
        // Documentation retains the common delivery evidence without activity.
        if (consumesActivity)
            AppendEvidence(prefix, "Recent log (last lines)", inputs.RecentLog);
        prefix.AppendLine(InstructionsMarker);
        prefix.AppendLine();
        return (prefix + suffix.TrimStart()).ReplaceLineEndings("\n");
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
        foreach (var newline in (string[])["\r\n", "\n"])
        {
            var removed = RemoveFencedSection(prompt, heading, value.ReplaceLineEndings(newline), newline);
            if (!ReferenceEquals(removed, prompt)) return removed;
        }
        return prompt;
    }

    private static string RemoveFencedSection(string prompt, string heading, string value, string newline)
    {
        var header = "## " + heading + newline + newline + "```" + newline;
        var start = prompt.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return prompt;
        var contentStart = start + header.Length;
        if (!prompt.AsSpan(contentStart).StartsWith(value.AsSpan(), StringComparison.Ordinal))
            return prompt;
        var end = contentStart + value.Length;
        var close = newline + "```" + newline + newline;
        if (!prompt.AsSpan(end).StartsWith(close.AsSpan(), StringComparison.Ordinal))
            return prompt;
        return prompt.Remove(start, end + close.Length - start);
    }
}
