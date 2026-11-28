namespace AgentStudio.Review;

/// <summary>Composes a Result when the terminal and narrative evidence are already sufficient.</summary>
internal static class SummaryEvidenceResult
{
    internal static string? Compose(TaskInfo task, string? authoredStatus, string rawLog,
        TerminalRunOutcome? outcome, AgentOutcome? agentOutcome, SummaryEvidenceBundle evidence)
    {
        if (outcome is null) return null;
        if (authoredStatus is not null
            && SummaryProtocolValidation.Validate(authoredStatus) is null
            && SummaryEvidenceAssembler.ParseStatusHeader(authoredStatus)?.Result == outcome.ProtocolResult)
            return authoredStatus;

        if (outcome.ProtocolResult is "Blocked" or "NeedsInput"
            && agentOutcome is { MatchedSentinel: true, Reason: { } reason }
            && agentOutcome.Kind == (outcome.ProtocolResult == "Blocked" ? AgentOutcomeKind.Blocked : AgentOutcomeKind.NeedsInput)
            && evidence.Commits.Count == 0 && !evidence.CodeActivityDetected
            && OneLine(reason) is { } blocker)
        {
            return Render(task, outcome.ProtocolResult, "blocked",
                "The run stopped at an explicit blocker.", [blocker], blocker);
        }

        if (!TaskModes.IsReportOnly(task.Mode) || outcome.ProtocolResult != "Success") return null;
        var narrative = authoredStatus ?? SummaryPromptFit.LatestAgentMessage(rawLog.Split('\n'));
        if (narrative is null) return null;
        var items = narrative.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Select(line => OneLine(line.TrimStart(' ', '-', '*', '>')))
            .OfType<string>()
            .Take(8)
            .ToArray();
        if (items.Length == 0) return null;

        // The original artifact carries its full detail and relative links.
        return Render(task, outcome.ProtocolResult, "forensics", items[0], items,
            "See the agent's report for its scope and open findings.", authoredStatus is not null);
    }

    private static string Render(TaskInfo task, string result, string taskCase, string solution,
        IReadOnlyList<string> items, string open, bool linkToStatus = false)
    {
        var title = OneLine(task.Title) ?? "Address the task request.";
        var lines = new List<string>
        {
            "# Status", "", $"- Result: {result}", $"- Case: {taskCase}", "- Duration: Unknown", "",
            "## Overview", $"- Problem: {title}", $"- Solution: {solution}", "", "## What Was Done",
        };
        lines.AddRange(items.Select(item => "- " + item));
        if (linkToStatus) lines.Add("- [Agent-authored status](results/status.md)");
        lines.AddRange(["", "## Open Items", "- " + open]);
        return string.Join('\n', lines);
    }

    private static string? OneLine(string text)
    {
        var clean = OrchestratorPromptBoundary.Redact(
            AgentOutcomeAnalyzer.SentinelRegex.Replace(text, ""));
        var line = string.Join(' ', clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Count(char.IsLetterOrDigit) < 8 || line.Contains("<!--", StringComparison.Ordinal)) return null;
        return SummaryPromptFit.ClipHead(line, 700);
    }
}
