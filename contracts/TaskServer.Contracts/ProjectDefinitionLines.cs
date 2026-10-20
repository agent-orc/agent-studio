using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public static partial class ProjectDefinitionReader
{
    /// <summary>
    /// Stamps each issue with the project.yml line its path points at. Paths use
    /// the validator grammar (<c>deploy[1].healthUrl</c>, <c>lifecycles.standard[0]</c>,
    /// <c>line:12</c>); a path without its own line falls back to the nearest
    /// enclosing key, and a path for an absent section keeps no line.
    /// </summary>
    private static List<ProjectDefinitionIssue> LocateIssues(
        IReadOnlyList<YamlLine> lines, IReadOnlyList<ProjectDefinitionIssue> issues)
    {
        var index = PathIndex(lines);
        return issues.Select(issue => issue.Line is not null ? issue : issue with { Line = Locate(index, issue.Path) })
            .ToList();
    }

    private static int? Locate(IReadOnlyDictionary<string, int> index, string path)
    {
        if (path.StartsWith("line:", StringComparison.Ordinal))
            return int.TryParse(path[5..], out var number) ? number : null;
        var candidate = path;
        while (candidate.Length > 0)
        {
            if (index.TryGetValue(candidate, out var line)) return line;
            var cut = Math.Max(candidate.LastIndexOf('.'), candidate.LastIndexOf('['));
            if (cut <= 0) return null;
            candidate = candidate[..cut];
        }
        return null;
    }

    /// <summary>Maps every key and sequence item of the bounded YAML subset to its first line.</summary>
    private static Dictionary<string, int> PathIndex(IReadOnlyList<YamlLine> lines)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var frames = new List<PathFrame>();
        foreach (var line in lines)
        {
            frames.RemoveAll(frame => frame.Indent >= line.Indent);
            var parent = frames.Count == 0 ? null : frames[^1];
            if (line.Text.StartsWith("- ", StringComparison.Ordinal) || line.Text == "-")
            {
                if (parent is null) continue;
                var item = $"{parent.Path}[{parent.Items++}]";
                index.TryAdd(item, line.Number);
                frames.Add(new PathFrame(line.Indent + 1, item));
                var inline = line.Text.Length > 2 ? line.Text[2..] : string.Empty;
                if (MapEntry().IsMatch(inline))
                {
                    var child = $"{item}.{KeyValue(inline).Key}";
                    index.TryAdd(child, line.Number);
                    frames.Add(new PathFrame(line.Indent + 2, child));
                }
                continue;
            }
            var key = KeyValue(line.Text).Key;
            if (key.Length == 0) continue;
            var path = parent is null ? key : $"{parent.Path}.{key}";
            index.TryAdd(path, line.Number);
            frames.Add(new PathFrame(line.Indent, path));
        }
        return index;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*\s*:(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MapEntry();

    private sealed class PathFrame(int indent, string path)
    {
        public int Indent { get; } = indent;
        public string Path { get; } = path;
        public int Items { get; set; }
    }
}
