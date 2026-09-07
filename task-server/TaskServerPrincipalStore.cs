using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedScopesByKind =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [TaskServerPrincipalKinds.Studio] = new HashSet<string>(
                [
                    TaskServerScopes.TasksRead,
                    TaskServerScopes.TasksWrite,
                    TaskServerScopes.Management,
                    TaskServerScopes.EventsSubscribe,
                ],
                StringComparer.Ordinal),
            [TaskServerPrincipalKinds.Engine] = new HashSet<string>(
                [
                    TaskServerScopes.TasksRead,
                    TaskServerScopes.OrchestrationClaim,
                    TaskServerScopes.OrchestrationWrite,
                    TaskServerScopes.EventsSubscribe,
                ],
                StringComparer.Ordinal),
            [TaskServerPrincipalKinds.Runner] = new HashSet<string>(
                [
                    TaskServerScopes.TasksRead,
                    TaskServerScopes.RunsClaim,
                    TaskServerScopes.RunsWrite,
                    TaskServerScopes.ReviewsClaim,
                    TaskServerScopes.ReviewsWrite,
                    TaskServerScopes.EventsWrite,
                ],
                StringComparer.Ordinal),
        };

    public async Task<IssuedPrincipalCredential?> EnsureBootstrapPrincipalAsync(
        string principalId,
        string kind,
        string? credential,
        string? runnerId,
        CancellationToken ct)
    {
        var scopes = DefaultScopes(kind);
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenReadyAsync(ct);
            if (await ScalarAsync(
                    connection,
                    "SELECT 1 FROM principals WHERE principal_id = $id;",
                    ct,
                    ("$id", principalId)) is not null)
                return null;

            var issued = string.IsNullOrWhiteSpace(credential)
                ? GenerateCredential()
                : NormalizeCredential(credential);
            var now = UtcNow;
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await InsertPrincipalAsync(
                connection,
                transaction,
                principalId,
                kind,
                scopes,
                runnerId,
                issued,
                now,
                ct);
            await transaction.CommitAsync(ct);
            return string.IsNullOrWhiteSpace(credential)
                ? new IssuedPrincipalCredential(
                    ToDto(principalId, kind, scopes, runnerId, now, null, null),
                    issued,
                    now)
                : null;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<TaskServerPrincipal?> AuthenticatePrincipalAsync(
        string credential,
        CancellationToken ct)
    {
        if (!TryCredentialId(credential, out var credentialId))
            return null;

        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT p.principal_id, p.kind, p.scopes_json, p.runner_id,
                   p.revoked_at, c.secret_hash, c.revoked_at, c.expires_at
              FROM principal_credentials c
              JOIN principals p ON p.principal_id = c.principal_id
             WHERE c.credential_id = $credential_id;
            """, ("$credential_id", credentialId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || !reader.IsDBNull(4)
            || !reader.IsDBNull(6)
            || !reader.IsDBNull(7) && Parse(reader.GetString(7)) <= UtcNow
            || !FixedTimeHashEquals(credential, reader.GetString(5)))
            return null;

        var principal = new TaskServerPrincipal(
            reader.GetString(0),
            reader.GetString(1),
            DeserializeScopes(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
        await reader.DisposeAsync();
        await ExecuteAsync(
            connection,
            "UPDATE principals SET last_seen_at = $now WHERE principal_id = $id;",
            ct,
            ("$now", Iso(UtcNow)),
            ("$id", principal.PrincipalId));
        return principal;
    }

    public async Task<IReadOnlyList<PrincipalDto>> ListPrincipalsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT principal_id, kind, scopes_json, runner_id, created_at, revoked_at, last_seen_at
              FROM principals
             ORDER BY created_at, principal_id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<PrincipalDto>();
        while (await reader.ReadAsync(ct))
            result.Add(ReadPrincipal(reader));
        return result;
    }

    public async Task<IssuedPrincipalCredential> CreatePrincipalAsync(
        CreatePrincipalRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        var principalId = RequireIdentifier(request.PrincipalId, "Principal id");
        var kind = NormalizeKind(request.Kind);
        var scopes = ValidateScopes(kind, request.Scopes);
        var runnerId = kind == TaskServerPrincipalKinds.Runner
            ? RequireIdentifier(request.RunnerId, "Runner id")
            : request.RunnerId is null
                ? null
                : throw new ArgumentException("Runner id is valid only for runner principals.");
        var credential = GenerateCredential();
        var now = UtcNow;

        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            if (await ReadPrincipalAsync(connection, transaction, principalId, ct) is not null)
                throw new TaskServerConflictException(
                    "principal-exists",
                    $"Principal '{principalId}' already exists. Rotate it instead.");
            await InsertPrincipalAsync(
                connection,
                transaction,
                principalId,
                kind,
                scopes,
                runnerId,
                credential,
                now,
                ct);
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "principal.created",
                "principal",
                principalId,
                JsonSerializer.Serialize(new { kind, runnerId, scopes }),
                ct);
        }, ct);
        return new IssuedPrincipalCredential(
            ToDto(principalId, kind, scopes, runnerId, now, null, null),
            credential,
            now);
    }

    public async Task<IssuedPrincipalCredential> RotatePrincipalAsync(
        string principalId,
        RotatePrincipalRequest request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        var overlap = request.OverlapSeconds ?? _options.PrincipalRotationOverlapSeconds;
        if (overlap < 0 || overlap > _options.MaximumPrincipalRotationOverlapSeconds)
            throw new ArgumentException(
                $"OverlapSeconds must be between 0 and {_options.MaximumPrincipalRotationOverlapSeconds}.");
        var now = UtcNow;
        var validUntil = now.AddSeconds(overlap);
        var credential = GenerateCredential();
        PrincipalDto? principal = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            principal = await ReadPrincipalAsync(connection, transaction, principalId, ct)
                        ?? throw new KeyNotFoundException("Principal was not found.");
            if (principal.RevokedAt is not null)
                throw new InvalidOperationException("A revoked principal cannot be rotated.");
            await ExecuteAsync(connection, """
                UPDATE principal_credentials
                   SET expires_at = $expires
                 WHERE principal_id = $id
                   AND revoked_at IS NULL
                   AND (expires_at IS NULL OR expires_at > $expires);
                """, ct, transaction,
                ("$expires", Iso(validUntil)),
                ("$id", principalId));
            await InsertCredentialAsync(
                connection,
                transaction,
                principalId,
                credential,
                now,
                ct);
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "principal.rotated",
                "principal",
                principalId,
                JsonSerializer.Serialize(new { overlapSeconds = overlap }),
                ct);
        }, ct);
        return new IssuedPrincipalCredential(
            principal!,
            credential,
            now,
            validUntil);
    }

    public async Task<PrincipalDto> RevokePrincipalAsync(
        string principalId,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        PrincipalDto? principal = null;
        var now = UtcNow;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            principal = await ReadPrincipalAsync(connection, transaction, principalId, ct)
                        ?? throw new KeyNotFoundException("Principal was not found.");
            await ExecuteAsync(connection, """
                UPDATE principals SET revoked_at = COALESCE(revoked_at, $now)
                 WHERE principal_id = $id;
                UPDATE principal_credentials SET revoked_at = COALESCE(revoked_at, $now)
                 WHERE principal_id = $id;
                """, ct, transaction, ("$now", Iso(now)), ("$id", principalId));
            await AuditAsync(
                connection,
                transaction,
                actorId,
                "principal.revoked",
                "principal",
                principalId,
                "{}",
                ct);
        }, ct);
        return principal! with { RevokedAt = principal.RevokedAt ?? now };
    }

    private static IReadOnlySet<string> DefaultScopes(string kind)
        => AllowedScopesByKind[NormalizeKind(kind)];

    private static IReadOnlySet<string> ValidateScopes(
        string kind,
        IReadOnlyList<string>? requested)
    {
        var allowed = AllowedScopesByKind[kind];
        if (requested is null || requested.Count == 0)
            return allowed;
        var scopes = requested
            .Select(scope => scope.Trim())
            .Where(scope => scope.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var invalid = scopes.Where(scope => !allowed.Contains(scope)).Order().ToArray();
        if (invalid.Length > 0)
            throw new ArgumentException(
                $"Scopes are not allowed for principal kind '{kind}': {string.Join(", ", invalid)}.");
        return scopes;
    }

    private static string NormalizeKind(string kind)
    {
        var normalized = kind.Trim().ToLowerInvariant();
        return AllowedScopesByKind.ContainsKey(normalized)
            ? normalized
            : throw new ArgumentException("Principal kind must be studio, engine, or runner.");
    }

    private static string RequireIdentifier(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                                         || character is '-' or '_' or '.' or ':')))
            throw new ArgumentException($"{label} is invalid.");
        return value.Trim();
    }

    private static string GenerateCredential()
    {
        var id = RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
        var secret = RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        return $"ats_{id}.{secret}";
    }

    private static string NormalizeCredential(string credential)
    {
        var trimmed = credential.Trim();
        if (trimmed.Length < 32)
            throw new ArgumentException("Principal credentials must contain at least 256 bits of random material.");
        return trimmed;
    }

    private static bool TryCredentialId(string credential, out string credentialId)
    {
        credentialId = string.Empty;
        var separator = credential.IndexOf('.');
        if (separator > 4 && credential.StartsWith("ats_", StringComparison.Ordinal))
        {
            credentialId = credential[4..separator];
            return credentialId.Length is >= 16 and <= 64
                   && credentialId.All(char.IsAsciiHexDigit);
        }
        if (credential.Length < 32)
            return false;
        credentialId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(credential)))[..16]
            .ToLowerInvariant();
        return true;
    }

    private static string HashCredential(string credential)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)))
            .ToLowerInvariant();

    private static bool FixedTimeHashEquals(string credential, string expectedHash)
    {
        var actual = Encoding.ASCII.GetBytes(HashCredential(credential));
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        return actual.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static IReadOnlySet<string> DeserializeScopes(string json)
        => (JsonSerializer.Deserialize<string[]>(json) ?? [])
            .ToHashSet(StringComparer.Ordinal);

    private static PrincipalDto ReadPrincipal(SqliteDataReader reader)
        => ToDto(
            reader.GetString(0),
            reader.GetString(1),
            DeserializeScopes(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Parse(reader.GetString(6)));

    private static PrincipalDto ToDto(
        string principalId,
        string kind,
        IReadOnlySet<string> scopes,
        string? runnerId,
        DateTime createdAt,
        DateTime? revokedAt,
        DateTime? lastSeenAt)
        => new(
            principalId,
            kind,
            scopes.Order(StringComparer.Ordinal).ToArray(),
            runnerId,
            createdAt,
            revokedAt,
            lastSeenAt);

    private static async Task InsertPrincipalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string principalId,
        string kind,
        IReadOnlySet<string> scopes,
        string? runnerId,
        string credential,
        DateTime now,
        CancellationToken ct)
    {
        await ExecuteAsync(connection, """
            INSERT INTO principals(
                principal_id, kind, scopes_json, runner_id, created_at, revoked_at, last_seen_at)
            VALUES ($id, $kind, $scopes, $runner, $now, NULL, NULL);
            """, ct, transaction,
            ("$id", principalId),
            ("$kind", kind),
            ("$scopes", JsonSerializer.Serialize(scopes.Order(StringComparer.Ordinal))),
            ("$runner", runnerId),
            ("$now", Iso(now)));
        await InsertCredentialAsync(connection, transaction, principalId, credential, now, ct);
    }

    private static async Task InsertCredentialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string principalId,
        string credential,
        DateTime now,
        CancellationToken ct)
    {
        if (!TryCredentialId(credential, out var credentialId))
            throw new InvalidOperationException("Generated principal credential is malformed.");
        await ExecuteAsync(connection, """
            INSERT INTO principal_credentials(
                credential_id, principal_id, secret_hash, created_at, expires_at, revoked_at)
            VALUES ($credential_id, $principal_id, $secret_hash, $now, NULL, NULL);
            """, ct, transaction,
            ("$credential_id", credentialId),
            ("$principal_id", principalId),
            ("$secret_hash", HashCredential(credential)),
            ("$now", Iso(now)));
    }

    private static async Task<PrincipalDto?> ReadPrincipalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string principalId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT principal_id, kind, scopes_json, runner_id, created_at, revoked_at, last_seen_at
              FROM principals WHERE principal_id = $id;
            """, transaction, ("$id", principalId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPrincipal(reader) : null;
    }
}
