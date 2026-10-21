using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Host model acceptance on the durable coding claim and permit paths. Each
/// check runs inside the write transaction that creates the lease.
/// </summary>
public sealed partial class TaskServerStore
{
    private const int MaxModelProbeKeys = 24;

    /// <summary>Yields only tasks whose effective named route this host accepts.</summary>
    private async IAsyncEnumerable<TaskDto> WhereModelRouteAcceptedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IAsyncEnumerable<TaskDto> tasks,
        string runnerId,
        List<string> refusals,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var task in tasks.WithCancellation(ct))
        {
            var refusal = await ModelRouteRefusalAsync(
                connection, transaction, task.TaskId, runnerId, deliversContinuationRoutes: true, ct);
            if (refusal is null) yield return task;
            else refusals.Add(refusal);
        }
    }

    /// <summary>
    /// Null when the host may lease the task for the model it would launch.
    /// A permit delivers only the mechanical route, so it passes
    /// <paramref name="deliversContinuationRoutes"/> as false.
    /// </summary>
    private async Task<string?> ModelRouteRefusalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string runnerId,
        bool deliversContinuationRoutes,
        CancellationToken ct)
    {
        var mechanicalRoute = await ReadMechanicalFreshRouteAsync(
            connection, transaction, taskId,
            await ReadUnclaimedMechanicalDeltaAsync(connection, transaction, taskId, ct),
            await ReadPreviousSessionAsync(connection, transaction, taskId, ct), ct);
        var intent = deliversContinuationRoutes
            ? await ReadNextContinuationAsync(connection, transaction, taskId, ct) : null;
        var fallback = deliversContinuationRoutes
            ? (await ReadProviderFallbackForClaimAsync(connection, transaction, taskId, ct))?.Fallback : null;
        string? primaryCli = null, primaryModel = null;
        await using (var command = Command(connection, """
            SELECT cli_type, model FROM task_studio_fields WHERE task_id = $task;
            """, transaction, ("$task", taskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                primaryCli = reader.IsDBNull(0) ? null : reader.GetString(0);
                primaryModel = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        var route = CodingRouteAcceptancePolicy.EffectiveNamedRoute(
            mechanicalRoute, intent, fallback, primaryCli, primaryModel);
        if (route is null) return null;
        return CodingRouteAcceptancePolicy.Refusal(
            route, await ReadModelAcceptanceEvidenceAsync(connection, transaction, runnerId, route.Key, ct), UtcNow);
    }

    private static async Task<CodingRouteAcceptancePolicy.AcceptanceEvidence> ReadModelAcceptanceEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string key,
        CancellationToken ct)
    {
        string? status = null;
        DateTime? freshUntil = null, watermark = null;
        await using (var command = Command(connection, """
            SELECT advertised_status, fresh_until, probe_server_watermark
              FROM runner_capabilities
             WHERE runner_id = $runner AND capability_key = $key;
            """, transaction, ("$runner", runnerId), ("$key", key)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                status = reader.GetString(0);
                freshUntil = Parse(reader.GetString(1));
                watermark = reader.IsDBNull(2) ? null : Parse(reader.GetString(2));
            }
        }
        var rejectedAt = await LatestModelRejectionAsync(connection, transaction, runnerId, key, ct);
        return new(status, freshUntil, watermark, rejectedAt);
    }

    /// <summary>Server time of the latest launch refusal of this model on this runner.</summary>
    private static async Task<DateTime?> LatestModelRejectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string key,
        CancellationToken ct)
        => await ScalarAsync(connection, """
            SELECT MAX(held.occurred_at) FROM events held
             WHERE held.kind = 'execution.model-capability-held'
               AND json_extract(held.payload_json, '$.runnerId') = $runner
               AND lower('model-acceptance:' || json_extract(held.payload_json, '$.provider')
                   || ':' || json_extract(held.payload_json, '$.model')) = $key;
            """, ct, transaction, ("$runner", runnerId), ("$key", key)) is string rejectedAt
            ? Parse(rejectedAt)
            : null;

    private static async Task<SessionContinuationLedgerEntry?> ReadPreviousSessionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string taskId, CancellationToken ct)
    {
        var json = Convert.ToString(await ScalarAsync(connection, """
            SELECT payload_json FROM events
             WHERE task_id = $task AND kind = 'session.continuation'
             ORDER BY rowid DESC LIMIT 1;
            """, ct, transaction, ("$task", taskId)), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SessionContinuationLedgerEntry>(json); }
        catch (JsonException) { return null; /* Corrupt evidence never authorizes a resume. */ }
    }

    /// <summary>
    /// Named primary routes of Ready cards. A host must prove these with its
    /// own login before a claim can lease them.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadModelProbeKeysAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT DISTINCT lower(COALESCE(NULLIF(trim(fields.cli_type), ''), 'claude')), lower(trim(fields.model))
              FROM tasks t
              JOIN task_studio_fields fields ON fields.task_id = t.id
             WHERE t.state = '2-ready' AND trim(COALESCE(fields.model, '')) <> ''
             ORDER BY 1, 2
             LIMIT $limit;
            """, transaction, ("$limit", MaxModelProbeKeys));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var keys = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var key = CapabilityProtocol.ModelAcceptance(reader.GetString(0), reader.GetString(1));
            if (CapabilityProtocol.TryParseModelAcceptance(key, out _, out _)) keys.Add(key);
        }
        return keys;
    }
}
