using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Recovery;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>Durable facts the recovery manifest and resume gate read from Task Server SQLite stores.</summary>
public sealed partial class TaskServerStore
{
    internal sealed record RecoverySnapshotFacts(
        string ServerId,
        int SchemaVersion,
        IReadOnlyList<RecoveryWorkspaceIdentity> Workspaces,
        IReadOnlyList<RecoveryProjectIdentity> Projects,
        int TaskCount,
        string TaskIdentitySha256,
        IReadOnlyList<RecoveryColdPayload> ColdPayloads,
        IReadOnlyList<RecoveryExternalArtifact> ExternalArtifacts,
        IReadOnlyList<RecoveryRepository> RecordedRepositories,
        IReadOnlyList<RecoveryClientCredential> ActivePrincipals,
        IReadOnlyList<RecoveryHostObligation> Obligations);

    internal string FullBackupSetPath(string backupId) => ResolveFullBackupPath(backupId);

    /// <summary>Reads recovery facts from a set's own <c>snapshot.db</c>, so the manifest matches the set exactly.</summary>
    internal static async Task<RecoverySnapshotFacts> ReadRecoverySnapshotFactsAsync(string setRoot, CancellationToken ct)
    {
        await using var connection = await OpenSnapshotAsync(Path.Combine(setRoot, "snapshot.db"), ct);
        return await ReadRecoveryFactsAsync(connection, setRoot, ct);
    }

    /// <summary>Recovery facts of the live store; used to compare a restored target with its manifest.</summary>
    internal async Task<RecoverySnapshotFacts> ReadLiveRecoveryFactsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        return await ReadRecoveryFactsAsync(connection, setRoot: null, ct);
    }

    /// <param name="fencedAt">Runner credentials issued before this instant are stale (hosts fenced, else restore).</param>
    /// <param name="restoredAt">A client is reconciled by a credential issued after this restore instant.</param>
    internal async Task<RecoveryResumeFacts> ReadRecoveryResumeFactsAsync(
        DateTime fencedAt,
        DateTime restoredAt,
        bool restoredFromRecoverySet,
        bool oldWriterClosed,
        bool obligationsRetained,
        IReadOnlyList<RecoveryFinding> openSetFindings,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var unresolved = Convert.ToInt32(await ScalarAsync(connection, """
            SELECT (SELECT count(*) FROM leases WHERE status IN ('active', 'process-unknown'))
                 + (SELECT count(*) FROM review_attempts WHERE status IN ('leased', 'process-unknown'));
            """, ct) ?? 0L, CultureInfo.InvariantCulture);

        // A runner principal is fenced once every live credential was issued after the restore,
        // or once the principal is revoked. Anything older could be an obsolete host replaying.
        var stale = new List<string>();
        await using (var command = Command(connection, """
            SELECT p.principal_id
              FROM principals p
             WHERE p.kind = 'runner' AND p.revoked_at IS NULL
               AND EXISTS (SELECT 1 FROM principal_credentials c
                            WHERE c.principal_id = p.principal_id AND c.revoked_at IS NULL
                              AND c.created_at < $fenced
                              AND (c.expires_at IS NULL OR c.expires_at > $now))
             ORDER BY p.principal_id;
            """, ("$fenced", Iso(fencedAt)), ("$now", Iso(UtcNow))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) stale.Add(reader.GetString(0));

        // A lost client is reconciled only after a fresh credential exists and every pre-restore
        // credential is revoked or expired. This covers runner re-enrolment and deliberate rotation
        // of other service principals without treating fencing alone as proof of reconnection. A runner
        // re-enrolled before fencing loses that credential to the fence, so it is not reconciled.
        var reconciledClients = new List<string>();
        await using (var command = Command(connection, """
            SELECT p.principal_id
              FROM principals p
             WHERE p.revoked_at IS NULL
               AND EXISTS (SELECT 1 FROM principal_credentials c
                            WHERE c.principal_id = p.principal_id AND c.revoked_at IS NULL
                              AND c.created_at >= $restored
                              AND (c.expires_at IS NULL OR c.expires_at > $now))
               AND NOT EXISTS (SELECT 1 FROM principal_credentials c
                                WHERE c.principal_id = p.principal_id AND c.revoked_at IS NULL
                                  AND c.created_at < $restored
                                  AND (c.expires_at IS NULL OR c.expires_at > $now))
             ORDER BY p.principal_id;
            """, ("$restored", Iso(restoredAt)), ("$now", Iso(UtcNow))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) reconciledClients.Add(reader.GetString(0));

        var obligations = await ReadObligationsAsync(connection, ct);
        return new RecoveryResumeFacts(
            _mode, restoredFromRecoverySet, unresolved, oldWriterClosed, stale, obligations, obligationsRetained, openSetFindings,
            ReconciledClientPrincipals: reconciledClients);
    }

    /// <summary>
    /// Revokes every runner credential issued before the restore, so a host still holding the old
    /// authority's credential cannot replay into the restored one. Runs only in Maintenance.
    /// </summary>
    internal async Task<int> FenceRecoveredRunnerCredentialsAsync(DateTime restoredAt, string actorId, CancellationToken ct)
    {
        RequireRecoveryMaintenance();
        var revoked = 0;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            revoked = await ExecuteAsync(connection, """
                UPDATE principal_credentials
                   SET revoked_at = $now
                 WHERE revoked_at IS NULL
                   AND created_at < $restored
                   AND principal_id IN (SELECT principal_id FROM principals WHERE kind = 'runner');
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$restored", Iso(restoredAt)));
            await AuditAsync(connection, transaction, actorId, "recovery.hosts-fenced", "recovery", _serverId,
                JsonSerializer.Serialize(new { revokedCredentials = revoked, restoredAt }), ct);
        }, ct);
        return revoked;
    }

    /// <summary>Revokes one restored client's old credentials and issues a fresh one while in Maintenance.</summary>
    internal async Task<IssuedPrincipalCredential> ReissueRecoveredClientCredentialAsync(
        string principalId, string actorId, CancellationToken ct)
    {
        RequireRecoveryMaintenance();
        var credential = GenerateCredential();
        var now = UtcNow;
        PrincipalDto? principal = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            principal = await ReadPrincipalAsync(connection, transaction, principalId, ct)
                        ?? throw new KeyNotFoundException("Principal was not found.");
            if (principal.RevokedAt is not null)
                throw new InvalidOperationException("A revoked principal cannot be re-enrolled after recovery.");
            await ExecuteAsync(connection, """
                UPDATE principal_credentials
                   SET revoked_at = $now
                 WHERE principal_id = $id AND revoked_at IS NULL;
                """, ct, transaction, ("$now", Iso(now)), ("$id", principalId));
            await InsertCredentialAsync(connection, transaction, principalId, credential, now, ct);
            await AuditAsync(connection, transaction, actorId, "recovery.client-reenrolled", "principal", principalId, "{}", ct);
        }, ct);
        return new IssuedPrincipalCredential(principal!, credential, now);
    }

    private void RequireRecoveryMaintenance()
    {
        if (!AuthorityReady) throw new InvalidOperationException("Lease and fence authority is not ready.");
        if (_mode != TaskServerMode.Maintenance)
            throw new TaskServerConflictException("maintenance-required", "Recovery fencing runs only in maintenance mode.");
    }

    internal async Task AuditRecoveryAsync(string actorId, string action, string targetId, object detail, CancellationToken ct)
        => await InWriteTransactionAsync(
            async (connection, transaction) => await AuditAsync(
                connection, transaction, actorId, action, "recovery", targetId, JsonSerializer.Serialize(detail), ct),
            ct, requireReady: false);

    private static async Task<RecoverySnapshotFacts> ReadRecoveryFactsAsync(
        SqliteConnection connection, string? setRoot, CancellationToken ct)
    {
        var serverId = Convert.ToString(await ScalarAsync(connection, "SELECT value FROM meta WHERE key = 'server_id';", ct),
            CultureInfo.InvariantCulture) ?? string.Empty;
        var schema = Convert.ToInt32(await ScalarAsync(connection, "SELECT value FROM meta WHERE key = 'schema_version';", ct) ?? 0,
            CultureInfo.InvariantCulture);

        var workspaces = new List<RecoveryWorkspaceIdentity>();
        await using (var command = Command(connection, "SELECT id, name FROM workspaces ORDER BY id;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) workspaces.Add(new(reader.GetString(0), reader.GetString(1)));

        var projects = new List<RecoveryProjectIdentity>();
        await using (var command = Command(connection, """
            SELECT p.id, p.workspace_id, p.name, p.task_key_prefix,
                   (SELECT count(*) FROM tasks t WHERE t.project_id = p.id)
              FROM projects p ORDER BY p.id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                projects.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));

        // Identity digest over task id, key and project, so a restored target proves the same task set.
        var identity = new StringBuilder();
        var taskCount = 0;
        await using (var command = Command(connection, "SELECT id, task_key, project_id FROM tasks ORDER BY id;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                taskCount++;
                identity.Append(reader.GetString(0)).Append(':').Append(reader.GetString(1)).Append(':').Append(reader.GetString(2)).Append('\n');
            }

        var cold = new List<RecoveryColdPayload>();
        if (setRoot is not null)
        {
            var manifestRoot = Path.Combine(setRoot, "manifests");
            if (Directory.Exists(manifestRoot))
                foreach (var path in Directory.EnumerateFiles(manifestRoot, "*.json").Order(StringComparer.Ordinal))
                {
                    var manifest = JsonSerializer.Deserialize<FullBackupArchiveManifest>(await File.ReadAllTextAsync(path, ct), RetentionJson);
                    if (manifest is null) continue;
                    foreach (var stage in manifest.Stages.Where(stage => stage.RelativePayloadPath is not null))
                        cold.Add(new(manifest.TaskId, "cold/" + stage.RelativePayloadPath, stage.PayloadSha256));
                }
        }

        var external = new List<RecoveryExternalArtifact>();
        await using (var command = Command(connection, """
            SELECT id, run_id, name, coalesce(source_path, ''), sha256
              FROM artifacts WHERE pointer_only = 1 ORDER BY id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                external.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));

        // Canonical results the authority accepted are the refs sampled against each origin.
        var repositories = new Dictionary<string, (string? Origin, List<RecoveryRef> Refs)>(StringComparer.Ordinal);
        await using (var command = Command(connection, """
            SELECT repository_id, repository_url, immutable_remote_ref, result_sha
              FROM result_handoffs
             WHERE immutable_remote_ref IS NOT NULL
             ORDER BY acknowledged_at DESC;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (!repositories.TryGetValue(id, out var entry))
                    repositories[id] = entry = (reader.IsDBNull(1) ? null : reader.GetString(1), []);
                if (entry.Refs.Count < SampledRefsPerRepository)
                    entry.Refs.Add(new(reader.GetString(2), reader.GetString(3), "result-handoff"));
            }

        var principals = new List<RecoveryClientCredential>();
        await using (var command = Command(connection, """
            SELECT principal_id, kind, runner_id FROM principals WHERE revoked_at IS NULL ORDER BY principal_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                principals.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    RecoveryCredentialCustody.Undeclared));

        return new RecoverySnapshotFacts(
            serverId,
            schema,
            workspaces,
            projects,
            taskCount,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))),
            cold,
            external,
            repositories.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new RecoveryRepository(item.Key, item.Value.Origin, item.Value.Refs))
                .ToList(),
            principals,
            await ReadObligationsAsync(connection, ct));
    }

    private static async Task<List<RecoveryHostObligation>> ReadObligationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var obligations = new List<RecoveryHostObligation>();
        await using (var command = Command(connection, """
            SELECT runner_id, run_id, final_handoff_state, backlog_count
              FROM runner_outbox_status
             WHERE backlog_count > 0 OR final_handoff_state <> 'acknowledged'
             ORDER BY runner_id, run_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                obligations.Add(new(RecoveryObligationKinds.RunnerOutbox, reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetInt64(3), "Runner outbox not acknowledged by the authority."));

        await using (var command = Command(connection, """
            SELECT runner_id, run_id, source_bundle_digest
              FROM result_handoffs
             WHERE immutable_remote_ref IS NULL AND source_bundle_digest IS NOT NULL
             ORDER BY runner_id, run_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                obligations.Add(new(RecoveryObligationKinds.SalvageBundle, reader.GetString(0), reader.GetString(1),
                    "bundle-only", 0, $"Accepted result exists only as source bundle {reader.GetString(2)}."));
        return obligations;
    }

    private const int SampledRefsPerRepository = 5;
}
