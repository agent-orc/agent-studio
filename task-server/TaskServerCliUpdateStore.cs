using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly (string Model, string Cli, string Minimum)[] ModelCliMinimums =
    [
        ("gpt-6-astra", "codex", "0.153.0"),
    ];

    private async Task EvaluateHostCliPolicyAsync(string runnerId, string actorId, CancellationToken ct)
    {
        List<TaskServerOperationalEvent> events = [];
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var runner = await ScalarAsync(connection,
                "SELECT host_id FROM runners WHERE id = $runner;", ct, transaction, ("$runner", runnerId));
            var hostId = Convert.ToString(runner, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(hostId)) return;
            var capabilities = new List<CapabilityHealthDto>();
            await using (var command = Command(connection, """
                SELECT capability_key, version, identity_value, advertised_at
                  FROM runner_capabilities
                 WHERE runner_id = $runner AND capability_key LIKE 'cli-execution:%';
                """, transaction, ("$runner", runnerId)))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    capabilities.Add(new CapabilityHealthDto(
                        reader.GetString(0), "cli-execution", "ready", "healthy", null,
                        Parse(reader.GetString(3)), Parse(reader.GetString(3)).AddMinutes(3), true,
                        null, null, null, null, 0,
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2), null, [], []));
            }
            var installations = InstalledCliProjection.FromCapabilities(
                capabilities, _options.CodexCliTargetVersion, _options.ClaudeCliTargetVersion);
            foreach (var cli in installations)
            {
                if (!cli.IsBelowTarget)
                {
                    await ExecuteAsync(connection,
                        "DELETE FROM studio_host_cli_drift WHERE host_id = $host AND cli_name = $cli;",
                        ct, transaction, ("$host", hostId), ("$cli", cli.Name));
                    continue;
                }
                await ExecuteAsync(connection, """
                    INSERT INTO studio_host_cli_drift(
                        host_id, cli_name, installed_version, target_version, first_detected_at, alarmed_at)
                    VALUES($host, $cli, $installed, $target, $now, NULL)
                    ON CONFLICT(host_id, cli_name) DO UPDATE SET
                        installed_version = excluded.installed_version,
                        target_version = excluded.target_version;
                    """, ct, transaction,
                    ("$host", hostId), ("$cli", cli.Name), ("$installed", cli.Version),
                    ("$target", cli.TargetVersion), ("$now", Iso(UtcNow)));
                var first = await ScalarAsync(connection, """
                    SELECT first_detected_at FROM studio_host_cli_drift
                     WHERE host_id = $host AND cli_name = $cli AND alarmed_at IS NULL;
                    """, ct, transaction, ("$host", hostId), ("$cli", cli.Name));
                if (first is not null && Parse(Convert.ToString(first, CultureInfo.InvariantCulture)!) <= UtcNow.AddHours(-24))
                {
                    await ExecuteAsync(connection, """
                        UPDATE studio_host_cli_drift SET alarmed_at = $now
                         WHERE host_id = $host AND cli_name = $cli AND alarmed_at IS NULL;
                        """, ct, transaction, ("$now", Iso(UtcNow)), ("$host", hostId), ("$cli", cli.Name));
                    events.Add(new TaskServerOperationalEvent("host.cli-drift.alarm", UtcNow, actorId, new
                    {
                        hostId,
                        cli = cli.Name,
                        summary = $"{hostId}: {cli.Name} {cli.Version} is below target {cli.TargetVersion} for more than 24 hours.",
                    }));
                }

                foreach (var minimum in ModelCliMinimums.Where(item => item.Cli == cli.Name
                    && InstalledCliVersionPolicy.IsBelow(cli.Version, item.Minimum)))
                {
                    var inserted = await ExecuteAsync(connection, """
                        INSERT OR IGNORE INTO studio_host_model_cli_alerts(
                            host_id, model_id, cli_name, installed_version, minimum_version, alerted_at)
                        VALUES($host, $model, $cli, $installed, $minimum, $now);
                        """, ct, transaction,
                        ("$host", hostId), ("$model", minimum.Model), ("$cli", minimum.Cli),
                        ("$installed", cli.Version), ("$minimum", minimum.Minimum), ("$now", Iso(UtcNow)));
                    if (inserted > 0)
                    {
                        var displayedMinimum = minimum.Minimum.EndsWith(".0", StringComparison.Ordinal)
                            ? minimum.Minimum[..^2]
                            : minimum.Minimum;
                        events.Add(new TaskServerOperationalEvent("host.model-cli-version.blocked", UtcNow, actorId, new
                        {
                            hostId,
                            model = minimum.Model,
                            summary = $"{hostId}: {minimum.Model} needs {minimum.Cli}-cli ≥ {displayedMinimum} " +
                                      $"(host has {cli.Version}).",
                        }));
                    }
                }
            }
        }, ct);
        await PublishOperationalEventsAsync(events, ct);
    }

    public async Task<HostCliUpdateDto> RequestHostCliUpdateAsync(
        string hostId,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        var normalized = RequireHostId(hostId);
        await RequestOperatorHostDrainAsync(
            normalized,
            new OperatorHostDrainRequest("CLI update is waiting for running runs and reviews to finish."),
            actorId,
            ct);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = await ReadHostCliUpdateAsync(connection, transaction, normalized, ct);
            if (existing is not null && existing.State is CliUpdateStates.Draining
                or CliUpdateStates.Ready or CliUpdateStates.Upgrading or CliUpdateStates.Probing)
                return;
            var now = UtcNow;
            await ExecuteAsync(connection, """
                INSERT INTO studio_host_cli_updates(
                    host_id, state, codex_target_version, claude_target_version,
                    requested_at, updated_at, completed_at, detail)
                VALUES($host, $state, $codex, $claude, $now, $now, NULL, $detail)
                ON CONFLICT(host_id) DO UPDATE SET
                    state = excluded.state,
                    codex_target_version = excluded.codex_target_version,
                    claude_target_version = excluded.claude_target_version,
                    requested_at = excluded.requested_at,
                    updated_at = excluded.updated_at,
                    completed_at = NULL,
                    detail = excluded.detail;
                """, ct, transaction,
                ("$host", normalized),
                ("$state", CliUpdateStates.Draining),
                ("$codex", _options.CodexCliTargetVersion),
                ("$claude", _options.ClaudeCliTargetVersion),
                ("$now", Iso(now)),
                ("$detail", "Waiting for active host slots to reach zero."));
            await AuditAsync(connection, transaction, actorId, "host.cli-update.requested", "host", normalized,
                JsonSerializer.Serialize(new
                {
                    codex = _options.CodexCliTargetVersion,
                    claude = _options.ClaudeCliTargetVersion,
                }), ct);
        }, ct);
        var result = await GetHostCliUpdateAsync(normalized, ct)
            ?? throw new InvalidOperationException("CLI update request was not persisted.");
        await PublishOperationalEventsAsync([
            new TaskServerOperationalEvent("host.cli-update.requested", UtcNow, actorId, result)
        ], ct);
        return result;
    }

    public async Task<HostCliUpdateDto> CancelHostCliUpdateAsync(
        string hostId,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        var normalized = RequireHostId(hostId);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadHostCliUpdateAsync(connection, transaction, normalized, ct)
                ?? throw new KeyNotFoundException("Host CLI update was not found.");
            if (!current.CanCancel)
                throw new TaskServerConflictException(
                    "cli-update-cannot-cancel",
                    "CLI update cannot be cancelled after package activation has started.");
            var now = UtcNow;
            await ExecuteAsync(connection, """
                UPDATE studio_host_cli_updates
                   SET state = $state, updated_at = $now, completed_at = $now, detail = $detail
                 WHERE host_id = $host;
                UPDATE host_admission
                   SET operator_drain_reason = NULL, operator_drain_at = NULL, updated_at = $now
                 WHERE host_id = $host;
                """, ct, transaction,
                ("$state", CliUpdateStates.Cancelled),
                ("$now", Iso(now)),
                ("$detail", "Cancelled by the operator before package activation."),
                ("$host", normalized));
            await AuditAsync(connection, transaction, actorId, "host.cli-update.cancelled", "host", normalized,
                JsonSerializer.Serialize(new { current.RequestedAt }), ct);
        }, ct);
        return (await GetHostCliUpdateAsync(normalized, ct))!;
    }

    public async Task<HostCliUpdateDto?> GetHostCliUpdateAsync(string hostId, CancellationToken ct)
    {
        var normalized = RequireHostId(hostId);
        await using var connection = await OpenReadyAsync(ct);
        return await ReadHostCliUpdateAsync(connection, null, normalized, ct);
    }

    public async Task<HostCliUpdateDto?> GetRunnerCliUpdateAsync(
        string runnerId,
        string instanceId,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var value = await ScalarAsync(connection, """
            SELECT host_id FROM runners WHERE id = $runner AND instance_id = $instance;
            """, ct, null, ("$runner", runnerId), ("$instance", instanceId));
        var hostId = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(hostId)) throw new KeyNotFoundException("Runner was not found.");
        return await ReadHostCliUpdateAsync(connection, null, hostId, ct);
    }

    public async Task<HostCliUpdateDto> RecordRunnerCliUpdateResultAsync(
        string runnerId,
        HostCliUpdateResultRequest request,
        string actorId,
        CancellationToken ct)
    {
        string hostId;
        await using (var connection = await OpenReadyAsync(ct))
        {
            var value = await ScalarAsync(
                connection,
                "SELECT host_id FROM runners WHERE id = $runner;",
                ct,
                null,
                ("$runner", runnerId));
            hostId = Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? throw new KeyNotFoundException("Runner was not found.");
        }
        return await RecordHostCliUpdateResultAsync(hostId, request, actorId, ct);
    }

    public async Task<HostCliUpdateDto> RecordHostCliUpdateResultAsync(
        string hostId,
        HostCliUpdateResultRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        var normalized = RequireHostId(hostId);
        var allowed = new[] { CliUpdateStates.Upgrading, CliUpdateStates.Probing, CliUpdateStates.Succeeded, CliUpdateStates.Failed };
        if (!allowed.Contains(request.State, StringComparer.Ordinal))
            throw new ArgumentException("CLI update result state is invalid.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadHostCliUpdateAsync(connection, transaction, normalized, ct)
                ?? throw new KeyNotFoundException("Host CLI update was not found.");
            if (request.State == CliUpdateStates.Upgrading && current.ActiveSlots > 0)
                throw new TaskServerConflictException("cli-update-host-busy", "Host still has active runs or reviews.");
            var transitionAllowed = request.State switch
            {
                CliUpdateStates.Upgrading => current.State == CliUpdateStates.Ready,
                CliUpdateStates.Probing => current.State == CliUpdateStates.Upgrading,
                CliUpdateStates.Succeeded => current.State == CliUpdateStates.Probing,
                CliUpdateStates.Failed => current.State is CliUpdateStates.Upgrading or CliUpdateStates.Probing,
                _ => false,
            };
            if (!transitionAllowed)
                throw new TaskServerConflictException(
                    "cli-update-invalid-transition",
                    $"CLI update cannot move from '{current.State}' to '{request.State}'.");
            var now = UtcNow;
            var terminal = request.State is CliUpdateStates.Succeeded or CliUpdateStates.Failed;
            await ExecuteAsync(connection, """
                UPDATE studio_host_cli_updates
                   SET state = $state, updated_at = $now, completed_at = $completed, detail = $detail
                 WHERE host_id = $host;
                """, ct, transaction,
                ("$state", request.State), ("$now", Iso(now)),
                ("$completed", terminal ? Iso(now) : null),
                ("$detail", request.Detail), ("$host", normalized));
            if (terminal)
                await ExecuteAsync(connection, """
                    UPDATE host_admission
                       SET operator_drain_reason = NULL, operator_drain_at = NULL, updated_at = $now
                     WHERE host_id = $host;
                    """, ct, transaction, ("$now", Iso(now)), ("$host", normalized));
            await AuditAsync(connection, transaction, actorId, $"host.cli-update.{request.State}", "host", normalized,
                JsonSerializer.Serialize(new { request.Detail }), ct);
        }, ct);
        var result = (await GetHostCliUpdateAsync(normalized, ct))!;
        if (request.State is CliUpdateStates.Succeeded or CliUpdateStates.Failed)
            await PublishOperationalEventsAsync([
                new TaskServerOperationalEvent($"host.cli-update.{request.State}", UtcNow, actorId, result)
            ], ct);
        return result;
    }

    internal async Task<HostCliUpdateDto?> ReadHostCliUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string hostId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT state, codex_target_version, claude_target_version,
                   requested_at, updated_at, completed_at, detail
              FROM studio_host_cli_updates
             WHERE host_id = $host;
            """, transaction, ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var state = reader.GetString(0);
        var codex = reader.GetString(1);
        var claude = reader.GetString(2);
        var requestedAt = Parse(reader.GetString(3));
        var updatedAt = Parse(reader.GetString(4));
        DateTime? completedAt = reader.IsDBNull(5) ? null : Parse(reader.GetString(5));
        var detail = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.DisposeAsync();
        var activeSlots = await ActiveHostSlotsAsync(connection, transaction, hostId, ct);
        if (state == CliUpdateStates.Draining && activeSlots == 0) state = CliUpdateStates.Ready;
        return new HostCliUpdateDto(
            hostId, state, codex, claude,
            requestedAt, updatedAt, activeSlots,
            state is CliUpdateStates.Draining or CliUpdateStates.Ready,
            detail,
            completedAt);
    }

    private static async Task<int> ActiveHostSlotsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string hostId,
        CancellationToken ct)
    {
        var telemetryTotal = 0;
        await using var command = Command(connection, """
            SELECT telemetry.payload_json
              FROM runners runner
              LEFT JOIN runner_telemetry_latest telemetry ON telemetry.runner_id = runner.id
             WHERE runner.host_id = $host;
            """, transaction, ("$host", hostId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            telemetryTotal += JsonSerializer.Deserialize<HostTelemetrySnapshotDto>(reader.GetString(0))?.ActiveSlots ?? 0;
        }
        await reader.DisposeAsync();
        var durableTotal = Convert.ToInt32(await ScalarAsync(connection, """
            SELECT
                (SELECT count(*)
                   FROM leases lease
                   JOIN runners runner ON runner.id = lease.runner_id
                  WHERE runner.host_id = $host
                    AND lease.status IN ('active', 'process-unknown'))
              + (SELECT count(*)
                   FROM review_attempts review
                  WHERE review.host_id = $host
                    AND review.status IN ('leased', 'process-unknown'));
            """, ct, transaction, ("$host", hostId)) ?? 0L, CultureInfo.InvariantCulture);
        return Math.Max(telemetryTotal, durableTotal);
    }

    private static string RequireHostId(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("Host id is required.");
        return hostId.Trim();
    }
}
