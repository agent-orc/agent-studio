using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Runner;

/// <summary>The frozen deep-review decision for one delivery of the card.</summary>
public sealed record DeepReviewDecisionEntry(
    string ResultSha,
    DateTime DecidedAt,
    string CliType,
    DeepReviewDecision Decision);

/// <summary>One executed deep review: the delivery, its verdict, its blocking findings and its usage.</summary>
public sealed record DeepReviewRoundEntry(
    string ReviewAttemptId,
    string ResultSha,
    string Status,
    string? Summary,
    IReadOnlyList<string> BlockFingerprints,
    string? Model,
    string? ThinkingLevel,
    long InputTokens,
    long OutputTokens,
    DateTime RecordedAt);

public sealed record DeepReviewLedger(
    IReadOnlyList<DeepReviewDecisionEntry> Decisions,
    IReadOnlyList<DeepReviewRoundEntry> Rounds)
{
    public static DeepReviewLedger Empty { get; } = new([], []);

    public IReadOnlyList<DeepReviewRoundRecord> RoundRecords => Rounds
        .Select(round => new DeepReviewRoundRecord(round.ReviewAttemptId, round.ResultSha, round.Status,
            round.BlockFingerprints))
        .ToArray();
}

/// <summary>
/// Card-local deep-review ledger (<c>deep-review-ledger.json</c>). It travels
/// with the card folder across lane moves. The first decision for a result
/// SHA wins, so a rebuilt review plan reuses the frozen receipt, and a round
/// is recorded once per review attempt.
/// </summary>
public static class DeepReviewLedgerStore
{
    public const string FileName = "deep-review-ledger.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static DeepReviewLedger Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return DeepReviewLedger.Empty;
        return JsonSerializer.Deserialize<DeepReviewLedger>(File.ReadAllText(path), Json)
               ?? throw new InvalidDataException($"Invalid deep-review ledger at {path}.");
    }

    /// <summary>Stores <paramref name="entry"/> unless its delivery already has a decision; returns the frozen one.</summary>
    public static DeepReviewDecisionEntry Freeze(string folder, DeepReviewDecisionEntry entry, out bool created)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folder), _ => new object()))
        {
            var current = Read(folder);
            var existing = current.Decisions.FirstOrDefault(item =>
                string.Equals(item.ResultSha, entry.ResultSha, StringComparison.OrdinalIgnoreCase));
            created = existing is null;
            if (existing is not null) return existing;
            Write(folder, current with { Decisions = current.Decisions.Append(entry).ToArray() });
            return entry;
        }
    }

    public static DeepReviewLedger RecordRound(string folder, DeepReviewRoundEntry round)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folder), _ => new object()))
        {
            var current = Read(folder);
            if (current.Rounds.Any(item => item.ReviewAttemptId == round.ReviewAttemptId)) return current;
            var updated = current with { Rounds = current.Rounds.Append(round).ToArray() };
            Write(folder, updated);
            return updated;
        }
    }

    private static void Write(string folder, DeepReviewLedger ledger)
    {
        // A lane move renames the card folder; a stale path must fail rather
        // than recreate an empty card folder in the lane the card left.
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Card folder {folder} no longer exists.");
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(ledger, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
