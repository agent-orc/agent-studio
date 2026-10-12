using System.Net;
using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>
/// Byte-stable contract for the implementation log inside a living Dossier.
/// Delivery cards may append one entry between the log markers. Everything
/// outside that bounded region remains owned by the decision document.
/// </summary>
public static class DossierImplementationContract
{
    public const string SectionStartMarker = "<!-- agent-studio:implementation-section:start -->";
    public const string SectionEndMarker = "<!-- agent-studio:implementation-section:end -->";
    public const string LogStartMarker = "<!-- agent-studio:implementation-log:start -->";
    public const string LogEndMarker = "<!-- agent-studio:implementation-log:end -->";

    private static readonly Regex ListItemRegex = new(
        @"<li\b(?<attrs>[^>]*)>(?<body>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex AttributeRegex = new(
        @"(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?<quote>['""])(?<value>.*?)\k<quote>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant);

    public static DossierImplementationReview Review(
        string? before,
        string? after,
        string taskKey)
    {
        var normalizedKey = taskKey.Trim();
        if (normalizedKey.Length == 0)
            return Failed("The delivery card has no stable key.");
        if (string.IsNullOrWhiteSpace(after))
        {
            return Failed(
                "The Dossier entrypoint is missing from the delivery revision.",
                DossierImplementationFindingKind.MissingEntrypoint);
        }
        if (!TrySplit(after, out var current, out var splitError, out var splitKind))
            return Failed(splitError!, splitKind);

        ImplementationSection? baseline = null;
        var baselineHasSection = !string.IsNullOrEmpty(before)
            && TrySplit(before!, out baseline, out _, out _);
        string candidateLog;
        var idempotent = false;
        if (baselineHasSection)
        {
            if (!string.Equals(baseline!.OuterPrefix, current.OuterPrefix, StringComparison.Ordinal)
                || !string.Equals(baseline.BeforeLog, current.BeforeLog, StringComparison.Ordinal)
                || !string.Equals(baseline.AfterLog, current.AfterLog, StringComparison.Ordinal)
                || !string.Equals(baseline.OuterSuffix, current.OuterSuffix, StringComparison.Ordinal))
            {
                return Failed("The Dossier changed outside the append-only implementation log.");
            }
            if (!current.Log.StartsWith(baseline.Log, StringComparison.Ordinal))
                return Failed("Existing implementation entries were edited or reordered.");

            candidateLog = current.Log[baseline.Log.Length..];
            if (string.IsNullOrWhiteSpace(candidateLog))
            {
                candidateLog = current.Log;
                idempotent = true;
            }
        }
        else
        {
            var withoutSection = current.OuterPrefix + current.OuterSuffix;
            if (!string.IsNullOrEmpty(before)
                && !string.Equals(before, withoutSection, StringComparison.Ordinal))
            {
                return Failed("Creating the implementation section also changed existing Dossier content.");
            }
            candidateLog = current.Log;
        }

        var entries = ParseEntries(candidateLog);
        var matching = entries.Where(entry =>
            string.Equals(entry.TaskKey, normalizedKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count > 1)
            return Failed($"The implementation log contains duplicate entries for {normalizedKey}.");
        if (matching.Count == 0)
        {
            // Only a log the delivery left untouched is an absence. Appending
            // another card's entry instead of this one is a contract violation
            // a newer revision must never clear.
            return !idempotent && entries.Count > 0
                ? Failed($"The implementation log has no entry for {normalizedKey}; one delivery may append only its own implementation entry.")
                : Failed(
                    $"The implementation log has no entry for {normalizedKey}.",
                    DossierImplementationFindingKind.MissingEntry);
        }
        if (!idempotent && entries.Count != 1)
            return Failed("One delivery may append only its own implementation entry.");

        var entry = matching[0];
        if (!DateOnly.TryParseExact(entry.DeliveredAt, "yyyy-MM-dd", out _))
            return Failed($"The implementation entry for {normalizedKey} needs a YYYY-MM-DD delivery date.");
        if (string.IsNullOrWhiteSpace(entry.Slice))
            return Failed($"The implementation entry for {normalizedKey} needs a compact slice name.");
        if (entry.Delivered.Length < 8)
            return Failed($"The implementation entry for {normalizedKey} needs a compact delivered summary.");

        return new DossierImplementationReview(
            true,
            idempotent,
            entry.Slice,
            entry.DeliveredAt,
            entry.Delivered,
            Array.Empty<string>());
    }

    private static IReadOnlyList<ImplementationEntry> ParseEntries(string html)
    {
        var result = new List<ImplementationEntry>();
        foreach (Match match in ListItemRegex.Matches(html))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in AttributeRegex.Matches(match.Groups["attrs"].Value))
            {
                attrs[attribute.Groups["name"].Value] = WebUtility.HtmlDecode(
                    attribute.Groups["value"].Value).Trim();
            }
            if (!attrs.ContainsKey("data-implementation-entry")) continue;
            attrs.TryGetValue("data-task-key", out var taskKey);
            attrs.TryGetValue("data-delivered-at", out var deliveredAt);
            attrs.TryGetValue("data-slice", out var slice);
            var delivered = WhitespaceRegex.Replace(
                WebUtility.HtmlDecode(TagRegex.Replace(match.Groups["body"].Value, " ")),
                " ").Trim();
            result.Add(new ImplementationEntry(
                taskKey ?? string.Empty,
                deliveredAt ?? string.Empty,
                slice ?? string.Empty,
                delivered));
        }
        return result;
    }

    private static bool TrySplit(
        string html,
        out ImplementationSection section,
        out string? error,
        out DossierImplementationFindingKind kind)
    {
        section = default!;
        error = null;
        kind = DossierImplementationFindingKind.None;
        var sectionStart = html.IndexOf(SectionStartMarker, StringComparison.Ordinal);
        var sectionEnd = html.IndexOf(SectionEndMarker, StringComparison.Ordinal);
        if (sectionStart < 0 || sectionEnd <= sectionStart)
        {
            error = "The Dossier has no canonical implementation section markers. Expected "
                + $"{SectionStartMarker} ... {LogStartMarker} ... {LogEndMarker} ... {SectionEndMarker} "
                + "as one block before </main>.";
            kind = DossierImplementationFindingKind.MissingSectionMarkers;
            return false;
        }
        if (html.IndexOf(SectionStartMarker, sectionStart + SectionStartMarker.Length, StringComparison.Ordinal) >= 0
            || html.IndexOf(SectionEndMarker, sectionEnd + SectionEndMarker.Length, StringComparison.Ordinal) >= 0)
        {
            error = "The Dossier contains duplicate implementation section markers.";
            kind = DossierImplementationFindingKind.Violation;
            return false;
        }

        var logStart = html.IndexOf(LogStartMarker, sectionStart + SectionStartMarker.Length, StringComparison.Ordinal);
        var logEnd = html.IndexOf(LogEndMarker, StringComparison.Ordinal);
        if (logStart < 0 || logEnd <= logStart || logEnd >= sectionEnd)
        {
            error = "The Dossier implementation section has no canonical append log. Expected "
                + $"{LogStartMarker} ... {LogEndMarker} inside {SectionStartMarker} ... {SectionEndMarker}, "
                + "with the section placed before </main>.";
            kind = DossierImplementationFindingKind.MissingAppendLog;
            return false;
        }

        var sectionEndExclusive = sectionEnd + SectionEndMarker.Length;
        var logContentStart = logStart + LogStartMarker.Length;
        section = new ImplementationSection(
            html[..sectionStart],
            html[sectionStart..logContentStart],
            html[logContentStart..logEnd],
            html[logEnd..sectionEndExclusive],
            html[sectionEndExclusive..]);
        return true;
    }

    private static DossierImplementationReview Failed(
        string finding,
        DossierImplementationFindingKind kind = DossierImplementationFindingKind.Violation) => new(
        false,
        false,
        null,
        null,
        null,
        new[] { finding })
    {
        FindingKind = kind,
    };

    private sealed record ImplementationSection(
        string OuterPrefix,
        string BeforeLog,
        string Log,
        string AfterLog,
        string OuterSuffix);
    private sealed record ImplementationEntry(
        string TaskKey,
        string DeliveredAt,
        string Slice,
        string Delivered);
}

public sealed record DossierImplementationReview(
    bool IsComplete,
    bool Idempotent,
    string? Slice,
    string? DeliveredAt,
    string? Delivered,
    IReadOnlyList<string> Findings)
{
    public DossierImplementationFindingKind FindingKind { get; init; } = DossierImplementationFindingKind.None;

    /// <summary>
    /// True when the inspected revision simply lacks the entry (or the file,
    /// section, or log that would hold it). Only an absence may be cleared by a
    /// newer revision; every other finding is an append-only violation.
    /// </summary>
    public bool IsAbsence => FindingKind is DossierImplementationFindingKind.MissingEntrypoint
        or DossierImplementationFindingKind.MissingSectionMarkers
        or DossierImplementationFindingKind.MissingAppendLog
        or DossierImplementationFindingKind.MissingEntry;
}

/// <summary>Typed classification of the single finding a contract review returns.</summary>
public enum DossierImplementationFindingKind
{
    None,
    MissingEntrypoint,
    MissingSectionMarkers,
    MissingAppendLog,
    MissingEntry,
    Violation,
}
