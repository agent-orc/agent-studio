using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.SourceFacts;

/// <summary>
/// Path history and blame of one file at one commit, read on the runner host
/// by the source facts producer (git-facts option C). Readers on the Task
/// Server and in the Studio only see the parsed rows.
/// </summary>
public static class SourcePathFacts
{
    public const int MaximumHistoryEntries = 50;
    public const int MaximumBlameRanges = 20_000;
    private const int MaximumBlameOutputLines = 400_000;
    private const char UnitSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';

    public static async Task<(IReadOnlyList<SourcePathHistoryEntry> Entries, bool Truncated)?> ReadHistoryAsync(
        string repositoryPath, string headSha, string path, CancellationToken ct)
    {
        var log = await ProcessRunner.RunAsync(
            "git",
            [
                "-c", "core.quotePath=false", "log", "--follow", "--no-color",
                $"--max-count={MaximumHistoryEntries + 1}",
                "--format=%H%x1f%aI%x1f%an <%ae>%x1f%s%x1f%B%x1e",
                headSha, "--", path,
            ],
            repositoryPath,
            ct: ct);
        if (!log.Success) return null;
        var entries = ParseHistory(log.StdOut);
        var truncated = entries.Count > MaximumHistoryEntries;
        return (truncated ? entries.Take(MaximumHistoryEntries).ToArray() : entries, truncated);
    }

    internal static IReadOnlyList<SourcePathHistoryEntry> ParseHistory(string output)
    {
        var entries = new List<SourcePathHistoryEntry>();
        foreach (var raw in output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(UnitSeparator, 5);
            if (parts.Length < 5) continue;
            var sha = parts[0].Trim();
            if (sha.Length == 0) continue;
            DateTime? at = DateTimeOffset.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed.UtcDateTime
                : null;
            entries.Add(new SourcePathHistoryEntry(sha, at, parts[2].Trim(), parts[3].Trim(), parts[4].Trim()));
        }
        return entries;
    }

    /// <summary>Returns null when the path does not exist at the head, for example a deleted file.</summary>
    public static async Task<(IReadOnlyList<SourceBlameRange> Ranges, IReadOnlyList<SourceBlameCommit> Commits)?> ReadBlameAsync(
        string repositoryPath, string headSha, string path, CancellationToken ct)
    {
        // The process runner keeps only a bounded tail of stdout; porcelain
        // output of a long file exceeds it, so the lines are collected here.
        var lines = new List<string>();
        var overflow = false;
        var blame = await ProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotePath=false", "blame", "--porcelain", headSha, "--", path],
            repositoryPath,
            onStdOut: line =>
            {
                if (lines.Count < MaximumBlameOutputLines) lines.Add(line);
                else overflow = true;
            },
            ct: ct);
        return blame.Success && !overflow ? ParseBlame(string.Join('\n', lines)) : null;
    }

    /// <summary>
    /// Parses <c>git blame --porcelain</c>. Each group header is
    /// <c>&lt;sha&gt; &lt;original&gt; &lt;final&gt; [&lt;count&gt;]</c>, commit
    /// details follow the first header of a commit, and every content line
    /// starts with a tab. Consecutive final lines of one commit become one range.
    /// </summary>
    internal static (IReadOnlyList<SourceBlameRange> Ranges, IReadOnlyList<SourceBlameCommit> Commits)? ParseBlame(string output)
    {
        var ranges = new List<SourceBlameRange>();
        var commits = new Dictionary<string, (string Author, string Mail, DateTime? At, string Summary)>(StringComparer.Ordinal);
        string? current = null;
        var finalLine = 0;
        foreach (var line in output.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t')
            {
                if (current is null) continue;
                if (ranges.Count > 0 && ranges[^1].Sha == current
                    && ranges[^1].StartLine + ranges[^1].LineCount == finalLine)
                    ranges[^1] = ranges[^1] with { LineCount = ranges[^1].LineCount + 1 };
                else if (ranges.Count < MaximumBlameRanges)
                    ranges.Add(new SourceBlameRange(finalLine, 1, current));
                else
                    return null;
                continue;
            }
            var space = line.IndexOf(' ');
            var head = space < 0 ? line : line[..space];
            if (head.Length is 40 or 64 && head.All(Uri.IsHexDigit))
            {
                var fields = line.Split(' ');
                if (fields.Length < 3 || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out finalLine))
                    return null;
                current = head;
                if (!commits.ContainsKey(current)) commits[current] = (string.Empty, string.Empty, null, string.Empty);
                continue;
            }
            if (current is null) continue;
            var value = space < 0 ? string.Empty : line[(space + 1)..];
            var known = commits[current];
            commits[current] = head switch
            {
                "author" => known with { Author = value },
                "author-mail" => known with { Mail = value },
                "author-time" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    => known with { At = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime },
                "summary" => known with { Summary = value },
                _ => known,
            };
        }
        return (ranges,
            commits.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new SourceBlameCommit(
                    pair.Key,
                    string.IsNullOrEmpty(pair.Value.Mail) ? pair.Value.Author : $"{pair.Value.Author} {pair.Value.Mail}",
                    pair.Value.At,
                    pair.Value.Summary))
                .ToArray());
    }
}
