using System.Globalization;
using System.Text;

namespace AgentStudio.Git;

/// <summary>One path of a commit's first-parent diff: name-status and numstat.</summary>
public sealed record GitCommitFileFact(string Status, string Path, string? OldPath, int Added, int Removed, bool Binary);

/// <summary>
/// The immutable facts of one commit (<c>git_commit_facts</c>, Git facts S3):
/// parents, tree, author time, subject, trailers and the per-path name-status
/// and numstat against the first parent. Content addressed by SHA, so a row
/// never changes once written.
/// </summary>
public sealed record GitCommitFact(
    string Sha,
    string ShortSha,
    IReadOnlyList<string> Parents,
    string Tree,
    DateTime AuthorDateUtc,
    string Author,
    string Subject,
    IReadOnlyDictionary<string, string> Trailers,
    IReadOnlyList<GitCommitFileFact> Files)
{
    public int Added => Files.Sum(file => file.Added);
    public int Removed => Files.Sum(file => file.Removed);

    /// <summary>The row the commit lists render, equal to what <c>log --shortstat</c> produced.</summary>
    public GitCommitInfo ToCommitInfo() => new(Sha, ShortSha, AuthorDateUtc, Author, Subject, Files.Count, Added, Removed);

    /// <summary>The file list of the commit views, ordered like <c>GetCommitFiles</c>.</summary>
    public List<GitFileChange> ToFileChanges() => Files
        .Select(file => new GitFileChange(file.Status, file.Path, file.Added, file.Removed))
        .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// Parses the batched commit read of the fact producer: one
/// <c>git log --no-walk=unsorted --stdin --root -M --diff-merges=first-parent
/// --raw --numstat -z</c> over many SHAs with <see cref="Format"/>. Pure, so
/// the format contract is pinned without a repository.
/// </summary>
internal static class GitCommitFactParser
{
    /// <summary>Starts every commit record; never part of a commit header, path or stat.</summary>
    internal const string RecordMarker = "\u001egit-facts-commit:";

    internal const string Format =
        "%x1egit-facts-commit:%H%x1f%h%x1f%P%x1f%T%x1f%at%x1f%aN%x1f%s%x1f%(trailers:unfold,only)%x1f";

    private const int HeaderFields = 8;

    internal static IReadOnlyList<GitCommitFact> Parse(string output)
    {
        var facts = new List<GitCommitFact>();
        if (string.IsNullOrEmpty(output)) return facts;
        foreach (var record in output.Split(RecordMarker, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = new string[HeaderFields];
            var start = 0;
            var complete = true;
            for (var index = 0; index < HeaderFields; index++)
            {
                var end = record.IndexOf('\u001f', start);
                if (end < 0) { complete = false; break; }
                fields[index] = record[start..end];
                start = end + 1;
            }
            if (!complete || fields[0].Length < 7) continue;
            if (!long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) continue;
            facts.Add(new GitCommitFact(
                fields[0],
                fields[1],
                fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                fields[3],
                DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
                fields[5],
                fields[6],
                ParseTrailers(fields[7]),
                ParseDiff(record[start..])));
        }
        return facts;
    }

    internal static IReadOnlyDictionary<string, string> ParseTrailers(string text)
    {
        var trailers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            trailers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return trailers;
    }

    /// <summary>
    /// The <c>-z</c> raw and numstat stream of one commit: raw entries
    /// (<c>:modes shas STATUS</c>, then one path or two for a rename or copy),
    /// then numstat entries (<c>added\tremoved\tpath</c>, or an empty path
    /// followed by the old and new path).
    /// </summary>
    internal static IReadOnlyList<GitCommitFileFact> ParseDiff(string diff)
    {
        var tokens = diff.Split('\0')
            .Select(token => token.TrimStart('\n', '\r'))
            .Where(token => token.Length > 0)
            .ToList();
        var entries = new List<(string Status, string Path, string? OldPath)>();
        var index = 0;
        while (index < tokens.Count && tokens[index].StartsWith(':'))
        {
            var status = tokens[index][(tokens[index].LastIndexOf(' ') + 1)..];
            index++;
            if (status.Length > 0 && status[0] is 'R' or 'C')
            {
                if (index + 1 >= tokens.Count) break;
                entries.Add((status, tokens[index + 1], tokens[index]));
                index += 2;
            }
            else
            {
                if (index >= tokens.Count) break;
                entries.Add((status, tokens[index], null));
                index++;
            }
        }

        var numstat = new Dictionary<string, (int Added, int Removed, bool Binary)>(StringComparer.Ordinal);
        while (index < tokens.Count)
        {
            var parts = tokens[index].Split('\t', 3);
            index++;
            if (parts.Length < 3) continue;
            var path = parts[2];
            if (path.Length == 0)
            {
                if (index + 1 >= tokens.Count) break;
                path = tokens[index + 1];
                index += 2;
            }
            var binary = parts[0] == "-" || parts[1] == "-";
            numstat[path] = (Count(parts[0]), Count(parts[1]), binary);
        }

        return entries.Select(entry =>
        {
            numstat.TryGetValue(entry.Path, out var stat);
            return new GitCommitFileFact(entry.Status, entry.Path, entry.OldPath, stat.Added, stat.Removed, stat.Binary);
        }).ToList();
    }

    private static int Count(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
}

/// <summary>One file's bounded patch inside a diff object.</summary>
internal sealed record GitPatchFile(string Path, string Patch, bool Truncated);

/// <summary>
/// Splits a unified patch into one section per file, keyed by the new path,
/// and bounds each section (docs/studio-route-ownership D1): at most
/// <see cref="MaxFileBytes"/> or <see cref="MaxFileLines"/> per file, beyond
/// which the file is marked truncated, never cut silently. Pure.
/// </summary>
internal static class GitPatchSplitter
{
    internal const int MaxFileBytes = 256 * 1024;
    internal const int MaxFileLines = 5_000;
    internal const string TruncatedNotice = "\\ Agent Studio: patch truncated at the per-file limit\n";

    internal static IReadOnlyList<GitPatchFile> Split(string patch)
    {
        var files = new List<GitPatchFile>();
        if (string.IsNullOrEmpty(patch)) return files;
        var text = patch.Replace("\r\n", "\n");
        var start = text.StartsWith("diff --git ", StringComparison.Ordinal) ? 0 : text.IndexOf("\ndiff --git ", StringComparison.Ordinal);
        if (start < 0) return files;
        if (start > 0) start++;
        while (start < text.Length)
        {
            var next = text.IndexOf("\ndiff --git ", start, StringComparison.Ordinal);
            var end = next < 0 ? text.Length : next + 1;
            var section = text[start..end];
            var path = SectionPath(section);
            if (path is not null) files.Add(Bound(path, section));
            start = end;
        }
        return files;
    }

    private static GitPatchFile Bound(string path, string section)
    {
        if (!section.EndsWith('\n')) section += "\n";
        var lines = 0;
        var bytes = 0;
        var lineStart = 0;
        var cut = -1;
        for (var index = 0; index < section.Length; index++)
        {
            if (section[index] != '\n') continue;
            bytes += Encoding.UTF8.GetByteCount(section.AsSpan(lineStart, index + 1 - lineStart));
            lines++;
            if (bytes > MaxFileBytes)
            {
                cut = Math.Max(lineStart, 1);
                break;
            }
            if (lines >= MaxFileLines)
            {
                cut = index + 1;
                break;
            }
            lineStart = index + 1;
        }
        return cut < 0 || cut >= section.Length
            ? new GitPatchFile(path, section, false)
            : new GitPatchFile(path, section[..cut] + TruncatedNotice, true);
    }

    /// <summary>The new path of one section: rename/copy target, <c>+++ b/</c>, <c>--- a/</c>, or the header.</summary>
    internal static string? SectionPath(string section)
    {
        string? plus = null;
        string? minus = null;
        foreach (var line in section.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith("Binary files ", StringComparison.Ordinal)
                || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                break;
            if (line.StartsWith("rename to ", StringComparison.Ordinal)) return Unquote(line["rename to ".Length..]);
            if (line.StartsWith("copy to ", StringComparison.Ordinal)) return Unquote(line["copy to ".Length..]);
            if (line.StartsWith("+++ ", StringComparison.Ordinal)) plus = line[4..];
            else if (line.StartsWith("--- ", StringComparison.Ordinal)) minus = line[4..];
        }
        if (plus is not null && plus != "/dev/null") return StripPrefix(Unquote(plus), "b/");
        if (minus is not null && minus != "/dev/null") return StripPrefix(Unquote(minus), "a/");
        return HeaderPath(section[..Math.Max(0, section.IndexOf('\n') < 0 ? section.Length : section.IndexOf('\n'))]);
    }

    /// <summary>The new path from <c>diff --git a/X b/Y</c> (quoted or with a path that contains spaces).</summary>
    internal static string? HeaderPath(string header)
    {
        var rest = header.StartsWith("diff --git ", StringComparison.Ordinal) ? header["diff --git ".Length..] : header;
        if (rest.EndsWith('"'))
        {
            var open = rest.LastIndexOf(" \"", StringComparison.Ordinal);
            if (open < 0) return null;
            return StripPrefix(Unquote(rest[(open + 1)..]), "b/");
        }
        // Unquoted, a and b paths are equal for everything but a rename, which
        // the rename lines above already answered: "a/P b/P".
        if (rest.Length >= 5 && (rest.Length - 5) % 2 == 0)
        {
            var length = (rest.Length - 5) / 2;
            var candidate = rest[(rest.Length - length)..];
            if (rest.StartsWith("a/", StringComparison.Ordinal)
                && rest.AsSpan(2, length).SequenceEqual(candidate)) return candidate;
        }
        var b = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        return b < 0 ? null : rest[(b + 3)..];
    }

    private static string StripPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    /// <summary>Reverses Git's C-style path quoting (<c>"a\tb\303\244"</c>).</summary>
    internal static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var bytes = new List<byte>(value.Length);
        for (var index = 1; index < value.Length - 1; index++)
        {
            var c = value[index];
            if (c != '\\' || index + 1 >= value.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            var escaped = value[++index];
            switch (escaped)
            {
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 't': bytes.Add(9); break;
                case 'n': bytes.Add(10); break;
                case 'v': bytes.Add(11); break;
                case 'f': bytes.Add(12); break;
                case 'r': bytes.Add(13); break;
                case >= '0' and <= '7' when index + 2 < value.Length - 1:
                    bytes.Add((byte)Convert.ToInt32(value.Substring(index, 3), 8));
                    index += 2;
                    break;
                default: bytes.AddRange(Encoding.UTF8.GetBytes(escaped.ToString())); break;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
