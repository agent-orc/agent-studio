using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Persistence;

namespace AgentStudio.Pipeline;

/// <summary>
/// How far the settlement of one passed Remote delivery has progressed. The
/// stages are the only resume points a restarted backend can trust, because
/// everything between them lives in one HTTP request that dies with the
/// process (AGT-2860).
/// </summary>
public enum RemoteDeliverySettlementStage
{
    /// <summary>The review settled and the integration verdict is decided, but <c>remote-delivery-integration</c> has not returned yet.</summary>
    IntegrationPending,

    /// <summary>Integration returned (merged, already merged, or failed); the lane transition has not landed yet.</summary>
    IntegrationSettled,

    /// <summary>The card left <c>4-auto-review</c> through the normal transition; nothing is outstanding.</summary>
    LaneSettled,
}

/// <summary>
/// Durable resume point for the post-review delivery sequence of one card.
/// <para>
/// The review report endpoint settles the ReviewAttempt, decides the delivery
/// gate from the report's verdicts, integrates, and only then moves the card
/// out of <c>4-auto-review</c>. All of that used to live inside a single
/// request: a backend restart in the middle left a card with a terminal
/// <c>Pass</c> attempt, <c>integration: pending</c>, and nobody able to tell
/// whether the gate had admitted the delivery - the verdicts that decided it
/// exist only in the report payload (AGT-2855, 17.09.2026). This sidecar writes
/// that decision down before the first side effect, so a later pass can resume
/// the sequence without re-reviewing an already passed subject.
/// </para>
/// <para>
/// <see cref="ReviewAttemptId"/> fences the record against a later delivery
/// generation: a card that was requeued and re-reviewed carries a different
/// current attempt, and the stale record is then ignored rather than replayed.
/// </para>
/// </summary>
public sealed record RemoteDeliverySettlementRecord
{
    public int Version { get; init; } = 1;

    /// <summary>Compare-and-swap revision. Legacy sidecars start at zero.</summary>
    public long Revision { get; init; }

    public string TaskKey { get; init; } = "";

    /// <summary>ReviewAttempt whose settlement opened this delivery sequence.</summary>
    public string ReviewAttemptId { get; init; } = "";

    /// <summary>New settlements require the canonical report journal for replay.</summary>
    public bool JournalRequired { get; init; }

    /// <summary>Terminal review outcome, as the authority recorded it.</summary>
    public string Outcome { get; init; } = "";

    /// <summary>Verdict of <see cref="RemoteDeliveryIntegrationPolicy"/> for this delivery.</summary>
    public bool ShouldIntegrate { get; init; }

    /// <summary>Build/test gate class behind <see cref="ShouldIntegrate"/>.</summary>
    public string BuildTestGate { get; init; } = "";

    /// <summary>Human-readable reason the gate admitted or refused the delivery.</summary>
    public string GateReason { get; init; } = "";

    public string IntegrationBranch { get; init; } = "";

    public string IntegrationStrategy { get; init; } = "";

    public string PipelineType { get; init; } = "";

    public DateTimeOffset DeliveredAtUtc { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RemoteDeliverySettlementStage Stage { get; init; }

    /// <summary>Outcome string of the integration run, once one returned.</summary>
    public string? IntegrationOutcome { get; init; }

    /// <summary>Operator-facing integration continuation detail, when one exists.</summary>
    public string? IntegrationDetail { get; init; }

    /// <summary>A recovery requeue must bypass the coordinator's completed-result cache.</summary>
    public bool DiscardCompletedReplay { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }

    // A rollback restores the same delivery content with a newer persistence
    // revision. Keep value equality about that content; concurrency checks must
    // explicitly compare Revision through MatchesRevision below.
    public bool Equals(RemoteDeliverySettlementRecord? other)
        => other is not null
           && Version == other.Version
           && TaskKey == other.TaskKey
           && ReviewAttemptId == other.ReviewAttemptId
           && JournalRequired == other.JournalRequired
           && Outcome == other.Outcome
           && ShouldIntegrate == other.ShouldIntegrate
           && BuildTestGate == other.BuildTestGate
           && GateReason == other.GateReason
           && IntegrationBranch == other.IntegrationBranch
           && IntegrationStrategy == other.IntegrationStrategy
           && PipelineType == other.PipelineType
           && DeliveredAtUtc == other.DeliveredAtUtc
           && Stage == other.Stage
           && IntegrationOutcome == other.IntegrationOutcome
           && IntegrationDetail == other.IntegrationDetail
           && DiscardCompletedReplay == other.DiscardCompletedReplay
           && RecordedAtUtc == other.RecordedAtUtc;

    public override int GetHashCode() => HashCode.Combine(Version, TaskKey, ReviewAttemptId);
}

/// <summary>
/// Reads and writes <see cref="RemoteDeliverySettlementRecord"/> beside the task
/// folder. Every write flushes the file and supported rename metadata. The settled
/// ReviewAttempt stays authoritative; new report workflows also journal the
/// delivery decision so a missing sidecar can be reconstructed safely.
/// </summary>
public static class RemoteDeliverySettlementStore
{
    public const string FileName = "remote-delivery-settlement.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string taskFolder)
        => Path.Combine(TaskPaths.LogsDir(taskFolder), FileName);

    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static object Gate(string taskFolder)
        => Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(taskFolder)), _ => new object());

    // Bootstrap/reconstruction boundary. Delayed completions must use the
    // snapshot overloads below, never install a record through this method.
    public static void Write(string taskFolder, RemoteDeliverySettlementRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(record);
        lock (Gate(taskFolder))
        {
            var current = Read(taskFolder);
            if (current == record)
            {
                Persist(taskFolder, current);
                return;
            }
            if (MatchesAttempt(current, record.ReviewAttemptId))
                record = record with { Stage = current!.Stage > record.Stage ? current.Stage : record.Stage };
            Persist(taskFolder, record with { Revision = current is null ? 0 : checked(current.Revision + 1) });
        }
    }

    /// <summary>Installs a delivery only if the snapshot read before preparation still owns the file.</summary>
    public static RemoteDeliverySettlementRecord? TryWrite(string taskFolder,
        RemoteDeliverySettlementRecord? expected, RemoteDeliverySettlementRecord record)
    {
        lock (Gate(taskFolder))
        {
            var current = Read(taskFolder);
            if (expected is null ? current is not null : !MatchesRevision(current, expected)) return null;
            if (MatchesAttempt(current, record.ReviewAttemptId) && current!.Stage > record.Stage)
                return null; // Only an explicit reopen may rewind this generation.
            var next = record with { Revision = current is null ? 0 : checked(current.Revision + 1) };
            Persist(taskFolder, next, replaceExisting: current is not null);
            return next;
        }
    }

    private static void Persist(string taskFolder, RemoteDeliverySettlementRecord record, bool replaceExisting = false)
    {
        var writer = new AtomicJsonFileWriter();
        var json = JsonSerializer.Serialize(record, Json);
        if (replaceExisting) writer.ReplaceExisting(PathFor(taskFolder), json);
        else writer.Write(PathFor(taskFolder), json);
    }

    public static RemoteDeliverySettlementRecord? Read(string taskFolder)
    {
        if (string.IsNullOrWhiteSpace(taskFolder)) return null;
        lock (Gate(taskFolder))
        {
            var path = PathFor(taskFolder);
            if (!File.Exists(path)) return null;
            try
            {
                var record = JsonSerializer.Deserialize<RemoteDeliverySettlementRecord>(File.ReadAllText(path), Json);
                return record is not null && !string.IsNullOrWhiteSpace(record.ReviewAttemptId) ? record : null;
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "RemoteDeliverySettlementStore: malformed or unreadable settlement record");
                return null;
            }
        }
    }

    private static bool MatchesRevision(RemoteDeliverySettlementRecord? current,
        RemoteDeliverySettlementRecord expected)
        => MatchesAttempt(current, expected.ReviewAttemptId) && current!.Revision == expected.Revision;

    private static RemoteDeliverySettlementRecord? Update(string taskFolder,
        RemoteDeliverySettlementRecord expected,
        Func<RemoteDeliverySettlementRecord, RemoteDeliverySettlementRecord?> update)
    {
        lock (Gate(taskFolder))
        {
            var current = Read(taskFolder);
            if (!MatchesRevision(current, expected)) return null;
            var next = update(current!);
            if (next is null) return null;
            // An idempotent replay still reconfirms directory durability, but
            // does not invalidate another completion's unchanged snapshot.
            if (next != current || next.Revision != current!.Revision)
                next = next with { Revision = checked(current!.Revision + 1) };
            Persist(taskFolder, next, replaceExisting: true);
            return next;
        }
    }

    /// <summary>Atomically advances the expected attempt and revision. A stale completion returns null.</summary>
    public static RemoteDeliverySettlementRecord? Advance(string taskFolder,
        RemoteDeliverySettlementRecord expected, RemoteDeliverySettlementStage stage,
        string? integrationOutcome = null, string? integrationDetail = null)
        => Update(taskFolder, expected, current =>
            current.Stage >= stage && integrationOutcome is null ? current : current with
            {
                Stage = current.Stage >= stage ? current.Stage : stage,
                IntegrationOutcome = integrationOutcome ?? current.IntegrationOutcome,
                IntegrationDetail = integrationDetail ?? current.IntegrationDetail,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

    /// <summary>Replaces integration detail, including clearing an earlier park reason, within the same revision gate.</summary>
    public static RemoteDeliverySettlementRecord? RecordIntegration(string taskFolder,
        RemoteDeliverySettlementRecord expected, string integrationOutcome, string? integrationDetail)
        => Update(taskFolder, expected, current => current with
        {
            Stage = current.Stage >= RemoteDeliverySettlementStage.IntegrationSettled
                ? current.Stage : RemoteDeliverySettlementStage.IntegrationSettled,
            IntegrationOutcome = integrationOutcome,
            IntegrationDetail = integrationDetail,
            DiscardCompletedReplay = false,
            RecordedAtUtc = DateTimeOffset.UtcNow,
        });

    /// <summary>Explicitly reopens a generation for infrastructure recovery, invalidating outstanding completions.</summary>
    public static RemoteDeliverySettlementRecord? RequeueInfrastructure(string taskFolder,
        RemoteDeliverySettlementRecord expected)
        => Update(taskFolder, expected, current => current.ShouldIntegrate
            && current.Stage is RemoteDeliverySettlementStage.LaneSettled or RemoteDeliverySettlementStage.IntegrationPending
                ? Reopened(current) : null);

    /// <summary>Explicit operator/engine retry of the same passed review. No other update may rewind stages.</summary>
    public static RemoteDeliverySettlementRecord? ReopenForRetry(string taskFolder,
        RemoteDeliverySettlementRecord expected)
        => Update(taskFolder, expected, current => current.ShouldIntegrate ? Reopened(current) : null);

    private static RemoteDeliverySettlementRecord Reopened(RemoteDeliverySettlementRecord current)
        => current with
        {
            Stage = RemoteDeliverySettlementStage.IntegrationPending,
            IntegrationOutcome = null,
            IntegrationDetail = null,
            DiscardCompletedReplay = true,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            // Even an already pending retry must fence an in-flight completion.
            Revision = checked(current.Revision + 1),
        };

    // Immediate operations retained for existing bootstrap/recovery consumers.
    // The read and the mutation share the gate; callers spanning an await use
    // the snapshot overloads so the earlier attempt AND revision are required.
    internal static bool Advance(string taskFolder, RemoteDeliverySettlementStage stage,
        string? integrationOutcome = null, string? integrationDetail = null)
    {
        lock (Gate(taskFolder))
            return Read(taskFolder) is { } current
                && Advance(taskFolder, current, stage, integrationOutcome, integrationDetail) is not null;
    }

    internal static bool RecordIntegration(string taskFolder, string integrationOutcome, string? integrationDetail)
    {
        lock (Gate(taskFolder))
            return Read(taskFolder) is { } current
                && RecordIntegration(taskFolder, current, integrationOutcome, integrationDetail) is not null;
    }

    internal static bool RequeueInfrastructure(string taskFolder)
    {
        lock (Gate(taskFolder))
            return Read(taskFolder) is { } current && RequeueInfrastructure(taskFolder, current) is not null;
    }

    public static bool ReopenForRetry(string taskFolder, string reviewAttemptId)
    {
        lock (Gate(taskFolder))
            return Read(taskFolder) is { } current && MatchesAttempt(current, reviewAttemptId)
                && ReopenForRetry(taskFolder, current) is not null;
    }

    /// <summary>
    /// True when the record describes the delivery generation that is current
    /// right now. A card that was requeued and re-reviewed has a different
    /// current ReviewAttempt, and its old record must never be replayed.
    /// </summary>
    public static bool MatchesAttempt(RemoteDeliverySettlementRecord? record, string? currentReviewAttemptId)
        => record is not null
           && !string.IsNullOrWhiteSpace(currentReviewAttemptId)
           && string.Equals(record.ReviewAttemptId, currentReviewAttemptId, StringComparison.Ordinal);
}
