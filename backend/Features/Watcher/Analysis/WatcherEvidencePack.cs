using System.Globalization;
using System.Text;

namespace AgentStudio.Watcher;

/// <summary>
/// The bounded, immutable evidence pack of dossier section 3. It records absent
/// evidence explicitly instead of substituting a guess, and it is size-capped so
/// a noisy case cannot push an unbounded prompt into a model call.
/// </summary>
public sealed record WatcherEvidencePack
{
    public required string CaseId { get; init; }
    public required string Fingerprint { get; init; }
    public required string DetectorClass { get; init; }
    public required string DetectorRule { get; init; }
    public required string Title { get; init; }
    public string? Project { get; init; }
    public required DateTime FirstSeenAtUtc { get; init; }
    public required DateTime LastSeenAtUtc { get; init; }
    public int Occurrences { get; init; }
    public int SweepCount { get; init; }
    public IReadOnlyList<string> AffectedCards { get; init; } = [];
    public IReadOnlyList<WatcherEvidenceItem> Items { get; init; } = [];
    /// <summary>Items dropped to stay inside the character budget, named rather than hidden.</summary>
    public int DroppedItems { get; init; }
    public required string Digest { get; init; }

    public bool Truncated => DroppedItems > 0;

    /// <summary>Rough token estimate used for contingent admission before a call.</summary>
    public long EstimatedTokens => Math.Max(1, ToMarkdown().Length / 4);

    /// <summary>The pack as the markdown block that travels into a card and into a prompt.</summary>
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.Append("- Case: `").Append(CaseId).AppendLine("`");
        builder.Append("- Detector class: ").AppendLine(DetectorClass);
        builder.Append("- Detector rule: ").AppendLine(DetectorRule);
        builder.Append("- Fingerprint: `").Append(Fingerprint).AppendLine("`");
        builder.Append("- Project: ").AppendLine(Project ?? "workspace (no single project)");
        builder.Append("- First seen: ").AppendLine(Stamp(FirstSeenAtUtc));
        builder.Append("- Last seen: ").AppendLine(Stamp(LastSeenAtUtc));
        builder.Append("- Occurrences: ").AppendLine(Occurrences.ToString(CultureInfo.InvariantCulture));
        builder.Append("- Confirmed over sweeps: ").AppendLine(SweepCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("- Affected cards: ")
               .AppendLine(AffectedCards.Count == 0 ? "none recorded" : string.Join(", ", AffectedCards));
        builder.AppendLine();
        builder.AppendLine("| Fact | Value | Source |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var item in Items)
        {
            builder.Append("| ").Append(Cell(item.Label))
                   .Append(" | ").Append(item.Available ? Cell(item.Value) : "_not available_")
                   .Append(" | ").Append(Cell(item.Source))
                   .AppendLine(" |");
        }
        if (Truncated)
        {
            builder.AppendLine();
            builder.Append("_Pack bounded: ").Append(DroppedItems.ToString(CultureInfo.InvariantCulture))
                   .AppendLine(" further evidence items were omitted to stay inside the pack limit._");
        }
        return builder.ToString();
    }

    private static string Stamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>Deterministic collector that turns a durable case into a bounded pack.</summary>
public static class WatcherEvidencePackBuilder
{
    /// <summary>Default character budget for the evidence table.</summary>
    public const int DefaultCharacterBudget = 8_000;

    public static WatcherEvidencePack Build(WatcherCase item, int characterBudget = DefaultCharacterBudget)
    {
        ArgumentNullException.ThrowIfNull(item);
        var budget = Math.Clamp(characterBudget, 500, 200_000);

        var kept = new List<WatcherEvidenceItem>();
        var used = 0;
        var dropped = 0;
        foreach (var evidence in item.Evidence)
        {
            var size = evidence.Label.Length + evidence.Value.Length + evidence.Source.Length;
            if (used + size > budget)
            {
                dropped++;
                continue;
            }
            used += size;
            kept.Add(evidence);
        }

        return new WatcherEvidencePack
        {
            CaseId = item.Id,
            Fingerprint = item.Fingerprint,
            DetectorClass = item.DetectorClass,
            DetectorRule = item.DetectorRule,
            Title = item.Title,
            Project = item.Project,
            FirstSeenAtUtc = item.FirstSeenAtUtc,
            LastSeenAtUtc = item.LastSeenAtUtc,
            Occurrences = item.Occurrences,
            SweepCount = item.SweepCount,
            AffectedCards = item.AffectedCards,
            Items = kept,
            DroppedItems = dropped,
            Digest = item.EvidenceDigest,
        };
    }
}
