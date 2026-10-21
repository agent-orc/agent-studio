using System.Globalization;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    /// <summary>
    /// The model-acceptance key a coding claim on this runner still needs, or
    /// null when the claim may create a lease. The effective model is a
    /// provider-rejection continuation's fallback, else the card's primary pin.
    /// A blank model runs the CLI default route, which the provider-auth real
    /// request already proves. Both durable claim paths call this inside their
    /// write transaction, so admission and lease creation are atomic.
    /// </summary>
    private async Task<string?> UnacceptedEffectiveModelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string taskId,
        CancellationToken ct)
    {
        string provider;
        string? model;
        if (await ReadProviderFallbackForClaimAsync(connection, transaction, taskId, ct) is { } continuation)
        {
            provider = continuation.Fallback.CliType;
            model = continuation.Fallback.To;
        }
        else
        {
            await using var command = Command(connection, """
                SELECT COALESCE(NULLIF(lower(cli_type), ''), 'claude'), model
                  FROM task_studio_fields WHERE task_id = $task;
                """, transaction, ("$task", taskId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            provider = reader.GetString(0);
            model = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        if (string.IsNullOrWhiteSpace(model)) return null;

        // A launch rejection sets this row unavailable, and advertisement
        // ingest restores ready only for a probe sequenced after the rejection.
        var key = CapabilityProtocol.ModelAcceptance(provider, model);
        var accepted = await ScalarAsync(connection, """
            SELECT 1 FROM runner_capabilities
             WHERE runner_id = $runner AND capability_key = $key
               AND advertised_status = 'ready' AND fresh_until > $now;
            """, ct, transaction, ("$runner", runnerId), ("$key", key), ("$now", Iso(UtcNow)));
        return accepted is null ? key : null;
    }

    /// <summary>The largest runner probe sequence of a launch rejection, or null.</summary>
    private static async Task<(bool Held, long? Sequence)> ReadModelRejectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string key,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT COUNT(*), MAX(json_extract(held.payload_json, '$.rejectionSequence'))
              FROM events held
             WHERE held.kind = 'execution.model-capability-held'
               AND json_extract(held.payload_json, '$.runnerId') = $runner
               AND 'model-acceptance:' || lower(json_extract(held.payload_json, '$.provider'))
                   || ':' || lower(json_extract(held.payload_json, '$.model')) = $key;
            """, transaction, ("$runner", runnerId), ("$key", key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt64(0) == 0) return (false, null);
        return (true, reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
    }
}
