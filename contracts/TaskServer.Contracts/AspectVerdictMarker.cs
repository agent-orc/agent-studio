using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Parsed form of the semantic review marker shared by the monolith and the
/// remote review runner. <see cref="Detail"/> retains duplicate field values
/// without allowing them to replace the first canonical value.
/// </summary>
public sealed record AspectVerdictMarker(
    string Status,
    string Summary,
    string? EvidenceChecked,
    string? Missing,
    string? Classification,
    string Detail,
    IReadOnlyList<string> Malformed)
{
    public bool IsMalformed => Malformed.Count > 0;
}

/// <summary>
/// One parser contract for <c>[[ASPECT_VERDICT: ...]]</c>. The last marker in
/// a reply wins, wrapped and multiline markers are accepted, and duplicate
/// keys resolve first-value-wins while preserving later values as detail.
/// </summary>
public static class AspectVerdictMarkerParser
{
    public const string DuplicateKey = "duplicate-key";

    private static readonly Regex Marker = new(
        @"\[\[ASPECT_VERDICT:\s*(?<body>.+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly HashSet<string> KnownFields = new(
        ["status", "summary", "evidence_checked", "missing", "classification"],
        StringComparer.OrdinalIgnoreCase);

    public static AspectVerdictMarker? ParseLast(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var matches = Marker.Matches(StripCodeFences(output));
        if (matches.Count == 0) return null;

        var body = Regex.Replace(
            matches[^1].Groups["body"].Value,
            @"(?m)^\s*>\s?",
            string.Empty);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var detail = new List<string>();
        var malformed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPart in body.Split(';'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;
            var equals = part.IndexOf('=');
            if (equals <= 0) continue;
            var key = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim();
            if (key.Length == 0 || !KnownFields.Contains(key)) continue;
            if (!fields.TryAdd(key, value))
            {
                malformed.Add(DuplicateKey);
                detail.Add($"{key}={value}");
            }
        }

        if (!fields.TryGetValue("status", out var status)) return null;
        fields.TryGetValue("summary", out var summary);
        fields.TryGetValue("evidence_checked", out var evidenceChecked);
        fields.TryGetValue("missing", out var missing);
        fields.TryGetValue("classification", out var classification);
        return new AspectVerdictMarker(
            status.Trim(),
            summary?.Trim() ?? string.Empty,
            NullIfBlank(evidenceChecked),
            NullIfBlank(missing),
            NullIfBlank(classification),
            string.Join("; ", detail),
            malformed.Order(StringComparer.Ordinal).ToArray());
    }

    public static string ClassificationWithMalformed(AspectVerdictMarker marker, string fallback)
    {
        var classification = string.IsNullOrWhiteSpace(marker.Classification)
            ? fallback
            : marker.Classification!.Trim();
        return marker.Malformed.Count == 0
            ? classification
            : $"{classification}; malformed: {string.Join(",", marker.Malformed)}";
    }

    private static string StripCodeFences(string raw)
    {
        var value = Regex.Replace(raw, "```[a-zA-Z0-9_-]*\\r?\\n?", string.Empty);
        return Regex.Replace(value, "```", string.Empty);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
