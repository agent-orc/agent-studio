using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

// The ReviewAttempt owns the verdict. This file owns only unfinished projections.
public sealed record RemoteReviewSettlementEntry
{
    /// <summary>
    /// Hash format of the entry (<see cref="RemoteReviewSettlementJournal.CurrentVersion"/>).
    /// Format 1 hashes the compact JSON of each member as the writing release
    /// serialized it; format 2 hashes its canonical JSON
    /// (<see cref="RemoteReviewSettlementJournal.Hash"/>).
    /// </summary>
    public int Version { get; init; } = RemoteReviewSettlementJournal.CurrentVersion;
    public required string AttemptId { get; init; }
    public required string TaskKey { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string ReportSha256 { get; init; }
    /// <summary>Hash of the runner payload before server-side brief and coverage normalization.</summary>
    public string? SubmittedReportSha256 { get; init; }
    public required Contract.ReviewReportRequest Report { get; init; }
    public RemoteDeliverySettlementRecord? Delivery { get; init; }
    public string? DeliverySha256 { get; init; }
    public ReviewRoundBudgetDecision? ReviewBudgetDecision { get; init; }
    public IReadOnlyList<Contract.ReviewVerdictDto>? ReviewBudgetOriginalVerdicts { get; init; }
    public CardRoundBudgetState? CardBudget { get; init; }
    public string? ReviewBudgetSha256 { get; init; }
    public RemoteReviewContinuation? Continuation { get; init; }
    public string? ContinuationSha256 { get; init; }
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

    /// <summary>
    /// Format 1: every hash covers the compact serialization of its member. A
    /// release that adds a member to a hashed record (AGT-3430 added
    /// <c>outputTail</c> to each command) serializes the same entry differently,
    /// so format 1 entries verify against the JSON members as stored (AGT-3467).
    /// </summary>
    public const int LegacyVersion = 1;

    /// <summary>
    /// Format 2: every hash covers the canonical JSON of its member, in which
    /// members holding null, false or zero are omitted. A release that adds an
    /// optional member at its default therefore keeps every hash.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>Canonical report hash: the idempotency identity of a report payload.</summary>
    public static string Hash(Contract.ReviewReportRequest report)
        => HashJsonText(JsonSerializer.Serialize(report, HashJson), canonical: true);

    /// <summary>The report hash of a verified entry in the current canonical format.</summary>
    public static string ReportHash(RemoteReviewSettlementEntry entry)
        => entry.Version >= CurrentVersion ? entry.ReportSha256 : Hash(entry.Report);

    /// <summary>
    /// The submitted payload hash comparable with <see cref="Hash"/>, or null
    /// when the entry predates the canonical format: the submitted payload is
    /// not journaled, so its legacy hash cannot be recomputed.
    /// </summary>
    public static string? SubmittedReportHash(RemoteReviewSettlementEntry entry)
        => entry.Version >= CurrentVersion ? entry.SubmittedReportSha256 : null;

    private static string? HashDelivery(RemoteDeliverySettlementRecord? delivery)
        => delivery is null ? null : HashJsonText(JsonSerializer.Serialize(delivery, HashJson), canonical: true);

    private static string? HashReviewBudget(RemoteReviewSettlementEntry entry)
        => entry.ReviewBudgetDecision is null && entry.ReviewBudgetOriginalVerdicts is null
           && entry.CardBudget is null
            ? null
            : HashJsonText(JsonSerializer.Serialize(
                entry.CardBudget is null
                    ? (object)new { entry.ReviewBudgetDecision, entry.ReviewBudgetOriginalVerdicts }
                    : new { entry.ReviewBudgetDecision, entry.ReviewBudgetOriginalVerdicts, entry.CardBudget },
                HashJson), canonical: true);

    private static string? HashContinuation(RemoteReviewContinuation? continuation)
        => continuation is null ? null : HashJsonText(JsonSerializer.Serialize(continuation, HashJson), canonical: true);

    private static string HashJsonText(string json, bool canonical)
        => HashNode(JsonNode.Parse(json), canonical);

    private static string HashNode(JsonNode? node, bool canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (canonical ? Canonical(node) : node)?.ToJsonString(HashJson) ?? "null"))).ToLowerInvariant();

    /// <summary>A copy of <paramref name="node"/> without object members that hold null, false or zero.</summary>
    private static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    if (IsDefault(value)) continue;
                    result[name] = Canonical(value);
                }
                return result;
            case JsonArray array:
                return new JsonArray(array.Select(Canonical).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    private static bool IsDefault(JsonNode? value)
        => value is null
           || value is JsonValue scalar && scalar.GetValueKind() switch
           {
               JsonValueKind.False => true,
               JsonValueKind.Number => double.TryParse(scalar.ToJsonString(), NumberStyles.Float,
                   CultureInfo.InvariantCulture, out var number) && number == 0,
               _ => false,
           };

    /// <summary>
    /// Hashes a stored member with the rules of the entry's format. Both formats
    /// hash the JSON as it is stored, so the hash proves what the writer
    /// produced, whatever members the reading release adds to the record.
    /// Format 1 needs the stored token text: a parsed and re-encoded string can
    /// differ from it (the writer stores a <see cref="DateTimeOffset"/> with a
    /// literal '+', a re-encoded string escapes it).
    /// </summary>
    private static string? HashStored(string? compactMember, bool canonical)
        => compactMember is null ? null
            : canonical ? HashJsonText(compactMember, canonical: true)
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compactMember))).ToLowerInvariant();

    /// <summary>The stored member as compact JSON with its token text unchanged, or null when absent.</summary>
    private static string? StoredMember(JsonElement stored, string name)
        => stored.TryGetProperty(name, out var member) && member.ValueKind != JsonValueKind.Null
            ? Compact(member.GetRawText())
            : null;

    /// <summary>Removes the whitespace between tokens; string contents stay as written.</summary>
    private static string Compact(string json)
    {
        var result = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        foreach (var c in json)
        {
            if (inString)
            {
                result.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"')
            {
                inString = true;
                result.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    private static string? StoredReviewBudget(JsonElement stored, RemoteReviewSettlementEntry entry)
    {
        if (entry.ReviewBudgetDecision is null && entry.ReviewBudgetOriginalVerdicts is null
            && entry.CardBudget is null)
            return null;
        // The writer hashed an anonymous object with these member names.
        var budget = "{\"reviewBudgetDecision\":" + (StoredMember(stored, "reviewBudgetDecision") ?? "null")
                     + ",\"reviewBudgetOriginalVerdicts\":"
                     + (StoredMember(stored, "reviewBudgetOriginalVerdicts") ?? "null");
        if (entry.CardBudget is not null)
            budget += ",\"cardBudget\":" + (StoredMember(stored, "cardBudget") ?? "null");
        return budget + "}";
    }

    public static RemoteReviewSettlementRead Read(string folder, string attemptId)
    {
        var path = PathFor(folder, attemptId);
        if (!File.Exists(path)) return new(RemoteReviewSettlementReadStatus.Missing);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var stored = document.RootElement;
            var entry = stored.ValueKind == JsonValueKind.Object
                ? stored.Deserialize<RemoteReviewSettlementEntry>(Json)
                : null;
            var problem = entry is null ? "empty" : Problem(stored, entry, attemptId);
            if (problem is not null)
                return new(RemoteReviewSettlementReadStatus.Repair,
                    Reason: $"corrupt-review-settlement-journal:{problem}");
            return new(RemoteReviewSettlementReadStatus.Ready, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException)
        {
            return new(RemoteReviewSettlementReadStatus.Repair, Reason: "unreadable-review-settlement-journal");
        }
    }

    /// <summary>The first reason the entry cannot be trusted, or null for a verified entry.</summary>
    private static string? Problem(JsonElement stored, RemoteReviewSettlementEntry entry, string attemptId)
    {
        if (entry.Version is not (LegacyVersion or CurrentVersion))
            return $"unsupported-format-{entry.Version}";
        if (entry.AttemptId != attemptId
            || string.IsNullOrWhiteSpace(entry.TaskKey) || string.IsNullOrWhiteSpace(entry.IdempotencyKey)
            || entry.Report is null || entry.Report.IdempotencyKey != entry.IdempotencyKey)
            return "identity-mismatch";
        if (entry.SubmittedReportSha256 is { } submitted
            && (submitted.Length != 64 || !submitted.All(Uri.IsHexDigit)))
            return "submitted-report-sha256-malformed";
        if (entry.Delivery is { } delivery
            && (delivery.ReviewAttemptId != attemptId || delivery.TaskKey != entry.TaskKey
                || !V1ReviewPlaneEndpoints.TryOutcome(entry.Report.Outcome, out var settledOutcome)
                || delivery.Outcome != settledOutcome.ToString()
                || !Enum.IsDefined(delivery.Stage)))
            return "delivery-mismatch";
        if ((entry.ReviewBudgetDecision is null) != (entry.ReviewBudgetOriginalVerdicts is null)
            || entry.CardBudget is not null && entry.ReviewBudgetDecision is null)
            return "review-budget-incomplete";
        if (entry.Continuation is { } continuation
            && ((continuation.DeliveredRound is null) != (continuation.DeliveredRoundSeed is null)
                || continuation.DeliveredRound is { } deliveredRound
                   && deliveredRound.AttemptId != attemptId))
            return "continuation-mismatch";
        var canonical = entry.Version >= CurrentVersion;
        if (!string.Equals(HashStored(StoredMember(stored, "delivery"), canonical), entry.DeliverySha256,
                StringComparison.Ordinal))
            return "delivery-sha256-mismatch";
        if (!string.Equals(HashStored(StoredReviewBudget(stored, entry), canonical), entry.ReviewBudgetSha256,
                StringComparison.Ordinal))
            return "review-budget-sha256-mismatch";
        if (!string.Equals(HashStored(StoredMember(stored, "continuation"), canonical), entry.ContinuationSha256,
                StringComparison.Ordinal))
            return "continuation-sha256-mismatch";
        if (!string.Equals(HashStored(StoredMember(stored, "report"), canonical), entry.ReportSha256,
                StringComparison.Ordinal))
            return "report-sha256-mismatch";
        return null;
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
                && bound.IdempotencyKey == entry.IdempotencyKey && ReportHash(bound) != entry.ReportSha256)
                return new AttemptWriteResult(AttemptWriteStatus.InvalidState, entry.AttemptId, IdempotencyConflictMessage);
            var owned = existing.Entry is { } prior
                        && prior.IdempotencyKey == entry.IdempotencyKey
                        && ReportHash(prior) == entry.ReportSha256;
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

    /// <summary>
    /// Writes the entry in the current format. A rewritten format 1 entry (an
    /// evidence or repair update after a verified read) is upgraded: its report
    /// hash is recomputed canonically, and its legacy submitted hash is dropped
    /// because the submitted payload cannot be rehashed.
    /// </summary>
    public static void Write(string folder, RemoteReviewSettlementEntry entry)
    {
        var upgraded = entry.Version < CurrentVersion;
        var path = PathFor(folder, entry.AttemptId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                entry with
                {
                    Version = CurrentVersion,
                    ReportSha256 = upgraded ? Hash(entry.Report) : entry.ReportSha256,
                    SubmittedReportSha256 = upgraded ? null : entry.SubmittedReportSha256,
                    DeliverySha256 = HashDelivery(entry.Delivery),
                    ReviewBudgetSha256 = HashReviewBudget(entry),
                    ContinuationSha256 = HashContinuation(entry.Continuation),
                }, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>
/// The operator-visible trace of a settlement journal that reads as repair
/// (AGT-3467). Before it, completion refused such cards with "only no aspects
/// ran" and nothing named the journal.
/// </summary>
public static class RemoteReviewSettlementDiagnostics
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Appends one <see cref="TimelineEventKinds.ReviewSettlementRepairRequired"/>
    /// entry per card and review attempt. Returns true when this call wrote it.
    /// </summary>
    public static bool RecordRepairOnce(TimelineLog timeline, string folder, string attemptId, string? reason)
    {
        lock (Gates.GetOrAdd(attemptId, _ => new object()))
        {
            if (timeline.ReadAll(folder).Any(entry =>
                    entry.Kind == TimelineEventKinds.ReviewSettlementRepairRequired
                    && entry.Details?.GetValueOrDefault("reviewAttemptId") == attemptId)) return false;
            var cause = reason ?? "unknown";
            return timeline.Append(folder, TimelineEventKinds.ReviewSettlementRepairRequired,
                TimelineActors.System,
                $"The settlement journal of review {attemptId} needs repair ({cause}); "
                + "completion has no content verdict until the review is repaired or re-run.",
                runId: attemptId,
                details: new Dictionary<string, string>
                {
                    ["reviewAttemptId"] = attemptId,
                    ["reason"] = cause,
                    ["journal"] = Path.GetFileName(RemoteReviewSettlementJournal.PathFor(folder, attemptId)),
                });
        }
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
                MonolithTransitionAdapter.ReviewRoundBudgetPolicyCountedBlockingAspects(originalVerdicts),
                budget.DegradedAspects,
                SpentBy: budget.SpentBy,
                BlockingFindingKeys: MonolithTransitionAdapter.BlockingReviewFindingKeys(originalVerdicts)));
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
    /// A documentation-only pass whose build and test aspect the batch gate
    /// owns. It carries no delivery record: the batch publishes the member and
    /// releases its lane, so recovery has no per-card integration to resume.
    /// </summary>
    public static bool IsBatchDeferredPass(RemoteReviewSettlementEntry entry)
        => (string.Equals(entry.Report.Outcome, "Pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Report.Outcome, "IntegrationBranchDefect",
                StringComparison.OrdinalIgnoreCase))
           && entry.Report.Verdicts.Any(verdict =>
               string.Equals(verdict.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase)
               && string.Equals(verdict.Classification, "DeferredToBatch", StringComparison.Ordinal));

    /// <summary>
    /// Restores the delivery sidecar from the journaled decision when an
    /// Auto Review card lost it (process death before the sidecar write).
    /// Callers verify that the entry belongs to the current accepted review.
    /// </summary>
    public static bool RestoreDeliverySidecar(TaskInfo task, RemoteReviewSettlementEntry entry)
    {
        if (!WouldRestoreDeliverySidecar(task, entry)) return false;
        RemoteDeliverySettlementStore.Write(task.FolderPath, entry.Delivery!);
        return true;
    }

    /// <summary>
    /// True when <see cref="RestoreDeliverySidecar"/> would write for this
    /// lane: the journal carries a delivery and the sidecar does not belong to
    /// the entry's attempt. Callers on a lane snapshot re-read the card only
    /// then (AGT-3091).
    /// </summary>
    public static bool WouldRestoreDeliverySidecar(TaskInfo task, RemoteReviewSettlementEntry entry)
        => entry.Delivery is not null
           && string.Equals(task.State, TaskStates.AutoReview, StringComparison.Ordinal)
           && !RemoteDeliverySettlementStore.MatchesAttempt(
               RemoteDeliverySettlementStore.Read(task.FolderPath), entry.AttemptId);
}
