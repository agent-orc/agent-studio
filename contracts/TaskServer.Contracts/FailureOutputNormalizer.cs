using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Shared output identity of a failed command without parsed test names
/// (AGT-2916). The review executor and the build/test gate both fingerprint a
/// build, lint or preparation failure with it, so two reproductions of the same
/// failure yield one identity: only diagnostic lines are kept, and timings,
/// durations, GUIDs and ports are removed. Each caller first collapses its own
/// attempt-local workspace and cache paths with <see cref="ReplaceRoots"/>.
/// </summary>
public static partial class FailureOutputNormalizer
{
    /// <summary>
    /// The diagnostic lines of <paramref name="output"/> without volatile
    /// values, or the exit status when the output carries no diagnostic line.
    /// </summary>
    public static string Identity(string output, int? exitCode)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => DiagnosticLine().IsMatch(line))
            .Select(line => Whitespace().Replace(StripVolatile(line), " ").Trim().ToLowerInvariant())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return lines.Length > 0 ? string.Join("\n", lines) : $"exit:{exitCode?.ToString() ?? "none"}";
    }

    /// <summary>
    /// Replaces every occurrence of each root (and, when
    /// <see cref="PathRoot.WithRunSegment"/> is set, the one per-run directory
    /// below it) by its placeholder. Longer roots are replaced first so a
    /// nested root is not swallowed by its parent.
    /// </summary>
    public static string ReplaceRoots(string output, IEnumerable<PathRoot> roots)
    {
        foreach (var root in roots
                     .Where(root => !string.IsNullOrWhiteSpace(root.Path))
                     .OrderByDescending(root => root.Path.TrimEnd('/', '\\').Length))
        {
            var pattern = Regex.Escape(root.Path.TrimEnd('/', '\\'))
                          + (root.WithRunSegment ? @"[/\\][^/\\\s""'\[\]()]+" : string.Empty)
                          + @"(?![\w-])";
            output = Regex.Replace(output, pattern, root.Placeholder.Replace("$", "$$", StringComparison.Ordinal),
                RegexOptions.IgnoreCase);
        }
        return output;
    }

    /// <summary>Removes timestamps, clock times, durations, GUIDs and ports from one line.</summary>
    public static string StripVolatile(string line)
    {
        line = IsoTimestamp().Replace(line, "<time>");
        line = ClockTime().Replace(line, "<time>");
        line = Guid().Replace(line, "<id>");
        line = Port().Replace(line, "${host}:<port>");
        line = PortWord().Replace(line, "port <port>");
        return Duration().Replace(line, "<duration>");
    }

    public sealed record PathRoot(string Path, string Placeholder, bool WithRunSegment = false);

    [GeneratedRegex(@"\b(?:error|errors|fatal|failed|failure|exception)\b|npm err!", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticLine();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?")]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?\b")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\s?(?:ms|s|sec|secs|seconds|m|min|mins|minutes)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    [GeneratedRegex(@"\b[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();

    [GeneratedRegex(@"(?<host>\blocalhost|\b\d{1,3}(?:\.\d{1,3}){3}|\[[0-9a-f:]*\]):\d{1,5}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Port();

    [GeneratedRegex(@"\bport\s*[:=]?\s*\d{1,5}\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
