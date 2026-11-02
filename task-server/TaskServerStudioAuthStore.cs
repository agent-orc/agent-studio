using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Security;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// Human studio identity: bootstrap, login, logout, change-password, and
/// status. This is a second, nested identity layer above the machine
/// principal bearer model in <see cref="TaskServerPrincipalStore"/> - the
/// connector authenticates itself to the Task Server with its own bearer for
/// every call, and a human studio session lives underneath that call.
/// </summary>
public sealed partial class TaskServerStore
{
    private const int StudioPasswordIterations = 100_000;
    private const int StudioPasswordHashSize = 32;
    private const int StudioPasswordSaltSize = 16;
    private static readonly string DummyStudioPasswordHash = HashStudioPassword(
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    private readonly object _studioLoginGate = new();
    private readonly Dictionary<string, (int Failures, DateTime WindowStarted, DateTime LockedUntil)> _studioClientFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, DateTime WindowStarted, DateTime LockedUntil)> _studioUsernameFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, DateTime WindowStarted, DateTime LockedUntil)> _studioNetworkFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _studioUsernameClientDelays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _studioRecentSuccesses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _studioPendingTotp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Challenge, DateTime ExpiresAt)> _studioPasskeyChallenges = new(StringComparer.Ordinal);

    public async Task<StudioAuthStatusDto> GetStudioAuthStatusAsync(string? sessionToken, CancellationToken ct, bool touch = true)
    {
        await using var connection = await OpenReadyAsync(ct);
        var bootstrapRequired = Convert.ToInt64(
            await ScalarAsync(connection, "SELECT count(*) FROM studio_users;", ct)) == 0;
        var bootstrapCodeRequired = bootstrapRequired
            && await ReadMetaAsync(connection, null, OwnerBootstrapCodeHashKey, ct) is not null;
        var session = await ResolveStudioSessionAsync(connection, null, sessionToken, ct, touch);
        return new StudioAuthStatusDto(
            bootstrapRequired,
            session is not null,
            session is null ? null : ToStudioAuthUserDto(session.Value.User),
            bootstrapCodeRequired,
            session?.EnrollmentOnly == true);
    }

    public async Task<StudioOwnerBootstrapSessionDto> BootstrapStudioAuthAsync(
        StudioBootstrapRequest request, bool requiresBootstrapCode, CancellationToken ct)
    {
        var username = RequireStudioUsername(request.Username);
        var password = RequireStudioPassword(request.Password);
        StudioOwnerBootstrapSessionDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = Convert.ToInt64(await ScalarAsync(
                connection, "SELECT count(*) FROM studio_users;", ct, transaction));
            var armedHash = await ReadMetaAsync(connection, transaction, OwnerBootstrapCodeHashKey, ct);
            var presented = string.IsNullOrWhiteSpace(request.BootstrapCode) ? null : request.BootstrapCode.Trim();
            var decision = OwnerBootstrapPolicy.DecideAdmit(
                existing > 0,
                requiresBootstrapCode,
                armedHash,
                presented is not null && armedHash is not null && FixedTimeEqualsHex(HashOneTimeSecret(presented), armedHash),
                presented is not null);
            switch (decision)
            {
                case OwnerBootstrapPolicy.AdmitDecision.AlreadyBootstrapped:
                    throw new TaskServerConflictException(
                        "studio-already-bootstrapped", "The studio owner account has already been created.");
                case OwnerBootstrapPolicy.AdmitDecision.CodeRequired:
                    throw new StudioAuthenticationException(
                        "owner-bootstrap-code-required",
                        "Owner bootstrap requires the one-time code from the installation host.");
                case OwnerBootstrapPolicy.AdmitDecision.CodeNotArmed:
                case OwnerBootstrapPolicy.AdmitDecision.CodeInvalid:
                    throw new StudioAuthenticationException(
                        "owner-bootstrap-code-invalid", "The owner bootstrap code is not valid for this installation.");
            }

            var userId = StableOrGeneratedId(null, "usr");
            var now = Iso(UtcNow);
            var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim();
            await ExecuteAsync(connection, """
                INSERT INTO studio_users(
                    id, username, display_name, role, password_hash, project_ids_json,
                    disabled, must_change_password, created_at, updated_at)
                VALUES ($id, $username, $display, $role, $hash, '[]', 0, 0, $now, $now);
                """, ct, transaction,
                ("$id", userId), ("$username", username), ("$display", displayName),
                ("$role", StudioUserRoles.Owner), ("$hash", HashStudioPassword(password)), ("$now", now));
            // Bootstrap closes for good: the armed code is consumed with the owner insert.
            await ExecuteAsync(connection, "DELETE FROM meta WHERE key = $key;", ct, transaction,
                ("$key", OwnerBootstrapCodeHashKey));
            var recoveryCode = await ReplaceRecoveryCodeAsync(connection, transaction, userId, ct);
            await AuditAsync(connection, transaction, userId, "studio-user.bootstrapped", "studio-user", userId,
                JsonSerializer.Serialize(new { username, bootstrapCode = presented is not null }), ct);

            var user = new StudioUserRow(userId, username, displayName, StudioUserRoles.Owner,
                string.Empty, [], false, false, Parse(now), Parse(now));
            var (token, csrf) = await CreateStudioSessionAsync(connection, transaction, userId, ct, enrollmentOnly: true);
            result = new StudioOwnerBootstrapSessionDto(
                token, csrf, new StudioAuthStatusDto(false, true, ToStudioAuthUserDto(user), SecondFactorEnrollmentRequired: true), recoveryCode);
        }, ct);
        return result!;
    }

    public async Task<StudioAuthSessionDto> LoginStudioAsync(StudioLoginRequest request, CancellationToken ct,
        string? clientKey = null, string? device = null, PasskeyAssertionProof? passkey = null,
        string? origin = null, string? rpId = null)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        passkey ??= request.Passkey;
        clientKey = string.IsNullOrWhiteSpace(clientKey) ? "unknown" : clientKey.Trim();
        if (clientKey.Length > 64) throw new StudioThrottledException();
        var client = clientKey;
        var normalizedUsername = username.ToLowerInvariant();
        var loginKey = normalizedUsername + "|" + client;
        var network = StudioNetworkKey(client);
        if (username.Length is 0 or > 64)
        {
            lock (_studioLoginGate)
            {
                CheckStudioThrottle(_studioClientFailures, client);
                RecordStudioFailure(_studioClientFailures, client, 10);
                if (network is not null) RecordStudioFailure(_studioNetworkFailures, network, 40);
            }
            throw new StudioAuthenticationException("invalid-credentials", "The username or password is incorrect.");
        }
        lock (_studioLoginGate)
        {
            if (_studioClientFailures.Count >= 8192 && !_studioClientFailures.ContainsKey(client))
                throw new StudioThrottledException();
            CheckStudioThrottle(_studioClientFailures, client);
            var recent = _studioRecentSuccesses.TryGetValue(loginKey, out var success)
                && UtcNow - success <= TimeSpan.FromDays(1);
            if (network is not null && !recent) CheckStudioThrottle(_studioNetworkFailures, network);
            if (_studioUsernameFailures.TryGetValue(normalizedUsername, out var usernameAttempt)
                && usernameAttempt.Failures >= 20 && UtcNow - usernameAttempt.WindowStarted <= TimeSpan.FromMinutes(15)
                && !recent)
            {
                if (!_studioUsernameClientDelays.TryGetValue(loginKey, out var retryAt))
                {
                    _studioUsernameClientDelays[loginKey] = UtcNow.AddSeconds(2);
                    throw new StudioThrottledException();
                }
                if (retryAt > UtcNow) throw new StudioThrottledException();
                _studioUsernameClientDelays.Remove(loginKey);
            }
        }
        StudioAuthSessionDto? result = null;
        try
        {
            await InWriteTransactionAsync(async (connection, transaction) =>
            {
                var user = await ReadStudioUserByUsernameAsync(connection, transaction, username, ct);
                var passwordMatches = (request.Password?.Length ?? 0) <= 128
                    && VerifyStudioPassword(request.Password ?? string.Empty, user?.PasswordHash ?? DummyStudioPasswordHash);
                if (user is null || user.Disabled || !passwordMatches)
                    throw new StudioAuthenticationException(
                        "invalid-credentials", "The username or password is incorrect.");
                var needsEnrollment = (user.Role is StudioUserRoles.Owner or StudioUserRoles.Operator)
                    && Convert.ToInt64(await ScalarAsync(connection,
                        "SELECT count(*) FROM studio_factors WHERE user_id = $user AND (totp_secret IS NOT NULL OR passkeys_json != '[]');",
                        ct, transaction, ("$user", user.Id))) == 0;
                if ((user.Role is StudioUserRoles.Owner or StudioUserRoles.Operator) && !needsEnrollment)
                {
                    if (passkey is not null)
                        await VerifyStudioPasskeyAsync(connection, transaction, user.Id, passkey,
                            username.ToLowerInvariant() + "|" + client, origin, rpId, ct);
                    else await VerifyStudioFactorAsync(connection, transaction, user.Id, request.SecondFactorCode, ct);
                }
                var (token, csrf) = await CreateStudioSessionAsync(connection, transaction, user.Id, ct,
                    enrollmentOnly: needsEnrollment, device: device, address: client);
                await AuditAsync(connection, transaction, user.Id, "studio-user.logged-in", "studio-user", user.Id, "{}", ct);
                result = new StudioAuthSessionDto(token, csrf,
                    new StudioAuthStatusDto(false, true, ToStudioAuthUserDto(user), SecondFactorEnrollmentRequired: needsEnrollment));
            }, ct);
        }
        catch (StudioAuthenticationException ex) when (ex.Code is "invalid-credentials" or "invalid-second-factor" or "invalid-passkey")
        {
            lock (_studioLoginGate)
            {
                RecordStudioFailure(_studioClientFailures, client, 10);
                RecordStudioFailure(_studioUsernameFailures, normalizedUsername, 20, lockout: false);
                if (network is not null) RecordStudioFailure(_studioNetworkFailures, network, 40);
                if (_studioUsernameFailures[normalizedUsername].Failures >= 20)
                    _studioUsernameClientDelays[loginKey] = UtcNow.AddSeconds(2);
            }
            throw;
        }
        lock (_studioLoginGate)
        {
            _studioClientFailures.Remove(client);
            _studioRecentSuccesses[loginKey] = UtcNow;
            _studioUsernameClientDelays.Remove(loginKey);
        }
        return result!;
    }

    private void CheckStudioThrottle(Dictionary<string, (int Failures, DateTime WindowStarted, DateTime LockedUntil)> failures, string key)
    {
        if (failures.TryGetValue(key, out var attempt) && attempt.LockedUntil > UtcNow)
            throw new StudioThrottledException();
    }

    private void RecordStudioFailure(Dictionary<string, (int Failures, DateTime WindowStarted, DateTime LockedUntil)> failures,
        string key, int limit, bool lockout = true)
    {
        if (failures.Count > 8192)
            foreach (var stale in failures.Where(x => UtcNow - x.Value.WindowStarted > TimeSpan.FromMinutes(15)).Select(x => x.Key).ToArray())
                failures.Remove(stale);
        var previous = failures.GetValueOrDefault(key);
        var active = UtcNow - previous.WindowStarted <= TimeSpan.FromMinutes(15);
        var count = active ? previous.Failures + 1 : 1;
        failures[key] = (count, active ? previous.WindowStarted : UtcNow,
            lockout && count >= limit ? UtcNow.AddMinutes(15) : DateTime.MinValue);
    }

    private static string? StudioNetworkKey(string client)
    {
        if (!IPAddress.TryParse(client, out var address)) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        Array.Clear(bytes, 6, 10);
        return new IPAddress(bytes) + "/48";
    }

    public async Task LogoutStudioAsync(string? sessionToken, CancellationToken ct)
    {
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct);
            if (session is null)
                throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            await ExecuteAsync(connection, """
                UPDATE studio_sessions SET revoked_at = $now WHERE id = $id;
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$id", session.Value.SessionId));
            await AuditAsync(connection, transaction, session.Value.User.Id, "studio-user.logged-out",
                "studio-user", session.Value.User.Id, "{}", ct);
        }, ct);
    }

    public async Task<StudioAuthUserDto> ChangeStudioPasswordAsync(
        string? sessionToken, StudioChangePasswordRequest request, CancellationToken ct)
    {
        var newPassword = RequireStudioPassword(request.NewPassword);
        StudioAuthUserDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct);
            if (session is null)
                throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            if (!VerifyStudioPassword(request.CurrentPassword ?? string.Empty, session.Value.User.PasswordHash))
                throw new StudioAuthenticationException("invalid-credentials", "The current password is incorrect.");

            var now = Iso(UtcNow);
            await ExecuteAsync(connection, """
                UPDATE studio_users SET password_hash = $hash, must_change_password = 0, updated_at = $now
                 WHERE id = $id;
                """, ct, transaction, ("$hash", HashStudioPassword(newPassword)), ("$now", now), ("$id", session.Value.User.Id));
            await AuditAsync(connection, transaction, session.Value.User.Id, "studio-user.password-changed",
                "studio-user", session.Value.User.Id, "{}", ct);
            result = ToStudioAuthUserDto(session.Value.User with { MustChangePassword = false, UpdatedAt = Parse(now) });
        }, ct);
        return result!;
    }

    private async Task<(string SessionId, StudioUserRow User, bool EnrollmentOnly)?> ResolveStudioSessionAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? sessionToken, CancellationToken ct, bool touch = true)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        await using var command = Command(connection, """
            SELECT s.id, u.id, u.username, u.display_name, u.role, u.password_hash,
                   u.project_ids_json, u.disabled, u.must_change_password, u.created_at, u.updated_at,
                   s.created_at, s.last_seen_at, s.expires_at, s.absolute_expires_at, s.enrollment_only
              FROM studio_sessions s
              JOIN studio_users u ON u.id = s.user_id
             WHERE s.token_hash = $hash AND s.revoked_at IS NULL;
            """, transaction, ("$hash", HashStudioSessionToken(sessionToken)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var sessionId = reader.GetString(0);
        var user = ReadStudioUser(reader, offset: 1);
        var created = Parse(reader.GetString(11));
        var seen = Parse(reader.GetString(12));
        var expires = reader.IsDBNull(13) ? seen.AddHours(8) : Parse(reader.GetString(13));
        var absolute = reader.IsDBNull(14) ? created.AddDays(7) : Parse(reader.GetString(14));
        var enrollmentOnly = reader.GetInt64(15) != 0;
        await reader.DisposeAsync();
        if (expires <= UtcNow || absolute <= UtcNow || user.Disabled) return null;
        if (touch && UtcNow - seen >= TimeSpan.FromMinutes(1))
            await ExecuteAsync(connection, "UPDATE studio_sessions SET last_seen_at = $seen, expires_at = $expiry WHERE id = $id;",
                ct, transaction, ("$seen", Iso(UtcNow)), ("$expiry", Iso(UtcNow.AddHours(8) < absolute ? UtcNow.AddHours(8) : absolute)), ("$id", sessionId));
        return (sessionId, user, enrollmentOnly);
    }

    private static async Task<StudioUserRow?> ReadStudioUserByUsernameAsync(
        SqliteConnection connection, SqliteTransaction transaction, string username, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT id, username, display_name, role, password_hash,
                   project_ids_json, disabled, must_change_password, created_at, updated_at
              FROM studio_users WHERE username = $username COLLATE NOCASE;
            """, transaction, ("$username", username));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStudioUser(reader, offset: 0) : null;
    }

    private static StudioUserRow ReadStudioUser(SqliteDataReader reader, int offset)
        => new(
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            reader.GetString(offset + 4),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(offset + 5)) ?? [],
            reader.GetInt64(offset + 6) != 0,
            reader.GetInt64(offset + 7) != 0,
            Parse(reader.GetString(offset + 8)),
            Parse(reader.GetString(offset + 9)));

    private static StudioAuthUserDto ToStudioAuthUserDto(StudioUserRow row)
        => new(row.Id, row.Username, row.DisplayName, row.Role, row.ProjectIds, row.Disabled, row.MustChangePassword);

    private async Task<(string Token, string Csrf)> CreateStudioSessionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string userId, CancellationToken ct,
        bool enrollmentOnly = false, string? device = null, string? address = null)
    {
        var id = RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
        var secret = RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var token = $"sts_{id}.{secret}";
        var csrf = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        var now = Iso(UtcNow);
        await ExecuteAsync(connection, """
            INSERT INTO studio_sessions(id, user_id, token_hash, csrf_token, created_at, last_seen_at,
                expires_at, absolute_expires_at, enrollment_only, device, address)
            VALUES ($id, $user, $hash, $csrf, $now, $now, $expiry, $absolute, $enrollment, $device, $address);
            """, ct, transaction,
            ("$id", $"sss_{Guid.NewGuid():N}"), ("$user", userId),
            ("$hash", HashStudioSessionToken(token)), ("$csrf", csrf), ("$now", now),
            ("$expiry", Iso(UtcNow.AddHours(8))), ("$absolute", Iso(UtcNow.AddDays(7))),
            ("$enrollment", enrollmentOnly ? 1 : 0), ("$device", device), ("$address", address));
        return (token, csrf);
    }

    private static string RequireStudioUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > 64)
            throw new ArgumentException("Username is required and must be 64 characters or fewer.");
        return username.Trim();
    }

    private static string RequireStudioPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.");
        return password;
    }

    private static string HashStudioPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(StudioPasswordSaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, StudioPasswordIterations, HashAlgorithmName.SHA256, StudioPasswordHashSize);
        return $"pbkdf2${StudioPasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyStudioPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
            return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string HashStudioSessionToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private sealed record StudioUserRow(
        string Id,
        string Username,
        string DisplayName,
        string Role,
        string PasswordHash,
        IReadOnlyList<string> ProjectIds,
        bool Disabled,
        bool MustChangePassword,
        DateTime CreatedAt,
        DateTime UpdatedAt);
}

public sealed class StudioAuthenticationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
