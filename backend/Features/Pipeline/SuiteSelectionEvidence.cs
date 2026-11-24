using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Coverage text shared by gate receipts and existing card evidence surfaces.</summary>
public static class SuiteSelectionEvidence
{
    public static bool SupportsFullBaseVerdict(TestSelectionAudit? audit)
        => audit?.Selector != ProjectTestSelectionPolicy.Selector || audit.FullSuiteRan;

    public static TestSelectionAudit? ReadLatestAudit(string path)
    {
        if (!File.Exists(path)) return null;
        var line = File.ReadLines(path).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (line is null) return null;
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("TestSelection", out var value)
            ? value.Deserialize<TestSelectionAudit>() : null;
    }

    public static string Summary(TestSelectionAudit? audit)
    {
        if (audit is null || audit.SuiteCommands.Count == 0) return "suite-coverage=unknown";
        var unrun = audit.SelectedSuites.Except(audit.AttemptedSuites).ToArray();
        return $"suites-ran={Names(audit.AttemptedSuites)}; suites-selected-but-not-started={Names(unrun)}; "
            + $"suites-omitted={Names(audit.OmittedSuites)}; selection-reason={string.Join(" ", audit.Reasons)};";
    }

    private static string Names(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(",", values);
}
