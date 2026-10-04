using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AgentStudio.Tasks;

/// <summary>
/// Owns the bounded acceptance-integration section in status.md. The markers
/// allow retries to replace stale reasons while preserving the task result.
/// <para>
/// AGT-2989: caller text (outcome, branch, reason) can carry anything a Git
/// failure or an operator typed, including the section markers themselves.
/// Every caller value is escaped so it cannot open or close an HTML comment,
/// and the markers are matched only as whole lines, so neither caller text
/// nor an agent-written task result above the section can make a later
/// upsert truncate the document. A writer-owned marker immediately before
/// the section identifies it even when task text around it changes. Quoted
/// marker pairs and copied section bodies without that marker remain task text.
/// Sections written before ownership markers existed are recognized by the
/// previous writer's trailing section layout during the first retry or clear.
/// </para>
/// </summary>
public static class AcceptanceIntegrationStatusDocument
{
    internal const string StartMarker = "<!-- agent-studio:acceptance-integration:start -->";
    internal const string EndMarker = "<!-- agent-studio:acceptance-integration:end -->";
    private const string Heading = "## Acceptance integration";
    private const string OwnershipPrefix = "<!-- agent-studio:acceptance-integration:owned:";
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    public static void WriteFailure(
        string folderPath,
        string outcome,
        string? detail,
        string integrationBranch,
        DateTime? recordedAtUtc = null)
    {
        var section = new StringBuilder()
            .AppendLine(StartMarker)
            .AppendLine(Heading)
            .AppendLine()
            .AppendLine($"- Outcome: `{SingleLine(outcome)}`")
            .AppendLine($"- Lane: `{TaskStates.HumanReview}`")
            .AppendLine($"- Integration branch: `{SingleLine(integrationBranch)}`")
            .AppendLine($"- Reason: {SingleLine(detail, "Integration did not complete.")}")
            .AppendLine($"- Recorded at: `{(recordedAtUtc ?? DateTime.UtcNow):O}`")
            .AppendLine(EndMarker)
            .ToString();
        Upsert(folderPath, section);
    }

    public static void WriteOperatorOverride(
        string folderPath,
        string? reason,
        DateTime? recordedAtUtc = null)
    {
        var section = new StringBuilder()
            .AppendLine(StartMarker)
            .AppendLine(Heading)
            .AppendLine()
            .AppendLine("- Outcome: `OperatorOverride`")
            .AppendLine($"- Lane: `{TaskStates.Completed}`")
            .AppendLine("- Integration: explicitly waived by the operator")
            .AppendLine($"- Reason: {SingleLine(reason, "No reason supplied.")}")
            .AppendLine($"- Recorded at: `{(recordedAtUtc ?? DateTime.UtcNow):O}`")
            .AppendLine(EndMarker)
            .ToString();
        Upsert(folderPath, section);
    }

    public static void Clear(string folderPath)
    {
        var path = Path.Combine(folderPath, "status.md");
        lock (PathLocks.GetOrAdd(path, static _ => new object()))
        {
            if (!File.Exists(path)) return;
            var original = File.ReadAllText(path);
            var updated = RemoveOwnedSection(original).TrimEnd();
            if (string.Equals(original.TrimEnd(), updated, StringComparison.Ordinal)) return;
            ReplaceAtomically(path, updated.Length == 0 ? string.Empty : updated + Environment.NewLine);
        }
    }

    private static void Upsert(string folderPath, string section)
    {
        Directory.CreateDirectory(folderPath);
        var path = Path.Combine(folderPath, "status.md");
        lock (PathLocks.GetOrAdd(path, static _ => new object()))
        {
            var original = File.Exists(path) ? File.ReadAllText(path) : "# Result\n";
            var preserved = RemoveOwnedSection(original).TrimEnd();
            var prefix = preserved.Length == 0
                ? string.Empty
                : preserved + Environment.NewLine + Environment.NewLine;
            var generation = FindOwnedSection(original)?.Generation + 1 ?? 1;
            var ownedSection = OwnershipPrefix
                + generation.ToString("D16", CultureInfo.InvariantCulture)
                + ":" + Guid.NewGuid().ToString("N") + " -->"
                + Environment.NewLine + section;
            var updated = prefix + ownedSection;
            ReplaceAtomically(path, updated.TrimEnd() + Environment.NewLine);
        }
    }

    internal static string RemoveOwnedSection(string content)
    {
        var owned = FindOwnedSection(content) ?? FindLegacySection(content);
        return owned is { } section
            ? content.Remove(section.Start, section.End - section.Start)
            : content;
    }

    private static (int Start, int End, long Generation)? FindLegacySection(string content)
    {
        // The previous writer appended this exact section after a blank line.
        // Migrate only a complete trailing section. An earlier quoted pair, a
        // pair inside task text, or a section followed by task notes is not
        // sufficient evidence of ownership without the new marker.
        if (content.Contains(OwnershipPrefix, StringComparison.Ordinal)) return null;
        var end = content.LastIndexOf(EndMarker, StringComparison.Ordinal);
        if (end < 0 || !IsWholeLine(content, end, EndMarker.Length)
            || !string.IsNullOrWhiteSpace(content[(end + EndMarker.Length)..]))
            return null;

        var start = content.LastIndexOf(StartMarker, end, StringComparison.Ordinal);
        while (start >= 0 && !IsWholeLine(content, start, StartMarker.Length))
            start = start == 0 ? -1 : content.LastIndexOf(StartMarker, start - 1, StringComparison.Ordinal);
        if (start < 0) return null;
        var prefix = content[..start];
        if (start != 0 && !prefix.EndsWith("\n\n", StringComparison.Ordinal)
            && !prefix.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            return null;

        var bodyStart = start + StartMarker.Length;
        if (bodyStart < content.Length && content[bodyStart] == '\r') bodyStart++;
        if (bodyStart >= content.Length || content[bodyStart] != '\n') return null;
        bodyStart++;
        return IsOwnedBody(content[bodyStart..end])
            ? (start, end + EndMarker.Length, 0)
            : null;
    }

    private static (int Start, int End, long Generation)? FindOwnedSection(string content)
    {
        // Retries increase the generation. An older copied block can therefore
        // remain before the current section; equal-generation copies after it
        // leave the first occurrence as the writer's original block.
        (int Start, int End, long Generation)? best = null;
        for (var owner = content.IndexOf(OwnershipPrefix, StringComparison.Ordinal);
             owner >= 0;
             owner = content.IndexOf(OwnershipPrefix, owner + OwnershipPrefix.Length, StringComparison.Ordinal))
        {
            var ownerEnd = content.IndexOf(" -->", owner + OwnershipPrefix.Length, StringComparison.Ordinal);
            if (ownerEnd < 0 || !IsWholeLine(content, owner, ownerEnd + 4 - owner))
                continue;
            var identity = content.AsSpan(owner + OwnershipPrefix.Length, ownerEnd - owner - OwnershipPrefix.Length);
            var separator = identity.IndexOf(':');
            if (separator < 0
                || !long.TryParse(identity[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
                || generation < 1
                || !Guid.TryParseExact(identity[(separator + 1)..], "N", out _))
                continue;

            var start = ownerEnd + 4;
            if (start < content.Length && content[start] == '\r') start++;
            if (start >= content.Length || content[start] != '\n') continue;
            start++;
            if (!content.AsSpan(start).StartsWith(StartMarker, StringComparison.Ordinal)
                || !IsWholeLine(content, start, StartMarker.Length))
                continue;

            var bodyStart = start + StartMarker.Length;
            if (bodyStart < content.Length && content[bodyStart] == '\r') bodyStart++;
            if (bodyStart >= content.Length || content[bodyStart] != '\n') continue;
            bodyStart++;

            var end = MarkerLine(content, EndMarker, bodyStart);
            if (end < 0 || !IsOwnedBody(content[bodyStart..end])) continue;
            if (best is null || generation > best.Value.Generation)
                best = (owner, end + EndMarker.Length, generation);
        }
        return best;
    }

    private static bool IsOwnedBody(string body)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n');
        return lines.Length == 8
            && lines[0] == Heading
            && lines[1].Length == 0
            && lines[2].StartsWith("- Outcome: `", StringComparison.Ordinal)
            && lines[3].StartsWith("- Lane: `", StringComparison.Ordinal)
            && (lines[4].StartsWith("- Integration branch: `", StringComparison.Ordinal)
                || lines[4] == "- Integration: explicitly waived by the operator")
            && lines[5].StartsWith("- Reason: ", StringComparison.Ordinal)
            && lines[6].StartsWith("- Recorded at: `", StringComparison.Ordinal)
            && lines[7].Length == 0;
    }

    private static int MarkerLine(string content, string marker, int from)
    {
        for (var index = content.IndexOf(marker, from, StringComparison.Ordinal);
             index >= 0;
             index = content.IndexOf(marker, index + 1, StringComparison.Ordinal))
            if (IsWholeLine(content, index, marker.Length)) return index;
        return -1;
    }

    private static bool IsWholeLine(string content, int index, int length)
    {
        var startsLine = index == 0 || content[index - 1] == '\n';
        var after = index + length;
        var endsLine = after == content.Length || content[after] is '\r' or '\n';
        return startsLine && endsLine;
    }

    private static void ReplaceAtomically(string path, string content)
    {
        var tempPath = path + ".acceptance-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Folds caller text onto one line and escapes HTML comment delimiters, so
    /// a value can neither start a new line nor spell a section marker.
    /// </summary>
    internal static string SingleLine(string? value, string fallback = "")
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("<!--", "&lt;!--", StringComparison.Ordinal)
            .Replace("-->", "--&gt;", StringComparison.Ordinal)
            .Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }
}
