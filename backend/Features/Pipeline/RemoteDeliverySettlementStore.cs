using System.Text.Json;
using System.Text.Json.Serialization;

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

    public string TaskKey { get; init; } = "";

    /// <summary>ReviewAttempt whose settlement opened this delivery sequence.</summary>
    public string ReviewAttemptId { get; init; } = "";

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

    public DateTimeOffset RecordedAtUtc { get; init; }
}

/// <summary>
/// Reads and writes <see cref="RemoteDeliverySettlementRecord"/> beside the task
/// folder. Every write is atomic (temp file plus move) and best-effort at the
/// call site: the settled ReviewAttempt stays authoritative, this sidecar only
/// shortens the path back to a resumable state.
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

    public static void Write(string taskFolder, RemoteDeliverySettlementRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(record);

        var path = PathFor(taskFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "RemoteDeliverySettlementStore: temporary file cleanup");
            }
        }
    }

    public static RemoteDeliverySettlementRecord? Read(string taskFolder)
    {
        if (string.IsNullOrWhiteSpace(taskFolder)) return null;
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<RemoteDeliverySettlementRecord>(
                File.ReadAllText(path), Json);
            return record is not null && !string.IsNullOrWhiteSpace(record.ReviewAttemptId)
                ? record
                : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "RemoteDeliverySettlementStore: malformed or unreadable settlement record");
            return null;
        }
    }

    /// <summary>
    /// Moves the record forward one stage. Idempotent and monotonic: replaying
    /// an earlier stage never rewinds a record another pass already advanced,
    /// so a resumed sequence and the original request cannot fight over it.
    /// Returns false when there is no record for this folder.
    /// </summary>
    public static bool Advance(
        string taskFolder,
        RemoteDeliverySettlementStage stage,
        string? integrationOutcome = null)
    {
        var current = Read(taskFolder);
        if (current is null) return false;
        if (current.Stage >= stage && integrationOutcome is null) return true;

        Write(taskFolder, current with
        {
            Stage = current.Stage >= stage ? current.Stage : stage,
            IntegrationOutcome = integrationOutcome ?? current.IntegrationOutcome,
            RecordedAtUtc = DateTimeOffset.UtcNow,
        });
        return true;
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
