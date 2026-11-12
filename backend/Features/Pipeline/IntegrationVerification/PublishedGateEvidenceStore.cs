using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3436 - one gate that passed on a tree the integration branch published.
/// The gate belongs to the card that brought the tree, but its verdict covers
/// every delivery that tree contains, so a later no-op card can name it.
/// </summary>
/// <param name="TreeSha">The exact integration commit the gate passed on.</param>
/// <param name="GateStep">The gate step that ran: the pre-develop build gate or the pre-main test gate.</param>
/// <param name="SourceJobId">The card whose integration ran the gate.</param>
/// <param name="GateVerdict">The gate verdict; only <see cref="BuildTestGateVerdict.Ok"/> is recorded or read.</param>
/// <param name="GateReason">The verdict line the source card recorded.</param>
/// <param name="PublishedSha">The published branch tip that carried the tree when it was recorded.</param>
/// <param name="Backfilled">AGT-3747: added later from the source card's own gate receipt, not by its push.</param>
/// <param name="GateLog">For a backfilled entry, the source card's gate receipt the entry was read from.</param>
public sealed record PublishedGateEvidence(
    string Project,
    string IntegrationBranch,
    string TreeSha,
    string GateStep,
    string SourceJobId,
    string GateVerdict,
    string GateReason,
    string PublishedSha,
    DateTimeOffset RecordedAtUtc,
    bool Backfilled = false,
    string? GateLog = null,
    string? TestLevel = null)
{
    public bool Passed => string.Equals(GateVerdict, nameof(BuildTestGateVerdict.Ok), StringComparison.Ordinal);

    /// <summary>The line a card that relies on this gate records as its evidence.</summary>
    public string Describe()
        => $"Gate {GateStep} of {SourceJobId} returned {GateVerdict} on {IntegrationBranch} tree {TreeSha}"
           + (Backfilled ? $" (backfilled {RecordedAtUtc:O} from {GateLog}, test level {TestLevel ?? "unknown"})." : $" (recorded {RecordedAtUtc:O}).");
}

/// <summary>
/// Project-level record of passed gates on published integration trees,
/// beside <c>external-published-verifications.json</c>. Bounded to the newest
/// <see cref="MaxEntriesPerBranch"/> entries per branch; a missing or
/// malformed file reads as "no evidence".
/// </summary>
public static class PublishedGateEvidenceStore
{
    public const string FileName = "published-gate-evidence.json";

    /// <summary>
    /// Enough merges to cover every card still open on a busy branch; an older
    /// tree is far enough behind the tip that its cards were settled long ago.
    /// </summary>
    public const int MaxEntriesPerBranch = 200;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IReadOnlyList<PublishedGateEvidence> Read(string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder)) return [];
        var path = Path.Combine(projectFolder, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PublishedGateEvidence>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "PublishedGateEvidenceStore: unreadable record");
            return [];
        }
    }

    /// <summary>
    /// Appends a passed gate. A red gate and a tree already recorded for the
    /// branch are refused. The newest entries per branch are kept.
    /// </summary>
    public static bool Append(string projectFolder, PublishedGateEvidence record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        if (!record.Passed || !ReviewSubjectStore.IsValidResultSha(record.TreeSha)) return false;
        lock (Gate)
        {
            var records = Read(projectFolder);
            if (records.Any(existing => SameTree(existing, record))) return false;
            var kept = Bound(records.Append(record));
            var path = Path.Combine(projectFolder, FileName);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(projectFolder);
                File.WriteAllText(temporary, JsonSerializer.Serialize(kept, Json));
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    /// <summary>The newest <see cref="MaxEntriesPerBranch"/> entries of each branch, oldest first.</summary>
    internal static List<PublishedGateEvidence> Bound(IEnumerable<PublishedGateEvidence> records)
        => records
            .GroupBy(record => AgentStudio.Tasks.TaskIntegrationBranch.Name(record.IntegrationBranch, fallback: string.Empty),
                StringComparer.OrdinalIgnoreCase)
            .SelectMany(branch => branch.OrderByDescending(record => record.RecordedAtUtc).Take(MaxEntriesPerBranch))
            .OrderBy(record => record.RecordedAtUtc)
            .ToList();

    private static bool SameTree(PublishedGateEvidence left, PublishedGateEvidence right)
        => string.Equals(left.TreeSha, right.TreeSha, StringComparison.OrdinalIgnoreCase)
           && IntegrationVerificationProjection.SameBranch(left.IntegrationBranch, right.IntegrationBranch);
}
