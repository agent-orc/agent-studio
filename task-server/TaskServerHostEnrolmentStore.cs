using System.Globalization;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Runner-host enrolment ledger (AGT-W63 I03). One host identity owns its role
/// principals and shared coding/review envelope; claims on an enrolled host
/// are admitted through <see cref="HostEnrolmentPolicy"/>. Hosts without an
/// enrolment keep the existing runtime-capacity behaviour.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<HostEnrolmentDto> EnrolHostAsync(
        string hostId,
        EnrolHostRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        ArgumentNullException.ThrowIfNull(request);
        hostId = hostId?.Trim() ?? string.Empty;
        if (HostEnrolmentPolicy.Validate(hostId, request) is { } error) throw new ArgumentException(error);
        var hostClass = request.HostClass.Trim().ToLowerInvariant();
        var roles = request.Roles
            .Select(role => new HostRolePrincipalDto(role.Role, role.PrincipalId.Trim()))
            .OrderBy(role => role.Role, StringComparer.Ordinal)
            .ToArray();
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadHostEnrolmentAsync(connection, transaction, hostId, ct);
            var generation = current?.Generation ?? 0;
            if (request.ExpectedGeneration != generation)
                throw new TaskServerConflictException(
                    "host-enrolment-generation-mismatch",
                    $"Host '{hostId}' enrolment is at generation {generation}, not {request.ExpectedGeneration}.");
            foreach (var role in roles)
            {
                var owner = Convert.ToString(await ScalarAsync(connection, """
                    SELECT host_id FROM host_enrolment_principals WHERE principal_id = $principal;
                    """, ct, transaction, ("$principal", role.PrincipalId)), CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(owner) && !string.Equals(owner, hostId, StringComparison.Ordinal))
                    throw new TaskServerConflictException(
                        "principal-enrolled-elsewhere",
                        $"Principal '{role.PrincipalId}' is already enrolled on host '{owner}'; enrol a distinct principal.");
                var runnerHost = Convert.ToString(await ScalarAsync(connection, """
                    SELECT host_id FROM runners WHERE id = $principal;
                    """, ct, transaction, ("$principal", role.PrincipalId)), CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(runnerHost) && !string.Equals(runnerHost, hostId, StringComparison.Ordinal))
                    throw new TaskServerConflictException(
                        "principal-enrolled-elsewhere",
                        $"Principal '{role.PrincipalId}' is registered from host '{runnerHost}'.");
            }
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                INSERT INTO host_enrolments(
                    host_id, host_class, status, generation, envelope_json,
                    enrolled_at, updated_at, removed_at)
                VALUES ($host, $class, 'enrolled', $generation, $envelope, $now, $now, NULL)
                ON CONFLICT(host_id) DO UPDATE SET
                    host_class = excluded.host_class,
                    status = 'enrolled',
                    generation = excluded.generation,
                    envelope_json = excluded.envelope_json,
                    enrolled_at = CASE WHEN host_enrolments.status = 'removed'
                                       THEN excluded.enrolled_at
                                       ELSE host_enrolments.enrolled_at END,
                    updated_at = excluded.updated_at,
                    removed_at = NULL;
                """, ct, transaction,
                ("$host", hostId),
                ("$class", hostClass),
                ("$generation", generation + 1),
                ("$envelope", JsonSerializer.Serialize(request.Envelope)),
                ("$now", now));
            await ExecuteAsync(connection, "DELETE FROM host_enrolment_principals WHERE host_id = $host;",
                ct, transaction, ("$host", hostId));
            foreach (var role in roles)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO host_enrolment_principals(principal_id, host_id, role)
                    VALUES ($principal, $host, $role);
                    """, ct, transaction,
                    ("$principal", role.PrincipalId),
                    ("$host", hostId),
                    ("$role", role.Role));
            }
            await AuditAsync(
                connection,
                transaction,
                actorId,
                current is null || current.Status == HostEnrolmentStatuses.Removed
                    ? "host.enrolled"
                    : "host.enrolment-updated",
                "host",
                hostId,
                JsonSerializer.Serialize(new
                {
                    hostClass,
                    generation = generation + 1,
                    roles,
                    request.Envelope,
                    request.Reason,
                }),
                ct);
        }, ct);
        await using var read = await OpenReadyAsync(ct);
        return (await ReadHostEnrolmentAsync(read, null, hostId, ct))!;
    }

    /// <summary>
    /// Removes a host. Its principals are released, new claims are refused with
    /// <c>host-removed</c>, and in-flight work settles through the existing
    /// lease and fence rules rather than being reassigned here.
    /// </summary>
    public async Task<HostEnrolmentDto> RemoveHostAsync(
        string hostId,
        RemoveHostRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        if (string.IsNullOrWhiteSpace(request?.Reason)) throw new ArgumentException("A removal reason is required.");
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var current = await ReadHostEnrolmentAsync(connection, transaction, hostId, ct)
                          ?? throw new KeyNotFoundException("Host enrolment was not found.");
            if (current.Generation != request.ExpectedGeneration)
                throw new TaskServerConflictException(
                    "host-enrolment-generation-mismatch",
                    $"Host '{hostId}' enrolment is at generation {current.Generation}, not {request.ExpectedGeneration}.");
            if (current.Status == HostEnrolmentStatuses.Removed) return;
            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                UPDATE host_enrolments
                   SET status = 'removed', generation = generation + 1,
                       updated_at = $now, removed_at = $now
                 WHERE host_id = $host;
                """, ct, transaction, ("$host", hostId), ("$now", now));
            await ExecuteAsync(connection, "DELETE FROM host_enrolment_principals WHERE host_id = $host;",
                ct, transaction, ("$host", hostId));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "host.removed",
                "host",
                hostId,
                JsonSerializer.Serialize(new { request.Reason, generation = current.Generation + 1 }),
                ct);
        }, ct);
        await using var read = await OpenReadyAsync(ct);
        return (await ReadHostEnrolmentAsync(read, null, hostId, ct))!;
    }

    public async Task<IReadOnlyList<HostEnrolmentDto>> ListHostEnrolmentsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var hosts = new List<string>();
        await using (var command = Command(connection, "SELECT host_id FROM host_enrolments ORDER BY host_id;"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) hosts.Add(reader.GetString(0));
        }
        var result = new List<HostEnrolmentDto>();
        foreach (var host in hosts)
            result.Add((await ReadHostEnrolmentAsync(connection, null, host, ct))!);
        return result;
    }

    private async Task<HostEnvelopeAdmission> EvaluateHostEnvelopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hostId,
        string role,
        string principalId,
        CancellationToken ct)
    {
        var enrolment = await ReadHostEnrolmentAsync(connection, transaction, hostId, ct);
        if (enrolment is null)
        {
            // A principal enrolled for another host must not claim from an
            // unenrolled one: that is the copied-secret case.
            var owner = Convert.ToString(await ScalarAsync(connection, """
                SELECT host_id FROM host_enrolment_principals WHERE principal_id = $principal;
                """, ct, transaction, ("$principal", principalId)), CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(owner)
                ? HostEnvelopeAdmission.Open
                : HostEnvelopeAdmission.Refused(
                    HostAdmissionReasons.PrincipalNotEnrolled,
                    $"Principal '{principalId}' is enrolled on host '{owner}', not '{hostId}'.");
        }
        var occupiedCoding = await CountOccupiedHostSlotsAsync(connection, transaction, hostId, ct);
        var occupiedReview = Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*) FROM review_attempts
             WHERE host_id = $host
               AND (status = 'process-unknown' OR (status = 'leased' AND expires_at > $now));
            """, ct, transaction, ("$host", hostId), ("$now", Iso(UtcNow))) ?? 0,
            CultureInfo.InvariantCulture);
        return HostEnrolmentPolicy.Decide(enrolment, role, principalId, occupiedCoding, occupiedReview);
    }

    private static async Task<HostEnrolmentDto?> ReadHostEnrolmentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string hostId,
        CancellationToken ct)
    {
        string hostClass, status, envelopeJson, enrolledAt, updatedAt;
        string? removedAt;
        long generation;
        await using (var command = Command(connection, """
            SELECT host_class, status, generation, envelope_json, enrolled_at, updated_at, removed_at
              FROM host_enrolments WHERE host_id = $host;
            """, transaction, ("$host", hostId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            hostClass = reader.GetString(0);
            status = reader.GetString(1);
            generation = reader.GetInt64(2);
            envelopeJson = reader.GetString(3);
            enrolledAt = reader.GetString(4);
            updatedAt = reader.GetString(5);
            removedAt = reader.IsDBNull(6) ? null : reader.GetString(6);
        }
        var roles = new List<HostRolePrincipalDto>();
        await using (var command = Command(connection, """
            SELECT role, principal_id FROM host_enrolment_principals
             WHERE host_id = $host ORDER BY role;
            """, transaction, ("$host", hostId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                roles.Add(new HostRolePrincipalDto(reader.GetString(0), reader.GetString(1)));
        }
        return new HostEnrolmentDto(
            hostId,
            hostClass,
            status,
            generation,
            roles,
            JsonSerializer.Deserialize<HostEnvelopeDto>(envelopeJson)!,
            Parse(enrolledAt),
            Parse(updatedAt),
            removedAt is null ? null : Parse(removedAt));
    }
}
