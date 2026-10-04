using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
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
    public ReviewRoundBudgetDecision? ReviewBudgetDecision { get; init; }
    public IReadOnlyList<Contract.ReviewVerdictDto>? ReviewBudgetOriginalVerdicts { get; init; }
    public string? ReviewBudgetSha256 { get; init; }
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
    private const string Prefix = "remote-review-settlement-";
    private const string IdempotencyConflictMessage = "idempotency-conflict";

    /// <summary>
    /// Per-attempt gates: only reports for the same attempt share a journal, so
    /// a slow settlement never stalls another review. One small object per
    /// attempt settled in this process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);
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

    private static string? HashReviewBudget(RemoteReviewSettlementEntry entry)
        => entry.ReviewBudgetDecision is null && entry.ReviewBudgetOriginalVerdicts is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new { entry.ReviewBudgetDecision, entry.ReviewBudgetOriginalVerdicts }, HashJson))))
                .ToLowerInvariant();

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
                       || !V1ReviewPlaneEndpoints.TryOutcome(entry.Report.Outcome, out var settledOutcome)
                       || delivery.Outcome != settledOutcome.ToString()
                       || !Enum.IsDefined(delivery.Stage))
                || !string.Equals(HashDelivery(entry.Delivery), entry.DeliverySha256, StringComparison.Ordinal)
                || (entry.ReviewBudgetDecision is null) != (entry.ReviewBudgetOriginalVerdicts is null)
                || !string.Equals(HashReviewBudget(entry), entry.ReviewBudgetSha256, StringComparison.Ordinal)
                || !string.Equals(Hash(entry.Report), entry.ReportSha256, StringComparison.Ordinal))
                return new(RemoteReviewSettlementReadStatus.Repair, Reason: "corrupt-review-settlement-journal");
            return new(RemoteReviewSettlementReadStatus.Ready, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(RemoteReviewSettlementReadStatus.Repair, Reason: "unreadable-review-settlement-journal");
        }
    }

    /// <summary>
    /// Journals the payload, settles the authority and releases the journal when
    /// the authority refuses it, as one step per attempt gate. A rejected or
    /// interrupted request therefore never binds the attempt to its payload:
    /// only an accepted report owns the journal. A different payload under the
    /// accepted key is refused as an idempotency conflict
    /// (<see cref="IsIdempotencyConflict"/>) without reaching the authority.
    /// </summary>
    public static AttemptWriteResult PrepareAndSettle(
        string folder,
        RemoteReviewSettlementEntry entry,
        Func<string?> acceptedIdempotencyKey,
        Func<AttemptWriteResult> settle)
    {
        lock (Gates.GetOrAdd(entry.AttemptId, _ => new object()))
        {
            var accepted = acceptedIdempotencyKey();
            // An accepted report owns the journal. A different key cannot settle
            // this attempt any more, so the authority answers it untouched.
            if (accepted is not null && accepted != entry.IdempotencyKey) return settle();
            var existing = Read(folder, entry.AttemptId);
            // A replay that raced past the endpoint's replay check: the authority
            // would answer Duplicate for the key without seeing the payload.
            if (accepted is not null && existing.Entry is { } bound
                && bound.IdempotencyKey == entry.IdempotencyKey && bound.ReportSha256 != entry.ReportSha256)
                return new AttemptWriteResult(AttemptWriteStatus.InvalidState, entry.AttemptId, IdempotencyConflictMessage);
            var owned = existing.Entry is { } prior
                        && prior.IdempotencyKey == entry.IdempotencyKey
                        && prior.ReportSha256 == entry.ReportSha256;
            if (!owned)
            {
                // Nothing was acknowledged for an unaccepted or unreadable journal:
                // it is the orphan of a rejected or interrupted request.
                if (accepted is not null) return settle();
                Write(folder, entry);
            }
            var settled = settle();
            if (settled.Status is not (AttemptWriteStatus.Accepted or AttemptWriteStatus.Duplicate))
            {
                // A journal left behind here stays an unaccepted orphan: the next
                // report replaces it and the reconciler releases it.
                try { Release(folder, entry.AttemptId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SilentCatch.Note(ex, "RemoteReviewSettlementJournal: orphan journal release is best-effort");
                }
            }
            return settled;
        }
    }

    /// <summary>True when <see cref="PrepareAndSettle"/> refused a changed payload under the accepted key.</summary>
    public static bool IsIdempotencyConflict(AttemptWriteResult result)
        => result.Status == AttemptWriteStatus.InvalidState
           && string.Equals(result.Message, IdempotencyConflictMessage, StringComparison.Ordinal);

    /// <summary>Deletes the journal of an attempt whose authority accepted no report.</summary>
    public static void Release(string folder, string attemptId)
    {
        var path = PathFor(folder, attemptId);
        if (File.Exists(path)) File.Delete(path);
    }

    public static void Write(string folder, RemoteReviewSettlementEntry entry)
    {
        var path = PathFor(folder, entry.AttemptId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                entry with
                {
                    DeliverySha256 = HashDelivery(entry.Delivery),
                    ReviewBudgetSha256 = HashReviewBudget(entry),
                }, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class RemoteReviewSettlementPolicy
{
    /// <summary>Restores the accepted round and its one linked follow-up before delivery resumes.</summary>
    public static bool RestoreReviewBudgetSideEffects(
        TaskInfo task,
        RemoteReviewSettlementEntry entry,
        TaskMutationService mutations,
        TaskScannerService scanner)
    {
        if (entry.ReviewBudgetDecision is not { } budget
            || entry.ReviewBudgetOriginalVerdicts is not { } originalVerdicts)
            return false;
        var legacy = AgentStudio.Review.ReviewProjectionReader.Read(task, [], null).Attempts
            .Where(attempt => attempt.AttemptId != entry.AttemptId).ToArray();
        var seed = ReviewRoundBudgetStore.Read(task.FolderPath, legacy);
        ReviewRoundBudgetStore.Record(task.FolderPath, seed,
            new DeliveredReviewRound(entry.AttemptId,
                originalVerdicts.Where(verdict => Contract.ReviewGradingPolicy.IsBlockingToken(verdict.Status))
                    .Select(verdict => verdict.Aspect).ToArray(),
                budget.DegradedAspects,
                SpentBy: budget.SpentBy));
        return V1ReviewPlaneEndpoints.CreateReviewBudgetFollowUpCard(
            task, entry.AttemptId, originalVerdicts, budget, mutations, scanner) is not null;
    }

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

    /// <summary>
    /// Restores the delivery sidecar from the journaled decision when an
    /// Auto Review card lost it (process death before the sidecar write).
    /// Callers verify that the entry belongs to the current accepted review.
    /// </summary>
    public static bool RestoreDeliverySidecar(TaskInfo task, RemoteReviewSettlementEntry entry)
    {
        if (entry.Delivery is not { } delivery
            || !string.Equals(task.State, TaskStates.AutoReview, StringComparison.Ordinal)
            || RemoteDeliverySettlementStore.MatchesAttempt(
                RemoteDeliverySettlementStore.Read(task.FolderPath), entry.AttemptId))
            return false;
        RemoteDeliverySettlementStore.Write(task.FolderPath, delivery);
        return true;
    }
}
