using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Node TAP points and their first diagnostic, read before truncating command output.</summary>
internal static partial class PostMergeTap
{
    private sealed record Point(string Name, string Result, string? Error);

    [GeneratedRegex(@"^\s*(?<result>not ok|ok)\s+\d+\s+-\s+(?<name>.+?)\s*$")]
    private static partial Regex TestPoint();
    [GeneratedRegex(@"\s+#\s*(?:SKIP|TODO)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Directive();

    internal static Dictionary<string, string> FailureMessages(string output)
        => Read(output).Where(point => point.Result == PostMergeTestResults.Fail).DistinctBy(point => point.Name)
            .Take(64).ToDictionary(point => point.Name, point => point.Error ?? "Not recorded in receipt.");

    internal static PostMergeTestRun Repeat(string tip, IReadOnlyList<string> tests,
        (int ExitCode, bool TimedOut, string Output, long DurationMs) repeat)
    {
        // Lint can stop before it reaches the original failing tests. Keep those outcomes unknown.
        var outcomes = Read(repeat.Output).Where(point => tests.Contains(point.Name))
            .GroupBy(point => point.Name).ToDictionary(group => group.Key,
                group => group.Any(point => point.Result == PostMergeTestResults.Fail)
                    ? PostMergeTestResults.Fail : PostMergeTestResults.Pass);
        return new(PostMergeRunPhases.Repeat, tip, tests, repeat.ExitCode, repeat.TimedOut, repeat.Output, repeat.DurationMs)
            { ReportedResults = outcomes };
    }

    private static IEnumerable<Point> Read(string output)
    {
        using var reader = new StringReader(output);
        Point? point = null;
        int? diagnosticIndent = null;
        while (reader.ReadLine() is { } line)
        {
            var field = line.TrimStart();
            var indent = line.Length - field.Length;
            if (diagnosticIndent is { } depth)
            {
                if (indent == depth && field == "...") diagnosticIndent = null;
                if (indent == depth && point is { Error: null } && field.StartsWith("error:", StringComparison.Ordinal))
                {
                    var message = field[6..].Trim();
                    if (message is "|" or "|-" or "|+" or ">" or ">-" or ">+")
                        message = reader.ReadLine()?.Trim() ?? "";
                    else if (message.Length >= 2 && message[0] == message[^1])
                    {
                        if (message[0] == '\'') message = message[1..^1].Replace("''", "'", StringComparison.Ordinal);
                        else if (message[0] == '"') message = message[1..^1];
                    }
                    if (message.Length > 0) point = point with { Error = message[..Math.Min(message.Length, 2000)] };
                }
                continue;
            }
            if (point is not null && field == "---")
            {
                diagnosticIndent = indent;
                continue;
            }
            var match = TestPoint().Match(line);
            if (!match.Success) continue;
            if (point is not null) yield return point;
            var name = match.Groups["name"].Value;
            point = name.Length <= 256 && !Directive().IsMatch(name)
                ? new(name, match.Groups["result"].Value == "ok" ? PostMergeTestResults.Pass : PostMergeTestResults.Fail, null)
                : null;
        }
        if (point is not null) yield return point;
    }
}
