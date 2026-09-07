using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Watcher;

/// <summary>
/// Builds the bounded, immutable evidence pack for a case (§3). Purely a
/// transform over what the case already carries - no I/O, no model call.
/// Absent evidence is recorded explicitly rather than guessed.
/// </summary>
public sealed class WatcherEvidencePackBuilder
{
    private const int MaxFieldLength = 2000;

    public WatcherEvidencePack Build(WatcherCase watcherCase)
    {
        var items = new List<WatcherEvidenceItem>
        {
            new("Detector class", watcherCase.DetectorClass, null, null),
            new("Project", watcherCase.Project, null, null),
            new("First seen", watcherCase.FirstSeenUtc.ToString("O"), watcherCase.FirstSeenUtc, null),
            new("Last seen", watcherCase.LastSeenUtc.ToString("O"), watcherCase.LastSeenUtc, null),
            new("Sweeps observed", watcherCase.SweepCount.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null),
            new("Total occurrences", watcherCase.OccurrenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null),
            new("Affected cards", watcherCase.AffectedCards.Count == 0 ? "(none attributed)" : string.Join(", ", watcherCase.AffectedCards), null, null),
            new("Latest summary", Truncate(watcherCase.LastSummary), watcherCase.LastSeenUtc, null),
        };

        foreach (var (key, value) in watcherCase.LastDetails)
            items.Add(new WatcherEvidenceItem(key, Truncate(value), null, null));

        foreach (var path in watcherCase.SourcePaths)
            items.Add(new WatcherEvidenceItem("Source path", path, null, path));

        var missing = new List<string>();
        if (watcherCase.AffectedCards.Count == 0)
            missing.Add("No affected card could be attributed to this fingerprint.");
        if (string.IsNullOrWhiteSpace(watcherCase.LastSummary))
            missing.Add("No human-readable summary was recorded by the detector.");

        var digestInput = string.Join("\n", items.Select(i => $"{i.Label}={i.Value}"));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput))).ToLowerInvariant();

        return new WatcherEvidencePack
        {
            CaseId = watcherCase.Id,
            Signals = items,
            MissingEvidence = missing,
            DigestSha256 = digest,
        };
    }

    private static string Truncate(string value) =>
        value.Length <= MaxFieldLength ? value : value[..MaxFieldLength] + "…(truncated)";
}
