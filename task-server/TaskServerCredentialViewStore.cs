using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// I8 credential view and generation-keyed reminders (docs/credentials-and-logins/index.html,
/// D4 option A). Reminders are reconciled when metadata changes, when a renewal completes,
/// on an hourly sweep; each pass is idempotent. The view itself is read-only.
/// </summary>
public sealed partial class TaskServerStore
{
    private const string ReminderActor = "credential-reminders";

    private static async Task ApplyCredentialReminderMigrationAsync(SqliteConnection connection, CancellationToken ct)
        => await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS credential_reminder_items(
                installation_id TEXT NOT NULL,
                host_id TEXT NOT NULL,
                credential_id TEXT NOT NULL,
                credential_generation TEXT NOT NULL,
                subject TEXT NOT NULL,
                due_at TEXT NOT NULL,
                threshold_days INTEGER NOT NULL,
                opened_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                closed_at TEXT,
                close_reason TEXT,
                PRIMARY KEY(installation_id, host_id, credential_id, credential_generation)
            );
            """, ct);

    public async Task ReconcileCredentialRemindersAsync(CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync((connection, transaction) =>
            ReconcileCredentialRemindersAsync(connection, transaction, ct), ct);
    }

    public async Task<IReadOnlyList<CredentialReminderDto>> ListCredentialRemindersAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadCredentialRemindersAsync(connection, null, ct);
    }

    public async Task<CredentialViewDto> GetCredentialViewAsync(string role, CancellationToken ct)
    {
        role = CredentialActionPolicy.NormalizeRole(role);
        await using var connection = await OpenReadyAsync(ct);
        var now = UtcNow;
        var registry = await ReadRegistryWithObservationAsync(connection, null, ct);
        var reminders = (await ReadCredentialRemindersAsync(connection, null, ct))
            .Where(item => item.ClosedAt is null)
            .ToDictionary(item => (item.InstallationId, item.HostId, item.CredentialId, item.Generation));
        var circuits = new Dictionary<string, CredentialStatusPolicy.Circuit>(StringComparer.Ordinal);
        await using (var command = Command(connection,
            "SELECT cohort_key, outcome, next_retry_at FROM provider_health_circuits;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                circuits[reader.GetString(0)] = new(reader.GetString(1),
                    reader.IsDBNull(2) ? null : Parse(reader.GetString(2)));
        var units = new List<(string Host, string Provider, CredentialStatusPolicy.Unit Unit)>();
        await using (var command = Command(connection, """
            SELECT r.host_id, c.capability_key, c.health_outcome, c.service_availability,
                   c.credential_generation, c.limited_until
              FROM runner_capabilities c JOIN runners r ON r.id = c.runner_id
             WHERE c.capability_key LIKE 'provider-auth:%';
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                units.Add((reader.GetString(0), reader.GetString(1)["provider-auth:".Length..],
                    new(reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : Parse(reader.GetString(5)))));
        var healthItems = new List<(string Cohort, string Generation, CredentialHealthItemRefDto Item)>();
        await using (var command = Command(connection, """
            SELECT cohort_key, credential_generation, kind, runbook_id, opened_at
              FROM provider_health_items WHERE closed_at IS NULL ORDER BY opened_at;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                healthItems.Add((reader.GetString(0), reader.GetString(1), new(reader.GetString(2),
                    Parse(reader.GetString(4)),
                    CredentialActionPolicy.SafeRunbookHref(reader.IsDBNull(3) ? null : reader.GetString(3)))));

        var mayReadLocators = CredentialActionPolicy.MayReadLocators(role);
        var rows = new List<CredentialViewRowDto>();
        foreach (var (record, observedAt) in registry)
        {
            var bindingKeys = new[] { $"binding:{record.HostId}:{record.CredentialId}", $"binding:{record.HostId}:{record.Provider}" };
            var binding = bindingKeys.Select(key => circuits.GetValueOrDefault(key)).FirstOrDefault(circuit => circuit is not null);
            var status = CredentialStatusPolicy.Classify(record, binding,
                circuits.GetValueOrDefault(ServiceCohort(record.Provider)),
                units.Where(unit => unit.Host == record.HostId && unit.Provider == record.Provider)
                    .Select(unit => unit.Unit).ToArray());
            var reminder = reminders.GetValueOrDefault((record.InstallationId, record.HostId, record.CredentialId, record.Generation));
            var healthItem = healthItems.LastOrDefault(item => bindingKeys.Contains(item.Cohort)
                && (item.Generation == record.Generation || item.Generation == "unknown")).Item;
            var runbookHref = CredentialActionPolicy.SafeRunbookHref(record.RunbookId) ?? healthItem?.RunbookHref
                ?? (record.RenewalMethod is "R1" or "R2" or "R3" or "R8" ? "docs/operations/setup/cli-relogin-runbook.md" : null);
            rows.Add(new(record.InstallationId, record.HostId, record.CredentialId, record.Kind, record.Provider,
                record.RenewalMethod, record.Locator.Adapter, record.Locator.EffectiveSource,
                mayReadLocators ? record.Locator.LocalRef : null, record.Scopes,
                record.Bindings.Select(item => item.ServiceId).Distinct(StringComparer.Ordinal).ToArray(),
                record.Owner, record.Generation, record.Supersedes, record.LastVerifiedAt, record.LastRealSuccessAt,
                observedAt, status.Status, status.Detail, status.NextRetryAt, status.ServiceAvailability,
                CredentialReminderPolicy.EvidenceQuality(record, observedAt, now),
                CredentialReminderPolicy.Assess(record, now), reminder, healthItem, runbookHref,
                mayReadLocators ? record.EvidenceRefs : [],
                CredentialActionPolicy.For(role, record, status.Status, reminder is not null, runbookHref)));
        }
        return new(role, now, rows);
    }

    private async Task ReconcileCredentialRemindersAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken ct)
    {
        var now = UtcNow;
        var registry = await ReadRegistryWithObservationAsync(connection, transaction, ct);
        var items = await ReadCredentialRemindersAsync(connection, transaction, ct);
        var current = registry.ToDictionary(entry => (entry.Record.InstallationId, entry.Record.HostId, entry.Record.CredentialId));
        foreach (var item in items.Where(item => item.ClosedAt is null))
        {
            var key = (item.InstallationId, item.HostId, item.CredentialId);
            if (!current.TryGetValue(key, out var entry))
                await CloseReminderAsync(connection, transaction, item, "removed", ct);
            else if (entry.Record.Generation != item.Generation)
                // A successful renewal or rotation advanced the generation.
                await CloseReminderAsync(connection, transaction, item, "renewed", ct);
        }
        foreach (var (record, _) in registry)
        {
            var existing = items.FirstOrDefault(item => item.InstallationId == record.InstallationId
                && item.HostId == record.HostId && item.CredentialId == record.CredentialId
                && item.Generation == record.Generation);
            var due = CredentialReminderPolicy.Assess(record, now);
            var decision = CredentialReminderPolicy.Decide(due, existing is null ? null
                : new(existing.DueAt, existing.ThresholdDays, existing.ClosedAt is not null));
            switch (decision.Action)
            {
                case CredentialReminderPolicy.ReminderAction.Close:
                    await CloseReminderAsync(connection, transaction, existing!, "not-due", ct);
                    break;
                case CredentialReminderPolicy.ReminderAction.Open:
                case CredentialReminderPolicy.ReminderAction.Escalate:
                case CredentialReminderPolicy.ReminderAction.Reschedule:
                    await ExecuteAsync(connection, """
                        INSERT INTO credential_reminder_items(installation_id, host_id, credential_id,
                            credential_generation, subject, due_at, threshold_days, opened_at, updated_at)
                        VALUES ($installation, $host, $credential, $generation, $subject, $due, $threshold, $now, $now)
                        ON CONFLICT(installation_id, host_id, credential_id, credential_generation) DO UPDATE SET
                            subject = excluded.subject, due_at = excluded.due_at,
                            threshold_days = excluded.threshold_days, updated_at = excluded.updated_at,
                            opened_at = CASE WHEN closed_at IS NULL THEN opened_at ELSE excluded.opened_at END,
                            closed_at = NULL, close_reason = NULL;
                        """, ct, transaction, ("$installation", record.InstallationId), ("$host", record.HostId),
                        ("$credential", record.CredentialId), ("$generation", record.Generation),
                        ("$subject", due.Subject), ("$due", Iso(decision.DueAt!.Value)),
                        ("$threshold", decision.ThresholdDays), ("$now", Iso(now)));
                    await AuditAsync(connection, transaction, ReminderActor,
                        "credential.reminder-" + decision.Action switch
                        {
                            CredentialReminderPolicy.ReminderAction.Open => "opened",
                            CredentialReminderPolicy.ReminderAction.Escalate => "escalated",
                            _ => "rescheduled",
                        }, "credential", record.CredentialId, JsonSerializer.Serialize(new
                        {
                            record.InstallationId, record.HostId, record.Generation, due.Subject,
                            dueAt = decision.DueAt, thresholdDays = decision.ThresholdDays,
                        }), ct);
                    break;
            }
        }
    }

    private async Task CloseReminderAsync(SqliteConnection connection, SqliteTransaction transaction,
        CredentialReminderDto item, string reason, CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            UPDATE credential_reminder_items SET closed_at = $now, close_reason = $reason, updated_at = $now
             WHERE installation_id = $installation AND host_id = $host AND credential_id = $credential
               AND credential_generation = $generation AND closed_at IS NULL;
            """, ct, transaction, ("$now", Iso(UtcNow)), ("$reason", reason),
            ("$installation", item.InstallationId), ("$host", item.HostId),
            ("$credential", item.CredentialId), ("$generation", item.Generation));
        await AuditAsync(connection, transaction, ReminderActor, "credential.reminder-closed", "credential",
            item.CredentialId, JsonSerializer.Serialize(new { item.InstallationId, item.HostId, item.Generation, reason }), ct);
    }

    private async Task<List<(CredentialRegistryRecordDto Record, DateTime ObservedAt)>> ReadRegistryWithObservationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var result = new List<(CredentialRegistryRecordDto, DateTime)>();
        await using var command = Command(connection, """
            SELECT payload_json, observed_at FROM credential_registry
             ORDER BY host_id, installation_id, credential_id;
            """, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((JsonSerializer.Deserialize<CredentialRegistryRecordDto>(reader.GetString(0), CredentialRegistryJson)!,
                Parse(reader.GetString(1))));
        return result;
    }

    private async Task<List<CredentialReminderDto>> ReadCredentialRemindersAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var result = new List<CredentialReminderDto>();
        await using var command = Command(connection, """
            SELECT installation_id, host_id, credential_id, credential_generation, subject, due_at,
                   threshold_days, opened_at, updated_at, closed_at, close_reason
              FROM credential_reminder_items ORDER BY opened_at, host_id, credential_id;
            """, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Parse(reader.GetString(5)), reader.GetInt32(6), Parse(reader.GetString(7)),
                Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        return result;
    }
}
