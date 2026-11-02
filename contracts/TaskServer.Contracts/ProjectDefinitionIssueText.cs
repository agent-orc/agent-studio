using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The one-line failure reason of an invalid repository execution definition.
/// The gate reason, the merge-step reason, the park reason and the agent's fix
/// round all carry this text, so it names every issue with its path, code and
/// message. AOW-15 was parked with a fixed sentence and a host repair hint while
/// the reader already knew that <c>commands.prepare</c> was the fault.
/// </summary>
public static partial class ProjectDefinitionIssueText
{
    /// <summary>Start of every invalid-definition reason; the merge-gate triage matches on it.</summary>
    public const string InvalidPrefix = "Repository execution definition is invalid";

    /// <summary>Upper bound of the reason text. Issues that do not fit are counted, not listed.</summary>
    public const int MaxLength = 1200;

    private const string Ellipsis = "...";

    /// <summary>
    /// <c>Repository execution definition is invalid (.agent-studio/project.yml): commands.prepare
    /// [prepare-path-invalid] Prepare must be ...; stack (line 3) [stack-required] ...</c>
    /// Never longer than <paramref name="maxLength"/> characters and always one line.
    /// </summary>
    public static string InvalidReason(IReadOnlyList<ProjectDefinitionIssue> issues, int maxLength = MaxLength)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var head = $"{InvalidPrefix} ({ProjectPreparationPaths.Definition})";
        if (issues.Count == 0) return Bound(head + ".", maxLength);

        var text = new StringBuilder(head).Append(": ");
        var listed = 0;
        foreach (var issue in issues)
        {
            var entry = Describe(issue);
            var separator = listed == 0 ? string.Empty : "; ";
            var rest = issues.Count - listed - 1;
            var suffix = rest == 0 ? "." : $"; and {rest} more issue(s).";
            if (text.Length + separator.Length + entry.Length + suffix.Length > maxLength)
            {
                if (listed > 0) break;
                // The first issue alone is too long: shorten its message rather than list nothing.
                var room = maxLength - text.Length - suffix.Length - Ellipsis.Length;
                if (room <= 0) break;
                entry = entry[..Math.Min(entry.Length, room)].TrimEnd() + Ellipsis;
            }
            text.Append(separator).Append(entry);
            listed++;
        }
        var omitted = issues.Count - listed;
        text.Append(omitted == 0 ? "." : $"; and {omitted} more issue(s).");
        return Bound(text.ToString(), maxLength);
    }

    /// <summary><c>path (line N) [code] message</c> on one line.</summary>
    public static string Describe(ProjectDefinitionIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var line = issue.Line is { } number ? $" (line {number})" : string.Empty;
        return $"{OneLine(issue.Path)}{line} [{OneLine(issue.Code)}] {OneLine(issue.Message).TrimEnd('.')}";
    }

    /// <summary>True when <paramref name="reason"/> reports an invalid definition.</summary>
    public static bool IsInvalidReason(string? reason)
        => reason?.Contains(InvalidPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The <c>path [code]</c> pairs named in an invalid-definition reason, in
    /// their written order. Lets a reader that only holds the reason line (a
    /// gate log, a merge step) recover the issues without the structured result.
    /// </summary>
    public static IReadOnlyList<string> IssueKeys(string? reason)
    {
        if (!IsInvalidReason(reason)) return [];
        var start = reason!.IndexOf(InvalidPrefix, StringComparison.Ordinal);
        return IssueKey().Matches(reason[start..])
            .Select(match => $"{match.Groups["path"].Value} [{match.Groups["code"].Value}]")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string OneLine(string? value)
        => WhiteSpace().Replace(value ?? string.Empty, " ").Trim();

    private static string Bound(string text, int maxLength)
        => text.Length <= maxLength || maxLength <= Ellipsis.Length
            ? text
            : text[..(maxLength - Ellipsis.Length)] + Ellipsis;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"(?:: |; )(?<path>\S+?)(?: \(line \d+\))? \[(?<code>[a-z][a-z0-9-]*)\]")]
    private static partial Regex IssueKey();
}
