using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    public async Task<CredentialInventoryDto> GetCredentialInventoryAsync(string role, CancellationToken ct)
    {
        // Read only named metadata columns. Verifiers, session tokens and code hashes
        // never enter this projection, including for an owner session.
        var now = UtcNow;
        var view = await GetCredentialViewAsync(role, ct);
        var records = await ListCredentialRegistryAsync(ct);
        var rows = view.Rows.ToDictionary(row => (row.InstallationId, row.HostId, row.CredentialId));
        var items = new List<CredentialInventoryItemDto>();
        foreach (var record in records)
        {
            var row = rows[(record.InstallationId, record.HostId, record.CredentialId)];
            var repository = record.Scopes.Select(scope => scope.StartsWith(
                    ReviewRepositoryAccessPolicy.ReadCapabilityPrefix, StringComparison.Ordinal)
                    ? scope[ReviewRepositoryAccessPolicy.ReadCapabilityPrefix.Length..]
                    : ReviewRepositoryAccessPolicy.CanonicalRepository(scope))
                .FirstOrDefault(value => value is not null);
            var category = record.Kind.StartsWith("github_", StringComparison.Ordinal) ? "repository"
                : record.Kind == "task_server_principal" ? "service" : "provider";
            var state = record.Locator.EffectiveSource == "absent" ? "missing"
                : row.Due.ThresholdDays is not null ? "expiring"
                : row.LastRealSuccessAt is null ? "unused" : "ok";
            items.Add(new($"registry:{record.HostId}:{record.CredentialId}", category, record.HostId,
                record.Kind, record.AccountRef ?? record.Provider,
                record.Scopes.Concat(record.Bindings.Select(binding =>
                    $"{binding.ServiceId}:{binding.Purpose}")).Distinct(StringComparer.Ordinal).ToArray(), repository,
                category == "repository" ? record.RepositoryWriteGrant switch
                { true => "write", false => "read", null => "unknown" } : null,
                record.ExpiresAt, record.LastRealSuccessAt,
                state, false, row.Due.ThresholdDays is { } threshold
                    ? $"{threshold}-day {row.Due.Subject} warning" : row.StatusDetail ?? row.Status, []));
        }

        await using var connection = await OpenReadyAsync(ct);
        await using (var command = Command(connection, """
            SELECT p.principal_id, p.kind, p.scopes_json, p.runner_id, p.last_seen_at,
                   p.revoked_at, c.credential_id, c.expires_at, c.revoked_at
              FROM principals p JOIN principal_credentials c ON c.principal_id = p.principal_id
             ORDER BY p.principal_id, c.created_at;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        while (await reader.ReadAsync(ct))
        {
            var expires = reader.IsDBNull(7) ? (DateTime?)null : Parse(reader.GetString(7));
            var revoked = !reader.IsDBNull(5) || !reader.IsDBNull(8);
            var lastUse = reader.IsDBNull(4) ? (DateTime?)null : Parse(reader.GetString(4));
            items.Add(new($"principal:{reader.GetString(6)}", "service",
                reader.IsDBNull(3) ? "control" : reader.GetString(3), reader.GetString(1),
                reader.GetString(0), JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [],
                null, null, expires, lastUse,
                revoked ? "unused" : expires is { } date && date <= now.AddDays(14) ? "expiring"
                    : lastUse is null ? "unused" : "ok", false,
                revoked ? "Revoked" : ExpiryDetail(expires, now, "Task Server principal"), []));
        }
        await using (var command = Command(connection, """
            SELECT e.enrolment_id, e.principal_id, e.kind, e.runner_id, e.expires_at,
                   e.consumed_at, e.revoked_at FROM service_enrolments e ORDER BY e.created_at;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        while (await reader.ReadAsync(ct))
        {
            var expires = Parse(reader.GetString(4));
            var consumed = !reader.IsDBNull(5);
            var revoked = !reader.IsDBNull(6);
            items.Add(new($"enrolment:{reader.GetString(0)}", "service",
                reader.IsDBNull(3) ? "control" : reader.GetString(3), "enrolment_code",
                reader.GetString(1), [], null, null, expires,
                consumed ? Parse(reader.GetString(5)) : null,
                consumed || revoked ? "unused" : expires <= now.AddDays(14) ? "expiring" : "ok",
                false, consumed ? "Consumed" : revoked ? "Revoked"
                    : ExpiryDetail(expires, now, "Pending enrolment"), []));
        }
        await using (var command = Command(connection, """
            SELECT u.id, u.display_name, u.role, u.disabled, MAX(s.created_at)
              FROM studio_users u LEFT JOIN studio_sessions s ON s.user_id = u.id
             GROUP BY u.id ORDER BY u.display_name;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        while (await reader.ReadAsync(ct))
            items.Add(new($"human:{reader.GetString(0)}", "human", "control", "studio_user",
                reader.GetString(1), [reader.GetString(2)], null, null, null,
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
                reader.GetInt32(3) != 0 || reader.IsDBNull(4) ? "unused" : "ok", false,
                reader.GetInt32(3) != 0 ? "Disabled" : "Studio account", []));

        var hosts = new Dictionary<string, (HashSet<string> Roles, HashSet<string> Ready)>();
        await using (var command = Command(connection, """
            SELECT r.host_id, r.capabilities_json, c.capability_key, c.advertised_status, c.fresh_until
              FROM runners r LEFT JOIN runner_capabilities c ON c.runner_id = r.id
             WHERE r.status = 'active';
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        while (await reader.ReadAsync(ct))
        {
            var hostId = reader.GetString(0);
            if (!hosts.TryGetValue(hostId, out var host))
                hosts[hostId] = host = (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            host.Roles.UnionWith(JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? []);
            if (!reader.IsDBNull(2) && reader.GetString(3) == "ready" &&
                !reader.IsDBNull(4) && Parse(reader.GetString(4)) > now)
                host.Ready.Add(reader.GetString(2));
        }
        var privateReviews = new List<CredentialInventoryPolicy.PrivateReview>();
        await using (var command = Command(connection, """
            SELECT s.repository_url, s.plan_json,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM review_attempts a
                        WHERE a.subject_id = s.id AND a.cleaned_at IS NULL)
                       THEN t.task_key ELSE '' END
              FROM review_subjects s JOIN tasks t ON t.id = s.task_id
             WHERE s.repository_url IS NOT NULL;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        while (await reader.ReadAsync(ct))
        {
            using var plan = JsonDocument.Parse(reader.GetString(1));
            if (!plan.RootElement.TryGetProperty("privateRepository", out var privateFlag) ||
                privateFlag.ValueKind != JsonValueKind.True) continue;
            if (ReviewRepositoryAccessPolicy.CanonicalRepository(reader.GetString(0)) is { } repository)
                privateReviews.Add(new(repository, reader.GetString(2)));
        }
        var repositories = new List<CredentialInventoryPolicy.Repository>();
        await using (var command = Command(connection, """
            SELECT repository_url, integration_ref, release_ref, repository_visibility
              FROM project_repositories;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        while (await reader.ReadAsync(ct))
            if (ReviewRepositoryAccessPolicy.CanonicalRepository(reader.GetString(0)) is { } repository)
                repositories.Add(new(repository, reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    !reader.IsDBNull(3) && ReviewRepositoryAccessPolicy.IsPrivate(reader.GetString(3))));
        var derived = CredentialInventoryPolicy.Derive(items,
            hosts.Select(pair => new CredentialInventoryPolicy.Host(pair.Key, pair.Value.Roles,
                pair.Value.Ready)).ToArray(), privateReviews, repositories, now).ToList();
        // This reads the same run and lease authority as the Environment projection.
        // Select only issuance metadata; the capability hash and issued token are excluded.
        await using (var command = Command(connection, """
            SELECT i.issuance_id, i.host_id, i.role, i.subject_id, i.run_id,
                   i.lease_id, i.fence, i.issuer, i.storage_mode, i.issued_at,
                   i.expires_at, i.revoked_at, i.revocation_status, i.deletion_status,
                   p.repository_url, t.task_key, l.status, l.expires_at
              FROM attempt_credential_issuances i
              JOIN leases l ON l.run_id = i.run_id AND l.lease_id = i.lease_id
              JOIN tasks t ON t.id = i.subject_id
              LEFT JOIN project_repositories p ON p.repository_id = i.repository_id
             ORDER BY i.capability_expires_at DESC LIMIT 200;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        while (await reader.ReadAsync(ct))
        {
            var expires = reader.IsDBNull(10) ? (DateTime?)null : Parse(reader.GetString(10));
            var revoked = !reader.IsDBNull(11);
            var leaseStale = reader.GetString(16) != "active" || Parse(reader.GetString(17)) <= now;
            var state = revoked ? "unused" : leaseStale || expires is { } deadline && deadline <= now
                ? "expiring" : reader.IsDBNull(9) ? "unused" : "ok";
            var repository = reader.IsDBNull(14) ? null
                : ReviewRepositoryAccessPolicy.CanonicalRepository(reader.GetString(14));
            var binding = $"{reader.GetString(15)} · attempt {reader.GetString(4)} · lease {reader.GetString(5)} · fence {reader.GetInt64(6)} · {reader.GetString(2)}";
            var revocation = reader.IsDBNull(12) ? "pending" : reader.GetString(12);
            var deletion = reader.IsDBNull(13) ? "pending" : reader.GetString(13);
            derived.Add(new($"attempt:{reader.GetString(0)}", "repository", reader.GetString(1),
                "attempt_repository_token", "Attempt repository access", [reader.GetString(2), "repository:read"],
                repository, "read", expires,
                reader.IsDBNull(9) ? null : Parse(reader.GetString(9)), state, false,
                revoked ? "Attempt credential settled" : leaseStale ? "Lease is stale" : "Attempt bound",
                [reader.GetString(15)], reader.GetString(7), binding, reader.GetString(8),
                $"{revocation} · file {deletion}"));
        }
        return new(now, derived);
    }

    private static string ExpiryDetail(DateTime? expires, DateTime now, string otherwise)
        => expires is { } date && CredentialReminderPolicy.Threshold(date - now) is { } threshold
            ? $"{threshold}-day expiry warning" : otherwise;
}
