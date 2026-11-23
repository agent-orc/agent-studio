using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Reads the first diagnostic from a failed-test console block before output tails are cut.</summary>
public static class TestFailureMessages
{
    public static string FirstError(string output, string? test)
    {
        var lines = output.Replace("\r", "").Split('\n')
            .Select(line => Regex.Replace(line, @"\x1b\[[0-?]*[ -/]*[@-~]", ""))
            .Select(line => Regex.Replace(line, @"^\s*\[xUnit\.net [^\]]+\]\s*", "")).ToArray();
        var start = test is null ? 0 : Array.FindIndex(lines, line => line.Contains(test, StringComparison.Ordinal));
        if (start < 0) return "Not recorded in receipt.";
        for (var index = start + (test is null ? 0 : 1); index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("Failed ", StringComparison.Ordinal)) break;
            if (line.Length == 0 || line is "Error Message:" or "Stack Trace:") continue;
            if (test is not null || line.Contains("error", StringComparison.OrdinalIgnoreCase)) return line;
        }
        return "Not recorded in receipt.";
    }
}
