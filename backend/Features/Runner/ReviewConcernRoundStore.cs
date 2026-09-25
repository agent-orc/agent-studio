using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public sealed record ReviewConcernRoundLedger(
    int Used,
    int Maximum,
    string ReviewAttemptId,
    IReadOnlyList<string> AspectIds,
    DateTime RecordedAtUtc,
    int? FixRunIndex = null,
    bool StillOpen = true,
    string? ReviewedResultSha = null,
    string? ReviewedIntegrationTipSha = null,
    IReadOnlyList<ReviewVerdictDto>? PreviousVerdicts = null,
    string RoundKind = RunTriggers.ReviewConcern);

/// <summary>Single durable bound for local and remote concern-fix rounds.</summary>
public static class ReviewConcernRoundStore
{
    public const string FileName = "review-concern-round.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static ReviewConcernRoundLedger? Read(string folderPath)
    {
        var path = Path.Combine(folderPath, FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ReviewConcernRoundLedger>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public static bool TryConsume(
        string folderPath,
        int maximum,
        string reviewAttemptId,
        IEnumerable<string> aspectIds,
        string? reviewedResultSha,
        string? reviewedIntegrationTipSha,
        IReadOnlyList<ReviewVerdictDto>? previousVerdicts,
        out ReviewConcernRoundLedger ledger)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folderPath), _ => new object()))
        {
            var existing = Read(folderPath);
            var used = existing?.Used ?? 0;
            maximum = Math.Max(0, maximum);
            if (used >= maximum)
            {
                ledger = existing ?? new ReviewConcernRoundLedger(0, maximum, reviewAttemptId, [], DateTime.UtcNow);
                return false;
            }
            ledger = new ReviewConcernRoundLedger(
                used + 1,
                maximum,
                reviewAttemptId,
                aspectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                DateTime.UtcNow,
                ReviewedResultSha: reviewedResultSha,
                ReviewedIntegrationTipSha: reviewedIntegrationTipSha,
                PreviousVerdicts: previousVerdicts,
                RoundKind: RunTriggers.ReviewConcern);
            Write(folderPath, ledger);
            return true;
        }
    }

    public static ReviewConcernRoundLedger RecordFinding(
        string folderPath,
        int maximumConcernRounds,
        string reviewAttemptId,
        IEnumerable<string> aspectIds,
        string? reviewedResultSha,
        string? reviewedIntegrationTipSha,
        IReadOnlyList<ReviewVerdictDto>? previousVerdicts)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folderPath), _ => new object()))
        {
            var existing = Read(folderPath);
            var ledger = new ReviewConcernRoundLedger(
                existing?.Used ?? 0,
                Math.Max(0, existing?.Maximum ?? maximumConcernRounds),
                reviewAttemptId,
                aspectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                DateTime.UtcNow,
                ReviewedResultSha: reviewedResultSha,
                ReviewedIntegrationTipSha: reviewedIntegrationTipSha,
                PreviousVerdicts: previousVerdicts,
                RoundKind: RunTriggers.ReviewFinding);
            Write(folderPath, ledger);
            return ledger;
        }
    }

    public static bool TryConsume(
        string folderPath,
        int maximum,
        string reviewAttemptId,
        IEnumerable<string> aspectIds,
        out ReviewConcernRoundLedger ledger)
        => TryConsume(
            folderPath,
            maximum,
            reviewAttemptId,
            aspectIds,
            reviewedResultSha: null,
            reviewedIntegrationTipSha: null,
            previousVerdicts: null,
            out ledger);

    public static void MarkReviewed(string folderPath, int fixRunIndex, bool stillOpen)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folderPath), _ => new object()))
        {
            var current = Read(folderPath);
            if (current is null) return;
            var updated = current with { FixRunIndex = fixRunIndex, StillOpen = stillOpen };
            Write(folderPath, updated);
        }
    }

    public static void RollBackUnstartedRound(string folderPath, string reviewAttemptId)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(folderPath), _ => new object()))
        {
            var current = Read(folderPath);
            if (current is null
                || current.FixRunIndex is not null
                || !string.Equals(current.ReviewAttemptId, reviewAttemptId, StringComparison.Ordinal))
                return;
            var path = Path.Combine(folderPath, FileName);
            if (current.Used <= 1)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var updated = current with { Used = current.Used - 1, StillOpen = false };
            Write(folderPath, updated);
        }
    }

    private static void Write(string folderPath, ReviewConcernRoundLedger ledger)
    {
        var path = Path.Combine(folderPath, FileName);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folderPath);
        File.WriteAllText(temp, JsonSerializer.Serialize(ledger, Json));
        File.Move(temp, path, overwrite: true);
    }
}
