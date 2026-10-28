using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.AttemptCredentials;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed record AttemptCredentialIdentity(string HostId, string Role, string SubjectId,
    string AttemptId, string RunnerId, string InstanceId, string LeaseId, long Fence, long Epoch,
    string StorageMode = "linux-tmpfs");
public sealed record AttemptCredentialCapability(string IssuanceId, string Capability, DateTime ExpiresAtUtc);
public sealed record AttemptCredentialMaterial(string IssuanceId, string Token, string RepositoryId,
    DateTime ExpiresAtUtc, string StorageMode);
public sealed record AttemptCredentialReceipt(string IssuanceId, string RevocationStatus,
    string DeletionStatus, DateTime RecordedAtUtc);
public sealed record AttemptCredentialExchangeRequest(AttemptCredentialIdentity Identity, string IssuanceId, string Capability);
public sealed record AttemptCredentialLifecycleRequest(AttemptCredentialIdentity Identity, string IssuanceId);
public sealed record AttemptCredentialDeletionRequest(AttemptCredentialIdentity Identity, string IssuanceId, string DeletionStatus);

public sealed partial class TaskServerStore
{
    private static string HashCapability(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void RequireCredentialCanary()
    {
        if (!_options.AttemptCredentialCanaryEnabled)
            throw new TaskServerConflictException("credential-canary-off", "Attempt credential exchange is disabled.");
        RequireWritable();
    }

    private async Task<(string RepositoryId, string RepositoryUrl)> ValidateCredentialIdentityAsync(
        SqliteConnection connection, SqliteTransaction transaction, AttemptCredentialIdentity identity,
        CancellationToken ct)
    {
        if (identity.Role != "coding" || identity.Epoch != identity.Fence || identity.Fence <= 0
            || string.IsNullOrWhiteSpace(identity.HostId) || string.IsNullOrWhiteSpace(identity.SubjectId)
            || identity.StorageMode is not ("linux-tmpfs" or "windows-acl-encrypted-volume"))
            throw new ArgumentException("A claimed coding work identity with matching epoch and fence is required.");
        var lease = await ReadLeaseAsync(connection, transaction, identity.AttemptId, ct)
            ?? throw new TaskServerConflictException("stale-fence", "Attempt lease does not exist.");
        ValidateLeaseReference(lease, identity.RunnerId, identity.InstanceId, identity.LeaseId, identity.Fence);
        await EnsureLeaseCurrentAsync(connection, transaction, lease, ct);
        if (lease.TaskId != identity.SubjectId)
            throw new TaskServerConflictException("stale-fence", "Attempt subject does not match the claim.");
        var host = Convert.ToString(await ScalarAsync(connection,
            "SELECT host_id FROM runners WHERE id = $runner AND instance_id = $instance AND status = 'active';",
            ct, transaction, ("$runner", identity.RunnerId), ("$instance", identity.InstanceId)), CultureInfo.InvariantCulture);
        if (host != identity.HostId)
            throw new TaskServerConflictException("stale-fence", "Host does not own this runner claim.");
        await using var command = Command(connection, """
            SELECT p.repository_id, p.repository_url
              FROM tasks t JOIN project_repositories p ON p.project_id = t.project_id
             WHERE t.id = $task;
            """, transaction, ("$task", lease.TaskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new TaskServerConflictException("repository-unregistered", "Claimed task has no registered repository.");
        return (reader.GetString(0), reader.GetString(1));
    }

    public async Task<AttemptCredentialCapability> CreateAttemptCredentialCapabilityAsync(
        AttemptCredentialIdentity identity, CancellationToken ct)
    {
        RequireCredentialCanary();
        var capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var issuanceId = "aci_" + Guid.NewGuid().ToString("N");
        var expires = UtcNow.AddMinutes(2);
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var (repositoryId, _) = await ValidateCredentialIdentityAsync(connection, transaction, identity, ct);
            if (await ScalarAsync(connection, """
                SELECT 1 FROM attempt_credential_issuances
                 WHERE run_id = $run AND lease_id = $lease AND fence = $fence
                   AND revoked_at IS NULL
                   AND (issued_at IS NOT NULL OR capability_expires_at > $now);
                """, ct, transaction, ("$run", identity.AttemptId),
                ("$lease", identity.LeaseId), ("$fence", identity.Fence), ("$now", Iso(UtcNow))) is not null)
                throw new TaskServerConflictException("credential-already-active", "An attempt credential is already active.");
            await ExecuteAsync(connection, """
                INSERT INTO attempt_credential_issuances(
                    issuance_id, run_id, lease_id, fence, epoch, host_id, role, subject_id,
                    repository_id, capability_hash, capability_expires_at, storage_mode, issuer)
                VALUES ($id, $run, $lease, $fence, $epoch, $host, $role, $subject,
                    $repository, $hash, $expires, $storage, 'github-app');
                """, ct, transaction,
                ("$id", issuanceId), ("$run", identity.AttemptId), ("$lease", identity.LeaseId),
                ("$fence", identity.Fence), ("$epoch", identity.Epoch), ("$host", identity.HostId),
                ("$role", identity.Role), ("$subject", identity.SubjectId), ("$repository", repositoryId),
                ("$hash", HashCapability(capability)), ("$expires", Iso(expires)),
                ("$storage", identity.StorageMode));
        }, ct);
        return new AttemptCredentialCapability(issuanceId, capability, expires);
    }

    public async Task<AttemptCredentialMaterial> ExchangeAttemptCredentialAsync(
        AttemptCredentialIdentity identity, string issuanceId, string capability,
        IRepositoryTokenIssuer issuer, IAttemptRootSecretStore roots, CancellationToken ct)
    {
        RequireCredentialCanary();
        string repositoryId = "", repositoryUrl = "";
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            (repositoryId, repositoryUrl) = await ValidateCredentialIdentityAsync(connection, transaction, identity, ct);
            var changed = await ExecuteAsync(connection, """
                UPDATE attempt_credential_issuances SET exchanged_at = $now
                 WHERE issuance_id = $id AND run_id = $run AND lease_id = $lease
                   AND fence = $fence AND epoch = $epoch AND host_id = $host
                   AND role = $role AND subject_id = $subject AND repository_id = $repository
                   AND capability_hash = $hash AND capability_expires_at > $now
                   AND exchanged_at IS NULL AND revoked_at IS NULL;
                """, ct, transaction,
                ("$now", Iso(UtcNow)), ("$id", issuanceId), ("$run", identity.AttemptId),
                ("$lease", identity.LeaseId), ("$fence", identity.Fence), ("$epoch", identity.Epoch),
                ("$host", identity.HostId), ("$role", identity.Role), ("$subject", identity.SubjectId),
                ("$repository", repositoryId), ("$hash", HashCapability(capability)));
            if (changed != 1)
                throw new TaskServerConflictException("capability-consumed", "Credential capability is invalid, expired, or used.");
        }, ct);
        IssuedRepositoryToken token;
        try { token = await issuer.IssueReadAsync(repositoryId, repositoryUrl, ct); }
        catch
        {
            await InWriteTransactionAsync(async (connection, transaction) =>
                await ExecuteAsync(connection, """
                    UPDATE attempt_credential_issuances
                       SET revoked_at = $now, revocation_status = 'not-issued',
                           deletion_status = 'no-file'
                     WHERE issuance_id = $id AND issued_at IS NULL;
                    """, CancellationToken.None, transaction,
                    ("$now", Iso(UtcNow)), ("$id", issuanceId)), CancellationToken.None);
            throw;
        }
        try
        {
            if (token.ExpiresAtUtc > UtcNow.AddHours(1))
                throw new InvalidDataException("Issuer expiry exceeds the one-hour backstop.");
            await roots.WriteAsync("issued-" + issuanceId, token.Value, ct);
            await InWriteTransactionAsync(async (connection, transaction) =>
            {
                await ValidateCredentialIdentityAsync(connection, transaction, identity, ct);
                await ExecuteAsync(connection, """
                    UPDATE attempt_credential_issuances
                       SET issued_at = $now, expires_at = $expires,
                           lease_expiry_at = (SELECT expires_at FROM leases WHERE run_id = $run)
                     WHERE issuance_id = $id AND revoked_at IS NULL;
                    """, ct, transaction, ("$now", Iso(UtcNow)),
                    ("$expires", Iso(token.ExpiresAtUtc)), ("$id", issuanceId),
                    ("$run", identity.AttemptId));
            }, ct);
        }
        catch
        {
            var revoked = false;
            try { revoked = await issuer.RevokeAsync(token.Value, CancellationToken.None); }
            catch (Exception) { revoked = false; }
            var rootDeleted = true;
            try { await roots.DeleteAsync("issued-" + issuanceId, CancellationToken.None); }
            catch (Exception) { rootDeleted = false; }
            await InWriteTransactionAsync(async (connection, transaction) =>
                await ExecuteAsync(connection, """
                    UPDATE attempt_credential_issuances
                       SET revoked_at = $now, revocation_status = $status,
                           deletion_status = $deletion
                     WHERE issuance_id = $id;
                    """, CancellationToken.None, transaction, ("$now", Iso(UtcNow)),
                    ("$status", revoked ? "revoked" : "expiry-backstop"),
                    ("$deletion", rootDeleted ? "no-host-file" : "root-delete-pending"),
                    ("$id", issuanceId)), CancellationToken.None);
            throw;
        }
        return new AttemptCredentialMaterial(issuanceId, token.Value, repositoryId,
            token.ExpiresAtUtc, identity.StorageMode);
    }

    public async Task<AttemptCredentialReceipt> RevokeAttemptCredentialAsync(
        AttemptCredentialIdentity identity, string issuanceId, IRepositoryTokenIssuer issuer,
        IAttemptRootSecretStore roots, CancellationToken ct)
    {
        RequireWritable();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var changed = await ExecuteAsync(connection, """
                UPDATE attempt_credential_issuances SET revoked_at = $now
                 WHERE issuance_id = $id AND run_id = $run AND lease_id = $lease
                   AND fence = $fence AND epoch = $epoch AND host_id = $host
                   AND role = $role AND subject_id = $subject AND revoked_at IS NULL;
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$id", issuanceId),
                ("$run", identity.AttemptId), ("$lease", identity.LeaseId),
                ("$fence", identity.Fence), ("$epoch", identity.Epoch),
                ("$host", identity.HostId), ("$role", identity.Role),
                ("$subject", identity.SubjectId));
            if (changed != 1)
                throw new TaskServerConflictException("credential-already-closed", "Attempt credential is not active for this identity.");
        }, ct);
        var token = await roots.ReadAsync("issued-" + issuanceId, CancellationToken.None);
        bool revoked;
        try { revoked = token is not null && await issuer.RevokeAsync(token, CancellationToken.None); }
        catch (Exception) { revoked = false; }
        var status = revoked ? "revoked" : "expiry-backstop";
        await roots.DeleteAsync("issued-" + issuanceId, CancellationToken.None);
        await InWriteTransactionAsync(async (connection, transaction) =>
            await ExecuteAsync(connection, """
                UPDATE attempt_credential_issuances
                   SET revocation_status = $status, deletion_status = 'host-receipt-pending'
                 WHERE issuance_id = $id;
                """, CancellationToken.None, transaction, ("$status", status), ("$id", issuanceId)), CancellationToken.None);
        return new AttemptCredentialReceipt(issuanceId, status, "host-receipt-pending", UtcNow);
    }

    public async Task<AttemptCredentialReceipt> RecordAttemptCredentialDeletionAsync(
        AttemptCredentialDeletionRequest request, CancellationToken ct)
    {
        RequireWritable();
        if (request.DeletionStatus is not ("container-tmpfs-removed" or "windows-file-deleted"))
            throw new ArgumentException("A verified credential deletion status is required.");
        var identity = request.Identity;
        var changed = 0;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            changed = await ExecuteAsync(connection, """
                UPDATE attempt_credential_issuances SET deletion_status = $status
                 WHERE issuance_id = $id AND run_id = $run AND lease_id = $lease
                   AND fence = $fence AND epoch = $epoch AND host_id = $host
                   AND role = $role AND subject_id = $subject AND revoked_at IS NOT NULL;
                """, ct, transaction, ("$status", request.DeletionStatus),
                ("$id", request.IssuanceId), ("$run", identity.AttemptId),
                ("$lease", identity.LeaseId), ("$fence", identity.Fence),
                ("$epoch", identity.Epoch), ("$host", identity.HostId),
                ("$role", identity.Role), ("$subject", identity.SubjectId));
        }, ct);
        if (changed != 1)
            throw new TaskServerConflictException("deletion-receipt-refused", "Credential was not revoked for this attempt identity.");
        return new AttemptCredentialReceipt(request.IssuanceId, "revocation-recorded", request.DeletionStatus, UtcNow);
    }

    public async Task<AttemptCredentialMaterial> RenewAttemptCredentialAsync(
        AttemptCredentialIdentity identity, string issuanceId, IRepositoryTokenIssuer issuer,
        IAttemptRootSecretStore roots, CancellationToken ct)
    {
        RequireCredentialCanary();
        string repositoryId = "", repositoryUrl = "";
        DateTime previousLeaseExpiry = default;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            (repositoryId, repositoryUrl) = await ValidateCredentialIdentityAsync(connection, transaction, identity, ct);
            var old = Convert.ToString(await ScalarAsync(connection, """
                SELECT lease_expiry_at FROM attempt_credential_issuances
                 WHERE issuance_id = $id AND run_id = $run AND lease_id = $lease
                   AND fence = $fence AND epoch = $epoch AND host_id = $host
                   AND role = $role AND subject_id = $subject AND repository_id = $repository
                   AND issued_at IS NOT NULL AND revoked_at IS NULL;
                """, ct, transaction, ("$id", issuanceId), ("$run", identity.AttemptId),
                ("$lease", identity.LeaseId), ("$fence", identity.Fence), ("$epoch", identity.Epoch),
                ("$host", identity.HostId), ("$role", identity.Role),
                ("$subject", identity.SubjectId), ("$repository", repositoryId)), CultureInfo.InvariantCulture);
            if (old is null)
                throw new TaskServerConflictException("credential-not-issued", "Attempt credential is not active.");
            previousLeaseExpiry = Parse(old);
            var lease = await ReadLeaseAsync(connection, transaction, identity.AttemptId, ct);
            if (lease!.ExpiresAt <= previousLeaseExpiry)
                throw new TaskServerConflictException("lease-not-renewed", "Renew the attempt lease before refreshing the credential.");
        }, ct);
        var token = await issuer.IssueReadAsync(repositoryId, repositoryUrl, ct);
        try
        {
            if (token.ExpiresAtUtc > UtcNow.AddHours(1))
                throw new InvalidDataException("Issuer expiry exceeds the one-hour backstop.");
            await InWriteTransactionAsync(async (connection, transaction) =>
            {
                await ValidateCredentialIdentityAsync(connection, transaction, identity, ct);
                var changed = await ExecuteAsync(connection, """
                    UPDATE attempt_credential_issuances
                       SET issued_at = $now, expires_at = $expires, lease_expiry_at = $leaseExpiry
                     WHERE issuance_id = $id AND revoked_at IS NULL AND lease_expiry_at = $previous;
                    """, ct, transaction, ("$now", Iso(UtcNow)),
                    ("$expires", Iso(token.ExpiresAtUtc)), ("$leaseExpiry", Iso((await ReadLeaseAsync(connection, transaction, identity.AttemptId, ct))!.ExpiresAt)),
                    ("$id", issuanceId), ("$previous", Iso(previousLeaseExpiry)));
                if (changed != 1)
                    throw new TaskServerConflictException("credential-renewal-race", "Another renewal changed this issuance.");
            }, ct);
            var oldToken = await roots.ReadAsync("issued-" + issuanceId, ct);
            await roots.WriteAsync("issued-" + issuanceId, token.Value, ct);
            if (oldToken is not null) await issuer.RevokeAsync(oldToken, ct);
        }
        catch
        {
            await issuer.RevokeAsync(token.Value, CancellationToken.None);
            throw;
        }
        return new AttemptCredentialMaterial(issuanceId, token.Value, repositoryId, token.ExpiresAtUtc, identity.StorageMode);
    }
}
