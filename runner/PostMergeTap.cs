using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Node TAP points and their first diagnostic, read before truncating command output.</summary>
internal sealed partial class PostMergeTap(string output)
{
    private sealed record Point(string Name, string Result, string? Error);

    [GeneratedRegex(@"^\s*(?<result>not ok|ok)\s+\d+\s+-\s+(?<name>.+?)\s*$")]
    private static partial Regex TestPoint();
    [GeneratedRegex(@"\s+#\s*(?:SKIP|TODO)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Directive();

    private readonly Dictionary<string, Point> _points = Read(output).GroupBy(point => point.Name)
        .ToDictionary(group => group.Key, group => group.FirstOrDefault(point => point.Result == PostMergeTestResults.Fail)
            ?? group.First());

    // The command verdict accepts up to 64 names in each list.
    internal string[] FailedTests => _points.Values.Where(point => point.Result == PostMergeTestResults.Fail)
        .Take(64).Select(point => point.Name).ToArray();

    internal string ResultOf(string name) => _points.GetValueOrDefault(name)?.Result ?? PostMergeTestResults.Unknown;
    internal string? ErrorOf(string name) => _points.GetValueOrDefault(name)?.Error;

    private static IEnumerable<Point> Read(string output)
    {
        using var reader = new StringReader(output);
        Point? point = null;
        int? diagnosticIndent = null;
        int? pointIndent = null;
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
            if (field == "---" && indent > pointIndent)
            {
                diagnosticIndent = indent;
                continue;
            }
            var match = TestPoint().Match(line);
            if (!match.Success) continue;
            if (point is not null) yield return point;
            pointIndent = indent;
            var name = match.Groups["name"].Value;
            point = name.Length <= 256 && !Directive().IsMatch(name)
                ? new(name, match.Groups["result"].Value == "ok" ? PostMergeTestResults.Pass : PostMergeTestResults.Fail, null)
                : null;
        }
        if (point is not null) yield return point;
    }
}
