using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Retention;

public sealed class RetentionExcerptWriter
{
    private static readonly Regex ErrorPattern = new(
        @"error|exception|failed|exit(?:ed)?\s*(?:code|=|:)\s*[1-9]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommandPattern = new(
        @"(?:^|\])\s*(?:\$|>|command:|exec(?:ute|uting|uted)?\s*:?)\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string> WriteAsync(
        string taskRoot,
        IReadOnlyList<RetentionFile> files,
        CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        output.AppendLine("# Retention excerpt").AppendLine();
        output.AppendLine("## Source").AppendLine();
        output.AppendLine("Generated before cold archival. Paths and hashes refer to the complete originals.").AppendLine();
        output.AppendLine("## Summary").AppendLine();
        output.AppendLine($"- Files: {files.Count}");
        output.AppendLine($"- Bytes: {files.Sum(file => file.Size)}").AppendLine();
        output.AppendLine("## Head").AppendLine();

        var cliFiles = files.Where(file => file.Classification.Family == "cli-output").ToList();
        var allCliLines = new List<string>();
        foreach (var file in cliFiles)
        {
            var path = SafePath(taskRoot, file.RelativePath);
            if (File.Exists(path))
                allCliLines.AddRange(await File.ReadAllLinesAsync(path, cancellationToken));
        }
        AppendLines(output, allCliLines.Take(200));

        output.AppendLine().AppendLine("## Errors").AppendLine();
        var errorIndices = allCliLines.Select((line, index) => (line, index))
            .Where(item => ErrorPattern.IsMatch(item.line)).Select(item => item.index).ToList();
        var windows = new SortedSet<int>();
        foreach (var index in errorIndices)
            for (var line = Math.Max(0, index - 50); line <= Math.Min(allCliLines.Count - 1, index + 50); line++)
                windows.Add(line);
        AppendLines(output, windows.Select(index => allCliLines[index]));

        output.AppendLine().AppendLine("## Tail").AppendLine();
        AppendLines(output, allCliLines.Skip(Math.Max(0, allCliLines.Count - 500)));

        output.AppendLine().AppendLine("## Metrics").AppendLine();
        output.AppendLine($"- Error markers: {errorIndices.Count}");
        output.AppendLine($"- Tool calls: {Count(allCliLines, "tool")}");
        output.AppendLine($"- Test runs: {Count(allCliLines, "test")}");
        output.AppendLine($"- Commits: {Count(allCliLines, "commit")}");
        output.AppendLine($"- Token lines: {Count(allCliLines, "token")}");
        var timestamps = allCliLines.Where(line => line.Contains(':') && (line.Contains('[') || line.Contains("duration", StringComparison.OrdinalIgnoreCase))).ToList();
        output.AppendLine($"- Timestamp/duration lines: {timestamps.Count}");

        output.AppendLine().AppendLine("## Timestamps and duration").AppendLine();
        AppendLines(output, timestamps);

        output.AppendLine().AppendLine("## Commands").AppendLine();
        foreach (var command in allCliLines.Select(line => CommandPattern.Match(line)).Where(match => match.Success)
                     .Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.Ordinal))
            output.AppendLine($"- `{command.Replace("`", "'")}`");

        output.AppendLine().AppendLine("## Review verdicts and findings").AppendLine();
        foreach (var file in files.Where(file => file.Classification.Family == "review-stdout"))
        {
            var path = SafePath(taskRoot, file.RelativePath);
            if (!File.Exists(path)) continue;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
                if (line.Contains("verdict", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("finding", StringComparison.OrdinalIgnoreCase))
                    output.AppendLine($"- {line}");
        }

        output.AppendLine().AppendLine("## Inventory").AppendLine();
        output.AppendLine("| Path | Bytes | SHA-256 | Retained content |");
        output.AppendLine("|---|---:|---|---|");
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = SafePath(taskRoot, file.RelativePath);
            var hash = File.Exists(path) ? await Sha256Async(path, cancellationToken) : "missing";
            var retainFull = file.Classification.Family == "results"
                             && (file.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                 || file.RelativePath.Contains("report", StringComparison.OrdinalIgnoreCase));
            output.AppendLine($"| `{file.RelativePath.Replace("|", "\\|")}` | {file.Size} | `{hash}` | {(retainFull ? "full below" : "inventory only")} |");
            if (retainFull && File.Exists(path))
            {
                output.AppendLine().AppendLine($"### {file.RelativePath}").AppendLine();
                output.AppendLine(await File.ReadAllTextAsync(path, cancellationToken)).AppendLine();
            }
        }
        return output.ToString();
    }

    private static int Count(IEnumerable<string> lines, string token)
        => lines.Count(line => line.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static void AppendLines(StringBuilder output, IEnumerable<string> lines)
    {
        output.AppendLine("```text");
        foreach (var line in lines) output.AppendLine(line);
        output.AppendLine("```");
    }

    private static string SafePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Artifact path escapes task root: {relative}");
        return full;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
