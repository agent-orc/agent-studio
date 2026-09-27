using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

// The ReviewAttempt owns the verdict. This file owns only unfinished projections.
public sealed record RemoteReviewSettlementEntry
{
    public int Version { get; init; } = 1;
    public required string AttemptId { get; init; }
    public required string TaskKey { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string ReportSha256 { get; init; }
    public required Contract.ReviewReportRequest Report { get; init; }
    public RemoteDeliverySettlementRecord? Delivery { get; init; }
    public string? DeliverySha256 { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
    public bool EvidenceComplete { get; init; }
    public int EvidenceFailures { get; init; }
    public DateTime NextEvidenceAttemptUtc { get; init; }
    public string? RepairReason { get; init; }
}

public enum RemoteReviewSettlementReadStatus { Missing, Ready, Repair }

public sealed record RemoteReviewSettlementRead(
    RemoteReviewSettlementReadStatus Status,
    RemoteReviewSettlementEntry? Entry = null,
    string? Reason = null);

public static class RemoteReviewSettlementJournal
{
    public const int MaxEvidenceFailures = 5;
    private const string Prefix = "remote-review-settlement-";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    public static string PathFor(string folder, string attemptId)
    {
        if (string.IsNullOrWhiteSpace(attemptId) || attemptId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Invalid review attempt id.", nameof(attemptId));
        return Path.Combine(TaskPaths.LogsDir(folder), Prefix + attemptId + ".json");
    }

    public static string Hash(Contract.ReviewReportRequest report)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, HashJson))))
            .ToLowerInvariant();

    private static string? HashDelivery(RemoteDeliverySettlementRecord? delivery)
        => delivery is null ? null : Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(delivery, HashJson)))).ToLowerInvariant();

    public static RemoteReviewSettlementRead Read(string folder, string attemptId)
    {
        var path = PathFor(folder, attemptId);
        if (!File.Exists(path)) return new(RemoteReviewSettlementReadStatus.Missing);
        try
        {
            var entry = JsonSerializer.Deserialize<RemoteReviewSettlementEntry>(File.ReadAllText(path), Json);
            if (entry is null || entry.Version != 1 || entry.AttemptId != attemptId
                || string.IsNullOrWhiteSpace(entry.TaskKey) || string.IsNullOrWhiteSpace(entry.IdempotencyKey)
                || entry.Report is null || entry.Report.IdempotencyKey != entry.IdempotencyKey
                || entry.Delivery is { } delivery
                   && (delivery.ReviewAttemptId != attemptId || delivery.TaskKey != entry.TaskKey
                       || delivery.Outcome != entry.Report.Outcome
                       || !Enum.IsDefined(delivery.Stage))
                || !string.Equals(HashDelivery(entry.Delivery), entry.DeliverySha256, StringComparison.Ordinal)
                || !string.Equals(Hash(entry.Report), entry.ReportSha256, StringComparison.Ordinal))
                return new(RemoteReviewSettlementReadStatus.Repair, Reason: "corrupt-review-settlement-journal");
            return new(RemoteReviewSettlementReadStatus.Ready, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(RemoteReviewSettlementReadStatus.Repair, Reason: "unreadable-review-settlement-journal");
        }
    }

    public static bool Prepare(string folder, RemoteReviewSettlementEntry entry)
    {
        lock (Gate)
        {
            var existing = Read(folder, entry.AttemptId);
            if (existing.Status == RemoteReviewSettlementReadStatus.Repair) return false;
            if (existing.Entry is { } prior)
                return prior.IdempotencyKey == entry.IdempotencyKey && prior.ReportSha256 == entry.ReportSha256;
            Write(folder, entry with { DeliverySha256 = HashDelivery(entry.Delivery) });
            return true;
        }
    }

    public static void Write(string folder, RemoteReviewSettlementEntry entry)
    {
        var path = PathFor(folder, entry.AttemptId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class RemoteReviewSettlementPolicy
{
    public static bool MatchesAcceptedReview(RemoteReviewSettlementEntry entry, ReviewAttemptDto? review)
        => review is not null
           && entry.AttemptId == review.AttemptId
           && entry.TaskKey == review.TaskKey
           && review.Reports.Any(report => report.AuthorityStatus == AttemptWriteStatus.Accepted
               && report.IdempotencyKey == entry.IdempotencyKey)
           && string.Equals(entry.Report.Workspace.ExpectedResultSha,
               review.Subject.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.Report.Workspace.ActualHead,
               review.TestedResultSha, StringComparison.OrdinalIgnoreCase);
}
