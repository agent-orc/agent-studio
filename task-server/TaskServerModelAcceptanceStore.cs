using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Host model acceptance for coding claims. A named model launches only on a
/// runner whose fresh probe accepted it; a launch rejection holds that model
/// until the same runner instance writes a causally newer successful probe.
/// </summary>
public sealed partial class TaskServerStore
{
    private const string ModelRefusedDetail = "The provider refused this model for the host login.";

    private static async Task RecordModelRejectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string capabilityKey,
        DateTime now,
        CancellationToken ct)
        => await ExecuteAsync(connection, """
            UPDATE runner_capabilities
               SET advertised_status = 'unavailable', detail = $detail
             WHERE runner_id = $runner AND capability_key = $key;
            INSERT INTO runner_model_rejections(runner_id, capability_key, rejected_at)
            VALUES ($runner, $key, $now)
            ON CONFLICT(runner_id, capability_key) DO UPDATE SET
                rejected_at = excluded.rejected_at,
                acknowledged_instance_id = NULL,
                acknowledged_sequence = NULL;
            """, ct, transaction,
            ("$runner", runnerId), ("$key", capabilityKey),
            ("$detail", ModelRefusedDetail), ("$now", Iso(now)));

    /// <summary>
    /// Applies the rejection ledger to one advertised model-acceptance entry and
    /// returns the status to store. Server receipt time is never evidence.
    /// </summary>
    private static async Task<string> ApplyModelRejectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapabilityAdvertisementRequest request,
        string capabilityKey,
        string advertisedStatus,
        long? probeSequence,
        CancellationToken ct)
    {
        ModelRejectionAcknowledgement? acknowledgement = null;
        await using (var command = Command(connection, """
            SELECT acknowledged_instance_id, acknowledged_sequence FROM runner_model_rejections
             WHERE runner_id = $runner AND capability_key = $key;
            """, transaction, ("$runner", request.RunnerId), ("$key", capabilityKey)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                acknowledgement = new ModelRejectionAcknowledgement(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1));
        }
        if (acknowledgement is null) return advertisedStatus;

        var (cleared, next) = ModelAcceptanceRecovery.Evaluate(
            acknowledgement, request.InstanceId, advertisedStatus, probeSequence);
        await ExecuteAsync(connection, cleared
            ? "DELETE FROM runner_model_rejections WHERE runner_id = $runner AND capability_key = $key;"
            : """
              UPDATE runner_model_rejections
                 SET acknowledged_instance_id = $instance, acknowledged_sequence = $sequence
               WHERE runner_id = $runner AND capability_key = $key;
              """, ct, transaction,
            ("$runner", request.RunnerId), ("$key", capabilityKey),
            ("$instance", next.InstanceId), ("$sequence", next.Sequence));
        return cleared || advertisedStatus != "ready" ? advertisedStatus : "unavailable";
    }

    /// <summary>Keeps claim order and skips tasks whose effective model this runner has not proven.</summary>
    private async IAsyncEnumerable<TaskDto> WhereModelAcceptedAsync(
        IAsyncEnumerable<TaskDto> tasks,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var task in tasks.WithCancellation(ct))
        {
            if (await ModelAcceptanceRefusalAsync(connection, transaction, task.TaskId, runnerId, ct) is null)
                yield return task;
        }
    }

    /// <summary>
    /// Null when the runner may launch the task's effective route now; otherwise
    /// the refusal. A blank model is the CLI's implicit default route, which the
    /// provider-auth real request already proves.
    /// </summary>
    private async Task<string?> ModelAcceptanceRefusalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string runnerId,
        CancellationToken ct)
    {
        var (provider, model) = await ReadEffectiveModelRouteAsync(connection, transaction, taskId, ct);
        if (string.IsNullOrWhiteSpace(model)) return null;
        var key = CapabilityProtocol.ModelAcceptance(provider, model);
        var accepted = Convert.ToInt64(await ScalarAsync(connection, """
            SELECT count(*) FROM runner_capabilities capability
             WHERE capability.runner_id = $runner AND capability.capability_key = $key
               AND capability.advertised_status = 'ready' AND capability.fresh_until > $now
               AND NOT EXISTS (
                   SELECT 1 FROM runner_model_rejections rejection
                    WHERE rejection.runner_id = $runner AND rejection.capability_key = $key);
            """, ct, transaction, ("$runner", runnerId), ("$key", key), ("$now", Iso(UtcNow))),
            CultureInfo.InvariantCulture) > 0;
        return accepted ? null : $"model-acceptance-unverified: {provider}/{model}";
    }

    /// <summary>
    /// The route a claim launches, in the runner's precedence: a mechanical
    /// fresh route, an explicitly selected continuation, a provider-rejection
    /// fallback, then the card's primary fields.
    /// </summary>
    private async Task<(string Provider, string? Model)> ReadEffectiveModelRouteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken ct)
    {
        string primaryCli;
        string? primaryModel;
        await using (var command = Command(connection, """
            SELECT COALESCE(NULLIF(lower(trim(cli_type)), ''), 'claude'), NULLIF(trim(model), '')
              FROM task_studio_fields WHERE task_id = $task;
            """, transaction, ("$task", taskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            var found = await reader.ReadAsync(ct);
            primaryCli = found ? reader.GetString(0) : "claude";
            primaryModel = found && !reader.IsDBNull(1) ? reader.GetString(1) : null;
        }

        var delta = await ReadUnclaimedMechanicalDeltaAsync(connection, transaction, taskId, ct);
        var previousJson = Convert.ToString(await ScalarAsync(connection, """
            SELECT payload_json FROM events
             WHERE task_id = $task AND kind = 'session.continuation'
             ORDER BY rowid DESC LIMIT 1;
            """, ct, transaction, ("$task", taskId)), CultureInfo.InvariantCulture);
        SessionContinuationLedgerEntry? previous = null;
        if (!string.IsNullOrWhiteSpace(previousJson))
        {
            try { previous = JsonSerializer.Deserialize<SessionContinuationLedgerEntry>(previousJson); }
            catch (JsonException) { /* Corrupt evidence never selects a route. */ }
        }
        if (await ReadMechanicalFreshRouteAsync(connection, transaction, taskId, delta, previous, ct)
            is { } mechanical)
            return (mechanical.CliType, mechanical.Model);

        if (await ReadNextContinuationAsync(connection, transaction, taskId, ct)
            is { Receipt.ExplicitSelection: true } intent)
        {
            var selection = intent.Selection;
            var cli = selection is null || selection.CliType || selection.Model || selection.ThinkingLevel
                ? intent.CliType : null;
            var model = selection is null || selection.Model ? intent.Model : null;
            return (string.IsNullOrWhiteSpace(cli) ? primaryCli : cli.Trim().ToLowerInvariant(), model);
        }

        if (await ReadProviderFallbackForClaimAsync(connection, transaction, taskId, ct) is { } continuation)
            return (continuation.Fallback.CliType, continuation.Fallback.To);
        return (primaryCli, primaryModel);
    }

    /// <summary>
    /// Named models the operator configured as quota routes or pinned on ready
    /// cards. Hosts probe these in addition to their built-in candidates.
    /// </summary>
    private static async Task<IReadOnlyList<ConfiguredModelRouteDto>> ReadConfiguredModelRoutesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var routes = new List<ConfiguredModelRouteDto>();
        await using (var command = Command(connection, """
            SELECT DISTINCT COALESCE(NULLIF(lower(trim(fields.cli_type)), ''), 'claude'), trim(fields.model)
              FROM task_studio_fields fields
              JOIN tasks task ON task.id = fields.task_id
             WHERE task.state = '2-ready' AND NULLIF(trim(fields.model), '') IS NOT NULL
             ORDER BY 1, 2 LIMIT 32;
            """, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                routes.Add(new ConfiguredModelRouteDto(reader.GetString(0), reader.GetString(1)));
        }
        var routesJson = Convert.ToString(await ScalarAsync(connection,
            "SELECT quota_model_routes_json FROM studio_cli_settings WHERE id = 1;", ct, transaction),
            CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(routesJson))
        {
            try
            {
                using var document = JsonDocument.Parse(routesJson);
                CollectQuotaRoutes(document.RootElement, routes);
            }
            catch (JsonException) { /* An unreadable routes document configures no probe. */ }
        }
        return routes.Distinct().ToArray();
    }

    private static void CollectQuotaRoutes(JsonElement element, List<ConfiguredModelRouteDto> routes)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectQuotaRoutes(item, routes);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        string? Text(string name) => element.TryGetProperty(name, out var value)
                                     && value.ValueKind == JsonValueKind.String
                                     && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim() : null;
        if (Text("cliType") is { } cli)
        {
            if (Text("primaryModel") is { } primary)
                routes.Add(new ConfiguredModelRouteDto(cli.ToLowerInvariant(), primary));
            if (Text("fallbackModel") is { } fallback)
                routes.Add(new ConfiguredModelRouteDto(
                    (Text("fallbackCliType") ?? cli).ToLowerInvariant(), fallback));
            return;
        }
        foreach (var property in element.EnumerateObject()) CollectQuotaRoutes(property.Value, routes);
    }
}
