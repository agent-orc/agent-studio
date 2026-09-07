using System.Globalization;

namespace AgentStudio.Review;

/// <summary>
/// Derives <see cref="ReviewRoundRecord"/>s from the legacy report Markdown for
/// cards written before the record existed (AGT-2689 and its human-review
/// siblings carry seven <c>remote-review-grade-*.md</c> files and no record).
///
/// <para>
/// This is the only Markdown-to-state parser left in the review path. It is a
/// backfill, not a contract: both planes write the record directly, so a card
/// created from now on never reaches this code. Once no live card predates the
/// record, this class and its call site can be deleted without touching any
/// reader.
/// </para>
///
/// <para>
/// The build-tests reading rule is the one AGT-2714 established: a round's
/// build-tests result is derived only from the report's <c>build-tests</c>
/// verdict rows, a blocking semantic verdict never changes it, and an absent row
/// is <see cref="ReviewBuildTestsResults.NotProven"/> rather than a failure. That
/// rule now lives in <see cref="ReviewRoundProjectionPolicy"/> and reads the rows
/// this class produces.
/// </para>
/// </summary>
public static class ReviewRoundMarkdownBackfill
{
    private const string RemoteReportPattern = "remote-review-grade-*.md";
    private const string LocalGradePattern = "code-review-grade-*.md";

    /// <summary>
    /// Every review round recoverable from report Markdown in the folder, oldest
    /// first. Rounds already covered by a canonical record are the caller's
    /// concern; see <see cref="ReviewRoundReader"/>.
    /// </summary>
    public static IReadOnlyList<ReviewRoundRecord> Read(string? jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder)) return [];
        var rounds = new List<ReviewRoundRecord>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(jobFolder, RemoteReportPattern, SearchOption.TopDirectoryOnly))
                if (ReadRemote(path) is { } remote) rounds.Add(remote);
            foreach (var path in Directory.EnumerateFiles(jobFolder, LocalGradePattern, SearchOption.TopDirectoryOnly))
                if (ReadLocal(path) is { } local) rounds.Add(local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to backfill review rounds from {Folder}", jobFolder);
        }
        return ReviewRoundRecordStore.Order(rounds);
    }

    /// <summary>
    /// One remote round from its rendered report. Null when the file is not a
    /// remote review grade or carries no subject commit, which is the same guard
    /// the evidence reader applied before the record existed.
    /// </summary>
    public static ReviewRoundRecord? ReadRemote(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var frontmatter = ReadFrontmatter(text);
        if (!string.Equals(frontmatter.GetValueOrDefault("type"), "remote-review-grade", StringComparison.OrdinalIgnoreCase))
            return null;

        var commit = frontmatter.GetValueOrDefault("actualHead")
                     ?? frontmatter.GetValueOrDefault("expectedResultSha")
                     ?? "";
        if (string.IsNullOrWhiteSpace(commit)) return null;

        var verdictRows = ReadTable(text, "## Aspect verdicts")
            .Where(columns => columns.Count >= 4 && !IsTableHeader(columns))
            .Select(columns => new ReviewRoundVerdictInput(columns[0], columns[1], columns[3]))
            .ToList();
        var commands = ReadTable(text, "## Command evidence")
            .Where(columns => columns.Count >= 7 && !IsTableHeader(columns))
            .Select(columns => new ReviewRoundCommandInput(columns[2], Unfence(columns[5]), ParseExit(columns[6])))
            .Where(command => ReviewRoundBuildTestsPairing.IsBuildVerifyStep(command.StepId))
            .ToList();

        var buildRows = verdictRows
            .Where(row => ReviewRoundBuildTestsPairing.IsBuildTestsAspect(row.Aspect))
            .ToList();
        var outcome = frontmatter.GetValueOrDefault("outcome") ?? "";
        var classification = frontmatter.GetValueOrDefault("failureClassification");
        return new ReviewRoundRecord
        {
            Plane = ReviewPlanes.Remote,
            AttemptId = frontmatter.GetValueOrDefault("attemptId")
                        ?? Path.GetFileNameWithoutExtension(path),
            SubjectSha = commit,
            ReceivedAt = ParseDate(frontmatter.GetValueOrDefault("receivedAt"))
                         ?? File.GetLastWriteTimeUtc(path),
            Outcome = string.IsNullOrWhiteSpace(classification)
                ? outcome
                : $"{outcome} / {classification}",
            Grade = null,
            Summary = ReadDetail(text),
            ReportRef = Path.GetFileName(path),
            BuildTests = ReviewRoundBuildTestsPairing.Pair(buildRows, commands),
            Aspects = verdictRows
                .Where(row => !ReviewRoundBuildTestsPairing.IsBuildTestsAspect(row.Aspect))
                .Select(row => new ReviewRoundAspect
                {
                    Name = row.Aspect,
                    Verdict = ReviewVerdicts.Normalize(row.Status),
                    Summary = row.Summary.Trim(),
                })
                .ToList(),
        };
    }

    /// <summary>
    /// One local round from a rendered <c>code-review-grade-*.md</c>. The local
    /// grade pass carries no command evidence and no aspect table, so the record
    /// holds the grade, verdict and summary only.
    /// </summary>
    public static ReviewRoundRecord? ReadLocal(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var frontmatter = ReadFrontmatter(text);
        if (!string.Equals(frontmatter.GetValueOrDefault("type"), "code-review-grade", StringComparison.OrdinalIgnoreCase))
            return null;

        var fileName = Path.GetFileName(path);
        return new ReviewRoundRecord
        {
            Plane = ReviewPlanes.Local,
            // The writer stamps the run instant into the file name to the
            // millisecond, so the stem is already a unique round identity.
            AttemptId = Path.GetFileNameWithoutExtension(fileName)["code-review-grade-".Length..],
            SubjectSha = NullIfPlaceholder(frontmatter.GetValueOrDefault("commit")),
            ReceivedAt = ParseDate(frontmatter.GetValueOrDefault("runAt"))
                         ?? File.GetLastWriteTimeUtc(path),
            Outcome = frontmatter.GetValueOrDefault("verdict") ?? "",
            Grade = frontmatter.GetValueOrDefault("grade")?.Trim().ToUpperInvariant() is { Length: 1 } grade
                ? grade
                : null,
            Summary = frontmatter.GetValueOrDefault("summary")?.Trim() ?? "",
            ReportRef = fileName,
        };
    }

    private static Dictionary<string, string> ReadFrontmatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal)) return values;
        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values[line[..colon].Trim()] = Unquote(line[(colon + 1)..].Trim());
        }
        return values;
    }

    /// <summary>The remote report's one-line detail paragraph, when it has one.</summary>
    private static string ReadDetail(string text)
    {
        const string marker = "**Detail:**";
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                return trimmed[marker.Length..].Trim();
        }
        return "";
    }

    private static IEnumerable<IReadOnlyList<string>> ReadTable(string text, string heading)
    {
        var inSection = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!inSection)
            {
                inSection = line.Equals(heading, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal)) yield break;
            if (!line.StartsWith('|')) continue;
            yield return line.Trim('|').Split('|').Select(value => value.Trim()).ToList();
        }
    }

    private static bool IsTableHeader(IReadOnlyList<string> columns) =>
        columns.Count == 0
        || columns[0].Equals("Aspect", StringComparison.OrdinalIgnoreCase)
        || columns[0].Equals("Phase", StringComparison.OrdinalIgnoreCase)
        || columns.All(column => column.Length > 0 && column.All(ch => ch is '-' or ':'));

    private static int? ParseExit(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var exit)
            ? exit
            : null;

    private static string Unfence(string value) => value.Trim().Trim('`').Trim();

    private static string? NullIfPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "(HEAD)" ? null : value.Trim();

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
            : value;
}
