using System.Text.Json;
using System.Security.Cryptography;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private sealed record StoredPasskey(string Id, string Name, string PublicKey, uint SignCount, DateTime CreatedAt);

    public string BeginStudioPasskeyAssertion(string username, string clientKey)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 || clientKey.Length > 64)
            throw new ArgumentException("Username is invalid.");
        var challenge = WebAuthnProofs.NewChallenge();
        lock (_studioLoginGate)
            StoreStudioPasskeyChallenge(username.Trim().ToLowerInvariant() + "|" + clientKey, challenge);
        return challenge;
    }

    public async Task<string> BeginStudioPasskeyRegistrationAsync(string? sessionToken, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var session = await ResolveStudioSessionAsync(connection, null, sessionToken, ct)
            ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
        var challenge = WebAuthnProofs.NewChallenge();
        lock (_studioLoginGate) StoreStudioPasskeyChallenge("register:" + session.SessionId, challenge);
        return challenge;
    }

    public async Task<IReadOnlyList<string>> RegisterStudioPasskeyAsync(string? sessionToken,
        PasskeyRegistrationProof proof, string origin, string rpId, CancellationToken ct)
    {
        IReadOnlyList<string>? recovery = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct)
                ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            (string Challenge, DateTime ExpiresAt) pending;
            lock (_studioLoginGate)
            {
                if (!_studioPasskeyChallenges.Remove("register:" + session.SessionId, out pending))
                    throw new StudioAuthenticationException("passkey-challenge-expired", "Start passkey setup again.");
            }
            if (pending.ExpiresAt <= UtcNow)
                throw new StudioAuthenticationException("passkey-challenge-expired", "Start passkey setup again.");
            string publicKey;
            try
            {
                WebAuthnProofs.VerifyClientData(proof.ClientDataJson, "webauthn.create", pending.Challenge, origin);
                publicKey = WebAuthnProofs.RegistrationPublicKey(proof.AuthenticatorData, proof.CredentialId, rpId);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
            {
                throw new StudioAuthenticationException("invalid-passkey", "Passkey registration could not be verified.");
            }
            await using var read = Command(connection,
                "SELECT recovery_hashes_json, passkeys_json FROM studio_factors WHERE user_id = $user;",
                transaction, ("$user", session.User.Id));
            await using var reader = await read.ExecuteReaderAsync(ct);
            var hashes = new List<string>();
            var passkeys = new List<StoredPasskey>();
            if (await reader.ReadAsync(ct))
            {
                hashes = JsonSerializer.Deserialize<List<string>>(reader.GetString(0)) ?? [];
                passkeys = JsonSerializer.Deserialize<List<StoredPasskey>>(reader.GetString(1)) ?? [];
            }
            await reader.DisposeAsync();
            if (passkeys.Any(x => x.Id == proof.CredentialId))
                throw new StudioAuthenticationException("passkey-exists", "This passkey is already registered.");
            if (hashes.Count == 0)
            {
                recovery = SecondFactorCodes.NewRecoveryCodes();
                hashes = recovery.Select(SecondFactorCodes.HashRecoveryCode).ToList();
            }
            else recovery = [];
            var name = proof.Name ?? "Passkey";
            passkeys.Add(new StoredPasskey(proof.CredentialId,
                name.Length > 80 ? name[..80] : name,
                publicKey, 0, UtcNow));
            await ExecuteAsync(connection, """
                INSERT INTO studio_factors(user_id, recovery_hashes_json, passkeys_json)
                VALUES ($user, $codes, $keys)
                ON CONFLICT(user_id) DO UPDATE SET recovery_hashes_json = excluded.recovery_hashes_json,
                    passkeys_json = excluded.passkeys_json;
                """, ct, transaction, ("$user", session.User.Id), ("$codes", JsonSerializer.Serialize(hashes)),
                ("$keys", JsonSerializer.Serialize(passkeys)));
            await ExecuteAsync(connection, "UPDATE studio_sessions SET enrollment_only = 0 WHERE id = $id;",
                ct, transaction, ("$id", session.SessionId));
        }, ct);
        return recovery!;
    }

    public async Task<string> BeginStudioPasskeyStepUpAsync(string? sessionToken, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var session = await ResolveStudioSessionAsync(connection, null, sessionToken, ct)
            ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
        var challenge = WebAuthnProofs.NewChallenge();
        lock (_studioLoginGate) StoreStudioPasskeyChallenge("step-up:" + session.SessionId, challenge);
        return challenge;
    }

    public async Task VerifyStudioPasskeyStepUpAsync(string? sessionToken, PasskeyAssertionProof proof,
        string origin, string rpId, CancellationToken ct)
    {
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct)
                ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            await VerifyStudioPasskeyAsync(connection, transaction, session.User.Id, proof,
                "step-up:" + session.SessionId, origin, rpId, ct);
            await MarkStudioStepUpAsync(connection, transaction, session.SessionId, ct);
        }, ct);
    }

    private async Task VerifyStudioPasskeyAsync(SqliteConnection connection, SqliteTransaction transaction,
        string userId, PasskeyAssertionProof proof, string challengeKey, string? origin, string? rpId, CancellationToken ct)
    {
        (string Challenge, DateTime ExpiresAt) pending;
        lock (_studioLoginGate)
        {
            if (!_studioPasskeyChallenges.Remove(challengeKey, out pending))
                throw new StudioAuthenticationException("passkey-challenge-expired", "Start passkey verification again.");
        }
        if (pending.ExpiresAt <= UtcNow)
            throw new StudioAuthenticationException("passkey-challenge-expired", "Start passkey verification again.");
        await using var command = Command(connection, "SELECT passkeys_json FROM studio_factors WHERE user_id = $user;",
            transaction, ("$user", userId));
        var json = await command.ExecuteScalarAsync(ct) as string ?? "[]";
        var passkeys = JsonSerializer.Deserialize<List<StoredPasskey>>(json) ?? [];
        var passkey = passkeys.FirstOrDefault(x => x.Id == proof.CredentialId)
            ?? throw new StudioAuthenticationException("invalid-passkey", "Passkey is not registered.");
        uint count;
        try
        {
            count = WebAuthnProofs.VerifyAssertion(proof.ClientDataJson, proof.AuthenticatorData,
                proof.Signature, passkey.PublicKey, pending.Challenge, origin ?? string.Empty,
                rpId ?? string.Empty, passkey.SignCount);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            throw new StudioAuthenticationException("invalid-passkey", "Passkey verification failed.");
        }
        passkeys = passkeys.Select(x => x.Id == passkey.Id ? x with { SignCount = count } : x).ToList();
        await ExecuteAsync(connection, "UPDATE studio_factors SET passkeys_json = $keys WHERE user_id = $user;",
            ct, transaction, ("$keys", JsonSerializer.Serialize(passkeys)), ("$user", userId));
    }

    private void StoreStudioPasskeyChallenge(string key, string challenge)
    {
        foreach (var expired in _studioPasskeyChallenges.Where(x => x.Value.ExpiresAt <= UtcNow).Select(x => x.Key).ToArray())
            _studioPasskeyChallenges.Remove(expired);
        if (_studioPasskeyChallenges.Count >= 8192 && !_studioPasskeyChallenges.ContainsKey(key))
            throw new StudioThrottledException();
        _studioPasskeyChallenges[key] = (challenge, UtcNow.AddMinutes(5));
    }
    public async Task<string> BeginStudioTotpAsync(string? sessionToken, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var session = await ResolveStudioSessionAsync(connection, null, sessionToken, ct)
            ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
        var secret = SecondFactorCodes.NewTotpSecret();
        lock (_studioLoginGate) _studioPendingTotp[session.User.Id] = secret;
        return secret;
    }

    public async Task<IReadOnlyList<string>> ConfirmStudioTotpAsync(string? sessionToken, string code, CancellationToken ct)
    {
        IReadOnlyList<string>? codes = null;
        string? confirmedUserId = null;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct)
                ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            confirmedUserId = session.User.Id;
            string? secret;
            lock (_studioLoginGate) _studioPendingTotp.TryGetValue(session.User.Id, out secret);
            if (secret is null || SecondFactorCodes.MatchTotpStep(secret, code, UtcNow) is not { } acceptedStep)
                throw new StudioAuthenticationException("invalid-second-factor", "The authenticator code is invalid.");
            codes = SecondFactorCodes.NewRecoveryCodes();
            await ExecuteAsync(connection, """
                INSERT INTO studio_factors(user_id, totp_secret, last_accepted_totp_step, recovery_hashes_json)
                VALUES ($user, $secret, $step, $codes)
                ON CONFLICT(user_id) DO UPDATE SET totp_secret = excluded.totp_secret,
                    last_accepted_totp_step = excluded.last_accepted_totp_step,
                    recovery_hashes_json = excluded.recovery_hashes_json;
                """, ct, transaction, ("$user", session.User.Id), ("$secret", secret),
                ("$step", acceptedStep),
                ("$codes", JsonSerializer.Serialize(codes.Select(SecondFactorCodes.HashRecoveryCode))));
            await ExecuteAsync(connection, "UPDATE studio_sessions SET enrollment_only = 0 WHERE id = $id;",
                ct, transaction, ("$id", session.SessionId));
        }, ct);
        lock (_studioLoginGate) _studioPendingTotp.Remove(confirmedUserId!);
        return codes!;
    }

    public async Task VerifyStudioStepUpAsync(string? sessionToken, string? code, CancellationToken ct)
    {
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct)
                ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            await VerifyStudioFactorAsync(connection, transaction, session.User.Id, code, ct);
            await MarkStudioStepUpAsync(connection, transaction, session.SessionId, ct);
        }, ct);
    }

    private async Task MarkStudioStepUpAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, CancellationToken ct)
    {
        var updated = await ExecuteAsync(connection, """
            UPDATE studio_sessions SET step_up_verified_at = $now
             WHERE id = $id AND revoked_at IS NULL AND enrollment_only = 0
               AND (expires_at IS NULL OR expires_at > $now)
               AND (absolute_expires_at IS NULL OR absolute_expires_at > $now);
            """, ct, transaction, ("$now", Iso(UtcNow)), ("$id", sessionId));
        if (updated != 1)
            throw new StudioAuthenticationException("authentication-required", "Sign in again.");
    }

    private async Task ConsumeStudioStepUpAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, CancellationToken ct)
    {
        var consumed = await ExecuteAsync(connection, """
            UPDATE studio_sessions SET step_up_verified_at = NULL
             WHERE id = $id AND revoked_at IS NULL AND enrollment_only = 0
               AND step_up_verified_at >= $minimum AND step_up_verified_at <= $now;
            """, ct, transaction, ("$id", sessionId), ("$minimum", Iso(UtcNow.AddMinutes(-5))),
            ("$now", Iso(UtcNow)));
        if (consumed != 1)
            throw new StudioAuthenticationException("step-up-required", "Verify a second factor before managing sessions.");
    }

    private async Task VerifyStudioFactorAsync(SqliteConnection connection, SqliteTransaction transaction,
        string userId, string? code, CancellationToken ct)
    {
        await using var command = Command(connection,
            "SELECT totp_secret, recovery_hashes_json, last_accepted_totp_step FROM studio_factors WHERE user_id = $user;",
            transaction, ("$user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new StudioAuthenticationException("second-factor-required", "Register a second factor before signing in.");
        var secret = reader.IsDBNull(0) ? null : reader.GetString(0);
        var hashes = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
        var lastAcceptedStep = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
        await reader.DisposeAsync();
        if (secret is not null && SecondFactorCodes.MatchTotpStep(secret, code, UtcNow) is { } acceptedStep
            && (lastAcceptedStep is null || acceptedStep > lastAcceptedStep))
        {
            await ExecuteAsync(connection,
                "UPDATE studio_factors SET last_accepted_totp_step = $step WHERE user_id = $user;",
                ct, transaction, ("$step", acceptedStep), ("$user", userId));
            return;
        }
        var matched = code is null ? null : hashes.FirstOrDefault(hash => SecondFactorCodes.VerifyRecoveryCode(code, hash));
        if (matched is null)
            throw new StudioAuthenticationException(string.IsNullOrWhiteSpace(code) ? "second-factor-required" : "invalid-second-factor",
                "A valid second factor is required.");
        hashes.Remove(matched);
        await ExecuteAsync(connection, "UPDATE studio_factors SET recovery_hashes_json = $codes WHERE user_id = $user;",
            ct, transaction, ("$codes", JsonSerializer.Serialize(hashes)), ("$user", userId));
    }

    public async Task<IReadOnlyList<StudioSessionSummaryDto>> ListStudioSessionsAsync(string? sessionToken, string? targetUserId,
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var session = await ResolveStudioSessionAsync(connection, null, sessionToken, ct)
            ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
        var userId = targetUserId ?? session.User.Id;
        if (userId != session.User.Id && session.User.Role != StudioUserRoles.Owner)
            throw new StudioAuthorizationException("owner-required", "Owner role is required.");
        await using var command = Command(connection, """
            SELECT id, device, address, created_at, last_seen_at FROM studio_sessions
             WHERE user_id = $user AND revoked_at IS NULL
               AND (expires_at IS NULL OR expires_at > $now)
               AND (absolute_expires_at IS NULL OR absolute_expires_at > $now)
             ORDER BY last_seen_at DESC;
            """, ("$user", userId), ("$now", Iso(UtcNow)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StudioSessionSummaryDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new StudioSessionSummaryDto(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), Parse(reader.GetString(3)), Parse(reader.GetString(4)),
                reader.GetString(0) == session.SessionId));
        return result;
    }

    public async Task RevokeStudioSessionsAsync(string? sessionToken, string? targetUserId, string? targetSessionId,
        CancellationToken ct)
    {
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var session = await ResolveStudioSessionAsync(connection, transaction, sessionToken, ct)
                ?? throw new StudioAuthenticationException("authentication-required", "A studio session is required.");
            var userId = targetUserId ?? session.User.Id;
            if (userId != session.User.Id && session.User.Role != StudioUserRoles.Owner)
                throw new StudioAuthorizationException("owner-required", "Owner role is required.");
            if (targetUserId is not null)
                await ConsumeStudioStepUpAsync(connection, transaction, session.SessionId, ct);
            await ExecuteAsync(connection, """
                UPDATE studio_sessions SET revoked_at = $now
                 WHERE user_id = $user AND revoked_at IS NULL
                   AND ($target IS NULL OR id = $target)
                   AND ($target IS NOT NULL OR id != $current);
                """, ct, transaction, ("$now", Iso(UtcNow)), ("$user", userId),
                ("$target", targetSessionId), ("$current", session.SessionId));
        }, ct);
    }
}

public sealed class StudioThrottledException : Exception
{
    public StudioThrottledException() : base("Too many failed attempts. Try again later.") { }
}
