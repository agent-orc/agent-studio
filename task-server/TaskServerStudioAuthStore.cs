using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
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

    public async Task<StudioAuthStatusDto> GetStudioAuthStatusAsync(string? sessionToken, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var bootstrapRequired = Convert.ToInt64(
            await ScalarAsync(connection, "SELECT count(*) FROM studio_users;", ct)) == 0;
        var session = await ResolveStudioSessionAsync(connection, null, sessionToken, ct);
        return new StudioAuthStatusDto(
            bootstrapRequired,
            session is not null,
            session is null ? null : ToStudioAuthUserDto(session.Value.User));
    }

    public async Task<StudioAuthSessionDto> BootstrapStudioAuthAsync(StudioBootstrapRequest request, CancellationToken ct)
    {
        var username = RequireStudioUsername(request.Username);
        var password = RequireStudioPassword(request.Password);
        StudioAuthSessionDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var existing = Convert.ToInt64(await ScalarAsync(
                connection, "SELECT count(*) FROM studio_users;", ct, transaction));
            if (existing > 0)
                throw new TaskServerConflictException(
                    "studio-already-bootstrapped", "The studio owner account has already been created.");

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
            await AuditAsync(connection, transaction, userId, "studio-user.bootstrapped", "studio-user", userId,
                JsonSerializer.Serialize(new { username }), ct);

            var user = new StudioUserRow(userId, username, displayName, StudioUserRoles.Owner,
                string.Empty, [], false, false, Parse(now), Parse(now));
            var (token, csrf) = await CreateStudioSessionAsync(connection, transaction, userId, ct);
            result = new StudioAuthSessionDto(
                token, csrf, new StudioAuthStatusDto(false, true, ToStudioAuthUserDto(user)));
        }, ct);
        return result!;
    }

    public async Task<StudioAuthSessionDto> LoginStudioAsync(StudioLoginRequest request, CancellationToken ct)
    {
        var username = RequireStudioUsername(request.Username);
        StudioAuthSessionDto? result = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var user = await ReadStudioUserByUsernameAsync(connection, transaction, username, ct);
            if (user is null || user.Disabled
                || !VerifyStudioPassword(request.Password ?? string.Empty, user.PasswordHash))
                throw new StudioAuthenticationException(
                    "invalid-credentials", "The username or password is incorrect.");

            var (token, csrf) = await CreateStudioSessionAsync(connection, transaction, user.Id, ct);
            await AuditAsync(connection, transaction, user.Id, "studio-user.logged-in", "studio-user", user.Id, "{}", ct);
            result = new StudioAuthSessionDto(
                token, csrf, new StudioAuthStatusDto(false, true, ToStudioAuthUserDto(user)));
        }, ct);
        return result!;
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

    private async Task<(string SessionId, StudioUserRow User)?> ResolveStudioSessionAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? sessionToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        await using var command = Command(connection, """
            SELECT s.id, u.id, u.username, u.display_name, u.role, u.password_hash,
                   u.project_ids_json, u.disabled, u.must_change_password, u.created_at, u.updated_at
              FROM studio_sessions s
              JOIN studio_users u ON u.id = s.user_id
             WHERE s.token_hash = $hash AND s.revoked_at IS NULL;
            """, transaction, ("$hash", HashStudioSessionToken(sessionToken)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var sessionId = reader.GetString(0);
        var user = ReadStudioUser(reader, offset: 1);
        return (sessionId, user);
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
        SqliteConnection connection, SqliteTransaction transaction, string userId, CancellationToken ct)
    {
        var id = RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
        var secret = RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var token = $"sts_{id}.{secret}";
        var csrf = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        var now = Iso(UtcNow);
        await ExecuteAsync(connection, """
            INSERT INTO studio_sessions(id, user_id, token_hash, csrf_token, created_at, last_seen_at)
            VALUES ($id, $user, $hash, $csrf, $now, $now);
            """, ct, transaction,
            ("$id", $"sss_{Guid.NewGuid():N}"), ("$user", userId),
            ("$hash", HashStudioSessionToken(token)), ("$csrf", csrf), ("$now", now));
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
