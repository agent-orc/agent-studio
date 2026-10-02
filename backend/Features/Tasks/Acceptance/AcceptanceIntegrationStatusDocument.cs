using System.Collections.Concurrent;
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
/// upsert truncate the document. Removal requires the owned heading and a
/// trailing end marker; a quoted marker pair is preserved when ownership
/// cannot be established.
/// </para>
/// </summary>
public static class AcceptanceIntegrationStatusDocument
{
    internal const string StartMarker = "<!-- agent-studio:acceptance-integration:start -->";
    internal const string EndMarker = "<!-- agent-studio:acceptance-integration:end -->";
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
            .AppendLine("## Acceptance integration")
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
            .AppendLine("## Acceptance integration")
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
            var updated = preserved.Length == 0
                ? section
                : preserved + Environment.NewLine + Environment.NewLine + section;
            ReplaceAtomically(path, updated.TrimEnd() + Environment.NewLine);
        }
    }

    internal static string RemoveOwnedSection(string content)
    {
        // Only the trailing section with our heading can be removed. A complete
        // quoted pair in task text is not evidence that we own that text.
        var start = LastMarkerLine(content, StartMarker);
        if (start < 0) return content;
        var heading = start + StartMarker.Length;
        if (heading < content.Length && content[heading] == '\r') heading++;
        if (heading >= content.Length || content[heading] != '\n') return content;
        heading++;
        const string ownedHeading = "## Acceptance integration";
        if (!content.AsSpan(heading).StartsWith(ownedHeading, StringComparison.Ordinal)
            || !IsWholeLine(content, heading, ownedHeading.Length)) return content;

        var end = MarkerLine(content, EndMarker, start + StartMarker.Length);
        if (end < 0) return content;
        for (var index = end + EndMarker.Length; index < content.Length; index++)
            if (!char.IsWhiteSpace(content[index])) return content;
        return content.Remove(start, end + EndMarker.Length - start);
    }

    private static int MarkerLine(string content, string marker, int from)
    {
        for (var index = content.IndexOf(marker, from, StringComparison.Ordinal);
             index >= 0;
             index = content.IndexOf(marker, index + 1, StringComparison.Ordinal))
            if (IsWholeLine(content, index, marker.Length)) return index;
        return -1;
    }

    private static int LastMarkerLine(string content, string marker)
    {
        for (var index = content.LastIndexOf(marker, StringComparison.Ordinal);
             index >= 0;
             index = index == 0 ? -1 : content.LastIndexOf(marker, index - 1, StringComparison.Ordinal))
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
