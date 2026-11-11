using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Security.Cryptography;

namespace AgentStudio.Security;

/// <summary>
/// Small-organization security store. The state file contains password, session,
/// enrollment, API token, and Runner credential hashes only. Plaintext credentials are
/// returned once by the operation that creates them and are never persisted.
/// Role bindings, groups, and service accounts live in the same state file;
/// see <c>Iam/AccessSecurityStore.Iam.cs</c>.
/// </summary>
public sealed partial class AccessSecurityStore
{
    public const string SessionCookieName = "__Host-agentstudio-session";
    // Browsers reject __Host- cookies over local HTTP. Only the local profile
    // uses this fallback name; the networked profile accepts __Host- only.
    public const string InsecureSessionCookieName = "agentstudio-session";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<AccessSecurityStore> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, LoginAttempt> _clientLoginAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoginAttempt> _networkLoginAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoginAttempt> _passwordChangeAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoginAttempt> _usernameLoginAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _recentSuccessfulLogins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _usernameClientDelays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingTotpSecrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Challenge, DateTime ExpiresAt)> _passkeyChallenges = new(StringComparer.Ordinal);
    private readonly LoginOverflowBudget _overflowClientAttempts = new();
    private readonly LoginOverflowBudget _overflowUsernameAttempts = new();
    private readonly LoginOverflowBudget _overflowDelays = new();
    private readonly HashSet<string> _clientsHashing = new(StringComparer.Ordinal);
    private DateTime _nextLoginPruneAt = DateTime.MinValue;
    private readonly SemaphoreSlim _passwordHashSlots;
    private readonly SemaphoreSlim _returningClientHashSlot = new(1, 1);
    private readonly Func<string, string, bool> _verifyPassword;
    private readonly Func<string, string> _hashPassword;
    private static readonly string DummyPasswordHash = PasswordSecretHasher.HashPassword(PasswordSecretHasher.RandomToken());
    private SecurityState? _state;
    private bool _lastSeenDirty;

    public AccessSecurityStore(IConfiguration configuration, ILogger<AccessSecurityStore> logger, TimeProvider? clock = null)
        : this(configuration, logger, clock, PasswordSecretHasher.VerifyPassword)
    {
    }

    internal AccessSecurityStore(IConfiguration configuration, ILogger<AccessSecurityStore> logger,
        TimeProvider? clock, Func<string, string, bool> verifyPassword, Func<string, string>? hashPassword = null)
    {
        _configuration = configuration;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _verifyPassword = verifyPassword;
        _hashPassword = hashPassword ?? PasswordSecretHasher.HashPassword;
        ControlPlaneAudit = new ControlPlaneAuditLog(SecurityFolder, _clock, logger);
        _passwordHashSlots = new SemaphoreSlim(Math.Clamp(
            configuration.GetValue("Security:LoginThrottle:MaxConcurrentHashes", Math.Max(2, Environment.ProcessorCount)),
            1, Math.Max(2, Environment.ProcessorCount)));
    }

    /// <summary>The control plane audit chain in <see cref="SecurityFolder"/> (security model SM-1).</summary>
    public ControlPlaneAuditLog ControlPlaneAudit { get; }

    public string SecurityFolder => Path.Combine(
        string.IsNullOrWhiteSpace(_configuration["TaskRepository"])
            ? Path.Combine(AppContext.BaseDirectory, "workspace")
            : _configuration["TaskRepository"]!, ".security");
    private string StatePath => Path.Combine(SecurityFolder, "access-state.json");
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private int MaxTrackedClients => Math.Clamp(_configuration.GetValue("Security:LoginThrottle:MaxTrackedClients", 8192), 1, 65536);
    private int MaxTrackedUsernames => Math.Clamp(_configuration.GetValue("Security:LoginThrottle:MaxTrackedUsernames", 8192), 1, 65536);
    private int MaxRecentSuccesses => Math.Clamp(_configuration.GetValue("Security:LoginThrottle:MaxRecentSuccesses", 8192), 1, 65536);
    private int MaxDelayEntries => Math.Clamp(_configuration.GetValue("Security:LoginThrottle:MaxDelayEntries", 8192), 1, 65536);
    private TimeSpan RecentSuccessWindow => TimeSpan.FromMinutes(_configuration.GetValue("Security:LoginThrottle:RecentSuccessMinutes", 1440));

    internal (int Clients, int Usernames, int RecentSuccesses, int Delays) LoginStateCounts
    {
        get { lock (_gate) return (_clientLoginAttempts.Count, _usernameLoginAttempts.Count,
            _recentSuccessfulLogins.Count, _usernameClientDelays.Count); }
    }

    public bool BootstrapRequired
    {
        get { lock (_gate) return StateLocked().Users.Count == 0; }
    }

    public (StudioUser User, string SessionToken, string CsrfToken) Bootstrap(BootstrapRequest request, bool publicEntry = false)
    {
        ValidateUsername(request.Username);
        ValidatePassword(request.Password, request.Username);
        lock (_gate)
        {
            var state = StateLocked();
            if (state.Users.Count != 0) throw new SecurityOperationException(409, "bootstrap-complete", "The first owner already exists.");
            var now = Now;
            var user = new StudioUser
            {
                Id = "usr_" + Guid.NewGuid().ToString("N"),
                Username = NormalizeUsername(request.Username),
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? (request.Username ?? string.Empty).Trim() : request.DisplayName.Trim(),
                Role = StudioRoles.Owner,
                PasswordHash = _hashPassword(request.Password ?? string.Empty),
                CreatedAt = now,
                PasswordChangedAt = now
            };
            state.Users.Add(user);
            var binding = NewBinding(IamPrincipalKind.User, user.Id,
                new IamGrantRequest(IamRoles.Owner, IamScope.Workspace), IamBindingSources.Bootstrap, user.Id);
            state.RoleBindings.Add(binding);
            state.IamVersion = CurrentIamVersion;
            var created = CreateSessionLocked(state, user, enrollmentOnly: publicEntry);
            SaveLocked(state);
            AuditBindings(user.Id, "binding.add", [binding]);
            return (user, created.Token, created.Csrf);
        }
    }

    public (StudioUser User, string SessionToken, string CsrfToken) Login(string username, string password, string clientKey,
        string? secondFactorCode = null, bool requireSecondFactor = false, string? device = null, string? address = null,
        PasskeyAssertionProof? passkey = null, string? origin = null, string? rpId = null)
    {
        // Reject oversized input before normalization can retain a user-controlled key.
        var normalized = (username?.Length ?? 0) > 64 ? null : NormalizeUsername(username);
        clientKey = string.IsNullOrWhiteSpace(clientKey) ? "unknown" : clientKey.Trim().ToLowerInvariant();
        if (clientKey.Length > 64)
            throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again later.");
        var loginKey = normalized is null ? null : normalized + "|" + clientKey;
        var networkKey = SecurityProfiles.IsNetworked(_configuration) ? NetworkThrottleKey(clientKey) : null;
        var overflowDelay = TimeSpan.FromSeconds(_configuration.GetValue("Security:LoginThrottle:UsernameDelaySeconds", 2));
        bool recentSuccess;
        StudioUser? candidate;
        lock (_gate)
        {
            var now = Now;
            PruneLoginStateLocked(now);
            recentSuccess = loginKey is not null && _recentSuccessfulLogins.TryGetValue(loginKey, out var lastSuccess)
                && now - lastSuccess <= RecentSuccessWindow;
            if (networkKey is not null && !recentSuccess
                && _networkLoginAttempts.TryGetValue(networkKey, out var networkAttempt)
                && networkAttempt.LockedUntil > now)
                throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again later.");
            if (_clientLoginAttempts.TryGetValue(clientKey, out var attempt) && attempt.LockedUntil > now)
                throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again later.");
            if (normalized is null || !IsValidUsername(normalized))
            {
                var recorded = RegisterLoginFailureLocked(_clientLoginAttempts, clientKey, now,
                    _configuration.GetValue("Security:LoginThrottle:ClientMaxFailures", 10),
                    TimeSpan.FromMinutes(_configuration.GetValue("Security:LoginThrottle:ClientLockoutMinutes", 15)), MaxTrackedClients);
                RecordNetworkFailureLocked(networkKey, now);
                throw recorded
                    ? new SecurityOperationException(401, "invalid-credentials", "Username or password is incorrect.")
                    : new SecurityOperationException(429, "login-throttled", "Login is busy. Try again shortly.");
            }
            candidate = StateLocked().Users.FirstOrDefault(x => x.Username == normalized);
            // Unknown and known usernames share every check below, so a failed login
            // cannot reveal whether the account exists. At capacity both spend hashes
            // only within the fixed-size overflow budgets.
            if (!_clientLoginAttempts.ContainsKey(clientKey)
                && (_clientLoginAttempts.Count >= MaxTrackedClients
                    || _overflowClientAttempts.HasActiveFailures(clientKey, now, LoginWindow))
                && !_overflowClientAttempts.CanHash(clientKey, clientKey, now, LoginWindow,
                    _configuration.GetValue("Security:LoginThrottle:ClientMaxFailures", 10), overflowDelay, ClientLockout))
                throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again shortly.");
            if (!_usernameLoginAttempts.ContainsKey(normalized)
                && (_usernameLoginAttempts.Count >= MaxTrackedUsernames
                    || _overflowUsernameAttempts.HasActiveFailures(normalized, now, LoginWindow))
                && (!_recentSuccessfulLogins.TryGetValue(loginKey!, out var overflowSuccessAt)
                    || now - overflowSuccessAt > RecentSuccessWindow)
                && !_overflowUsernameAttempts.CanHash(normalized, loginKey!, now, LoginWindow,
                    _configuration.GetValue("Security:LoginThrottle:UsernameMaxFailures", 20), overflowDelay))
                throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again shortly.");
            var usernameBudget = _configuration.GetValue("Security:LoginThrottle:UsernameMaxFailures", 20);
            if (_usernameLoginAttempts.TryGetValue(normalized, out var usernameAttempt)
                && usernameAttempt.Failures >= usernameBudget
                && now - usernameAttempt.WindowStartedAt <= LoginWindow
                && (!_recentSuccessfulLogins.TryGetValue(loginKey!, out var successAt)
                    || now - successAt > RecentSuccessWindow))
            {
                var delay = TimeSpan.FromSeconds(_configuration.GetValue("Security:LoginThrottle:UsernameDelaySeconds", 2));
                if (!_usernameClientDelays.TryGetValue(loginKey!, out var retryAt))
                {
                    if (!EnsureLoginCapacityLocked(_usernameClientDelays, loginKey!, MaxDelayEntries))
                    {
                        if (!_overflowDelays.PassedDelay(loginKey!, now, delay))
                            throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again shortly.");
                    }
                    else
                    {
                        _usernameClientDelays[loginKey!] = now + delay;
                        throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again shortly.");
                    }
                }
                if (retryAt > now)
                    throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again shortly.");
                _usernameClientDelays.Remove(loginKey!);
            }
            if (!_clientsHashing.Add(clientKey))
                throw new SecurityOperationException(429, "login-throttled", "Another login from this client is in progress.");
        }

        try
        {
            // Returning clients have one independent slot even when unknown clients
            // saturate the general pool. Neither wait holds the state lock.
            var reserved = false;
            if (!_passwordHashSlots.Wait(0))
            {
                if (!recentSuccess || !_returningClientHashSlot.Wait(TimeSpan.FromSeconds(2)))
                    throw new SecurityOperationException(429, "login-throttled", "Login is busy. Try again shortly.");
                reserved = true;
            }
            bool passwordMatches;
            try
            {
                var passwordCandidate = password ?? string.Empty;
                passwordMatches = passwordCandidate.Length <= 128 && _verifyPassword(
                    passwordCandidate, candidate?.PasswordHash ?? DummyPasswordHash);
            }
            finally { if (reserved) _returningClientHashSlot.Release(); else _passwordHashSlots.Release(); }

            lock (_gate)
            {
                var state = StateLocked();
                var current = state.Users.FirstOrDefault(x => x.Username == normalized);
                var now = Now;
                if (current is null || current.Disabled || !ReferenceEquals(current, candidate) || !passwordMatches)
                {
                    var clientRecorded = !_clientLoginAttempts.ContainsKey(clientKey)
                        && _overflowClientAttempts.HasActiveFailures(clientKey, now, LoginWindow)
                        ? false : RegisterLoginFailureLocked(_clientLoginAttempts, clientKey, now,
                            _configuration.GetValue("Security:LoginThrottle:ClientMaxFailures", 10),
                            TimeSpan.FromMinutes(_configuration.GetValue("Security:LoginThrottle:ClientLockoutMinutes", 15)), MaxTrackedClients);
                    var usernameRecorded = !_usernameLoginAttempts.ContainsKey(normalized)
                        && _overflowUsernameAttempts.HasActiveFailures(normalized, now, LoginWindow)
                        ? false : RegisterLoginFailureLocked(_usernameLoginAttempts, normalized, now,
                            _configuration.GetValue("Security:LoginThrottle:UsernameMaxFailures", 20), null, MaxTrackedUsernames);
                    if (!clientRecorded)
                        _overflowClientAttempts.RecordFailure(clientKey, clientKey, now, LoginWindow,
                            _configuration.GetValue("Security:LoginThrottle:ClientMaxFailures", 10), overflowDelay, ClientLockout);
                    RecordNetworkFailureLocked(networkKey, now);
                    if (!usernameRecorded)
                        _overflowUsernameAttempts.RecordFailure(normalized, loginKey!, now, LoginWindow,
                            _configuration.GetValue("Security:LoginThrottle:UsernameMaxFailures", 20), overflowDelay);
                    if (usernameRecorded && _usernameLoginAttempts[normalized].Failures >= _configuration.GetValue("Security:LoginThrottle:UsernameMaxFailures", 20)
                        && EnsureLoginCapacityLocked(_usernameClientDelays, loginKey!, MaxDelayEntries))
                        _usernameClientDelays[loginKey!] = now + TimeSpan.FromSeconds(_configuration.GetValue("Security:LoginThrottle:UsernameDelaySeconds", 2));
                    throw new SecurityOperationException(401, "invalid-credentials", "Username or password is incorrect.");
                }

                var hasFactor = !string.IsNullOrEmpty(current.TotpSecret) || current.Passkeys.Count > 0;
                var needsEnrollment = requireSecondFactor && RequiresSecondFactor(state, current.Id) && !hasFactor;
                if (hasFactor)
                {
                    try
                    {
                        if (passkey is not null)
                            VerifyPasskeyLocked(state, current, passkey, normalized + "|" + clientKey, origin, rpId);
                        else VerifySecondFactorLocked(state, current, secondFactorCode);
                    }
                    catch (SecurityOperationException ex) when (ex.Code is "invalid-second-factor" or "invalid-passkey")
                    {
                        RegisterLoginFailureLocked(_clientLoginAttempts, clientKey, now,
                            _configuration.GetValue("Security:LoginThrottle:ClientMaxFailures", 10), ClientLockout, MaxTrackedClients);
                        RegisterLoginFailureLocked(_usernameLoginAttempts, normalized, now,
                            _configuration.GetValue("Security:LoginThrottle:UsernameMaxFailures", 20), null, MaxTrackedUsernames);
                        RecordNetworkFailureLocked(networkKey, now);
                        throw;
                    }
                }

                _clientLoginAttempts.Remove(clientKey);
                if (!EnsureLoginCapacityLocked(_recentSuccessfulLogins, loginKey!, MaxRecentSuccesses))
                    _recentSuccessfulLogins.Remove(_recentSuccessfulLogins.MinBy(x => x.Value).Key);
                _recentSuccessfulLogins[loginKey!] = now;
                _usernameClientDelays.Remove(loginKey!);
                var created = CreateSessionLocked(state, current, device, address, needsEnrollment);
                SaveLocked(state);
                return (current, created.Token, created.Csrf);
            }
        }
        finally { lock (_gate) _clientsHashing.Remove(clientKey); }
    }

    public HumanPrincipal? AuthenticateSession(string? token, bool touch = true)
    {
        if (!TryTokenParts(token, "ssn", out var id)) return null;
        string tokenHash;
        lock (_gate)
        {
            var state = StateLocked();
            var session = state.Sessions.FirstOrDefault(x => x.Id == id);
            if (session is null) return null;
            if (session.ExpiresAt <= Now || session.AbsoluteExpiresAt <= Now)
            {
                state.Sessions.Remove(session);
                _lastSeenDirty = true;
                return null;
            }
            tokenHash = session.TokenHash;
        }
        // Secret verification is CPU intensive and must not hold the global gate.
        if (!PasswordSecretHasher.VerifySecret(token!, tokenHash)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var session = state.Sessions.FirstOrDefault(x => x.Id == id);
            if (session is null || session.TokenHash != tokenHash
                || session.ExpiresAt <= now || session.AbsoluteExpiresAt <= now) return null;
            var user = state.Users.FirstOrDefault(x => x.Id == session.UserId && !x.Disabled);
            if (user is null) return null;
            if (touch && now - session.LastSeenAt >= TimeSpan.FromMinutes(1))
            {
                var sliding = TimeSpan.FromMinutes(_configuration.GetValue("Security:Session:IdleMinutes", 480));
                var updated = session with { LastSeenAt = now, ExpiresAt = Min(now + sliding, session.AbsoluteExpiresAt) };
                state.Sessions[state.Sessions.IndexOf(session)] = updated;
                session = updated;
                _lastSeenDirty = true;
            }
            return new HumanPrincipal(user, session);
        }
    }

    public bool ValidateCsrf(StudioSession session, string? csrfToken)
        => !string.IsNullOrWhiteSpace(csrfToken) && PasswordSecretHasher.VerifySecret(csrfToken, session.CsrfHash);

    public void Logout(StudioSession session)
    {
        lock (_gate)
        {
            var state = StateLocked();
            state.Sessions.RemoveAll(x => x.Id == session.Id);
            SaveLocked(state);
        }
    }

    public string BeginTotpEnrollment(HumanPrincipal principal)
    {
        lock (_gate)
        {
            var state = StateLocked();
            if (!state.Users.Any(x => x.Id == principal.User.Id && !x.Disabled))
                throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
            var secret = SecondFactorCodes.NewTotpSecret();
            _pendingTotpSecrets[principal.User.Id] = secret;
            return secret;
        }
    }

    public IReadOnlyList<string> ConfirmTotpEnrollment(HumanPrincipal principal, string code)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var user = state.Users.FirstOrDefault(x => x.Id == principal.User.Id && !x.Disabled)
                ?? throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
            if (!_pendingTotpSecrets.TryGetValue(user.Id, out var secret)
                || !SecondFactorCodes.VerifyTotp(secret, code, Now))
                throw new SecurityOperationException(400, "invalid-second-factor", "The authenticator code is invalid.");
            var recovery = SecondFactorCodes.NewRecoveryCodes();
            state.Users[state.Users.IndexOf(user)] = user with
            {
                TotpSecret = secret,
                RecoveryCodeHashes = recovery.Select(SecondFactorCodes.HashRecoveryCode).ToList()
            };
            _pendingTotpSecrets.Remove(user.Id);
            var session = state.Sessions.FirstOrDefault(x => x.Id == principal.Session.Id);
            if (session is not null)
                state.Sessions[state.Sessions.IndexOf(session)] = session with { SecondFactorEnrollmentOnly = false };
            SaveLocked(state);
            return recovery;
        }
    }

    public void VerifyStepUp(HumanPrincipal principal, string? code)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var user = state.Users.FirstOrDefault(x => x.Id == principal.User.Id && !x.Disabled)
                ?? throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
            VerifySecondFactorLocked(state, user, code);
            MarkStepUpLocked(state, principal);
            SaveLocked(state);
        }
    }

    public string BeginPasskeyRegistration(HumanPrincipal principal)
    {
        lock (_gate)
        {
            var challenge = WebAuthnProofs.NewChallenge();
            StorePasskeyChallengeLocked("register:" + principal.Session.Id, challenge);
            return challenge;
        }
    }

    public IReadOnlyList<string> RegisterPasskey(HumanPrincipal principal, PasskeyRegistrationProof proof,
        string origin, string rpId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var user = state.Users.FirstOrDefault(x => x.Id == principal.User.Id && !x.Disabled)
                ?? throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
            var key = "register:" + principal.Session.Id;
            if (!_passkeyChallenges.Remove(key, out var pending) || pending.ExpiresAt <= Now)
                throw new SecurityOperationException(400, "passkey-challenge-expired", "Start passkey setup again.");
            string publicKey;
            try
            {
                WebAuthnProofs.VerifyClientData(proof.ClientDataJson, "webauthn.create", pending.Challenge, origin);
                publicKey = WebAuthnProofs.RegistrationPublicKey(proof.AuthenticatorData, proof.CredentialId, rpId);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
            {
                throw new SecurityOperationException(400, "invalid-passkey", "Passkey registration could not be verified.");
            }
            if (state.Users.Any(x => x.Passkeys.Any(p => p.Id == proof.CredentialId)))
                throw new SecurityOperationException(409, "passkey-exists", "This passkey is already registered.");
            var recovery = user.RecoveryCodeHashes.Count == 0 ? SecondFactorCodes.NewRecoveryCodes() : [];
            var name = proof.Name ?? "Passkey";
            state.Users[state.Users.IndexOf(user)] = user with
            {
                Passkeys = [.. user.Passkeys, new StudioPasskey(proof.CredentialId,
                    name.Length > 80 ? name[..80] : name, publicKey, 0, Now)],
                RecoveryCodeHashes = recovery.Count == 0 ? user.RecoveryCodeHashes : recovery.Select(SecondFactorCodes.HashRecoveryCode).ToList()
            };
            var session = state.Sessions.FirstOrDefault(x => x.Id == principal.Session.Id);
            if (session is not null)
                state.Sessions[state.Sessions.IndexOf(session)] = session with { SecondFactorEnrollmentOnly = false };
            SaveLocked(state);
            return recovery;
        }
    }

    public string BeginPasskeyAssertion(string username, string clientKey)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 64 || clientKey.Length > 64)
            throw new SecurityOperationException(400, "invalid-request", "Username is invalid.");
        lock (_gate)
        {
            var challenge = WebAuthnProofs.NewChallenge();
            StorePasskeyChallengeLocked(NormalizeUsername(username) + "|" + clientKey.Trim().ToLowerInvariant(), challenge);
            return challenge;
        }
    }

    public string BeginPasskeyStepUp(HumanPrincipal principal)
    {
        lock (_gate)
        {
            var challenge = WebAuthnProofs.NewChallenge();
            StorePasskeyChallengeLocked("step-up:" + principal.Session.Id, challenge);
            return challenge;
        }
    }

    public void VerifyPasskeyStepUp(HumanPrincipal principal, PasskeyAssertionProof proof, string origin, string rpId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var user = state.Users.FirstOrDefault(x => x.Id == principal.User.Id && !x.Disabled)
                ?? throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
            VerifyPasskeyLocked(state, user, proof, "step-up:" + principal.Session.Id, origin, rpId);
            MarkStepUpLocked(state, principal);
            SaveLocked(state);
        }
    }

    private void VerifyPasskeyLocked(SecurityState state, StudioUser user, PasskeyAssertionProof proof,
        string challengeKey, string? origin, string? rpId)
    {
        if (!_passkeyChallenges.Remove(challengeKey, out var pending) || pending.ExpiresAt <= Now)
            throw new SecurityOperationException(401, "passkey-challenge-expired", "Start passkey verification again.");
        var passkey = user.Passkeys.FirstOrDefault(x => x.Id == proof.CredentialId)
            ?? throw new SecurityOperationException(401, "invalid-passkey", "Passkey is not registered.");
        try
        {
            var count = WebAuthnProofs.VerifyAssertion(proof.ClientDataJson, proof.AuthenticatorData,
                proof.Signature, passkey.PublicKey, pending.Challenge, origin ?? string.Empty,
                rpId ?? string.Empty, passkey.SignCount);
            state.Users[state.Users.IndexOf(user)] = user with
            {
                Passkeys = user.Passkeys.Select(x => x.Id == passkey.Id ? x with { SignCount = count } : x).ToList()
            };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            throw new SecurityOperationException(401, "invalid-passkey", "Passkey verification failed.");
        }
    }

    private void StorePasskeyChallengeLocked(string key, string challenge)
    {
        foreach (var expired in _passkeyChallenges.Where(x => x.Value.ExpiresAt <= Now).Select(x => x.Key).ToArray())
            _passkeyChallenges.Remove(expired);
        if (_passkeyChallenges.Count >= 8192 && !_passkeyChallenges.ContainsKey(key))
            throw new SecurityOperationException(429, "passkey-busy", "Passkey verification is busy. Try again later.");
        _passkeyChallenges[key] = (challenge, Now.AddMinutes(5));
    }

    public IReadOnlyList<SessionResponse> ListSessions(HumanPrincipal principal, string? userId = null)
    {
        lock (_gate)
        {
            var target = userId ?? principal.User.Id;
            if (target != principal.User.Id && principal.User.Role != StudioRoles.Owner)
                throw new SecurityOperationException(403, "owner-required", "Owner role is required.");
            return StateLocked().Sessions.Where(x => x.UserId == target && x.ExpiresAt > Now && x.AbsoluteExpiresAt > Now)
                .OrderByDescending(x => x.LastSeenAt)
                .Select(x => new SessionResponse(x.Id, x.Device, x.Address, x.CreatedAt, x.LastSeenAt,
                    x.Id == principal.Session.Id)).ToArray();
        }
    }

    public void RevokeSession(HumanPrincipal principal, string sessionId, string? userId = null)
    {
        lock (_gate)
        {
            var target = userId ?? principal.User.Id;
            if (target != principal.User.Id && principal.User.Role != StudioRoles.Owner)
                throw new SecurityOperationException(403, "owner-required", "Owner role is required.");
            var state = StateLocked();
            if (!state.Sessions.Any(x => x.UserId == target && x.Id == sessionId))
                throw new SecurityOperationException(404, "session-not-found", "Session not found.");
            if (userId is not null) ConsumeStepUpLocked(state, principal);
            state.Sessions.RemoveAll(x => x.UserId == target && x.Id == sessionId);
            SaveLocked(state);
        }
    }

    public void RevokeOtherSessions(HumanPrincipal principal, string? userId = null)
    {
        lock (_gate)
        {
            var target = userId ?? principal.User.Id;
            if (target != principal.User.Id && principal.User.Role != StudioRoles.Owner)
                throw new SecurityOperationException(403, "owner-required", "Owner role is required.");
            var state = StateLocked();
            if (userId is not null) ConsumeStepUpLocked(state, principal);
            state.Sessions.RemoveAll(x => x.UserId == target && x.Id != principal.Session.Id);
            SaveLocked(state);
        }
    }

    private void MarkStepUpLocked(SecurityState state, HumanPrincipal principal)
    {
        var session = state.Sessions.FirstOrDefault(x => x.Id == principal.Session.Id && x.UserId == principal.User.Id)
            ?? throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
        if (session.SecondFactorEnrollmentOnly || session.ExpiresAt <= Now || session.AbsoluteExpiresAt <= Now)
            throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
        state.Sessions[state.Sessions.IndexOf(session)] = session with { StepUpVerifiedAt = Now };
    }

    private void ConsumeStepUpLocked(SecurityState state, HumanPrincipal principal,
        string message = "Verify a second factor before managing sessions.")
    {
        var session = state.Sessions.FirstOrDefault(x => x.Id == principal.Session.Id && x.UserId == principal.User.Id);
        if (session?.StepUpVerifiedAt is not { } verifiedAt || session.SecondFactorEnrollmentOnly
            || session.ExpiresAt <= Now || session.AbsoluteExpiresAt <= Now
            || verifiedAt > Now || Now - verifiedAt > TimeSpan.FromMinutes(5))
            throw new SecurityOperationException(403, "step-up-required", message);
        state.Sessions[state.Sessions.IndexOf(session)] = session with { StepUpVerifiedAt = null };
    }

    private void VerifySecondFactorLocked(SecurityState state, StudioUser user, string? code)
    {
        if (string.IsNullOrEmpty(user.TotpSecret) && user.Passkeys.Count == 0)
            throw new SecurityOperationException(403, "second-factor-required", "Register a second factor before signing in.");
        if (string.IsNullOrWhiteSpace(code))
            throw new SecurityOperationException(401, "second-factor-required", "A second factor is required.");
        if (user.TotpSecret is not null && SecondFactorCodes.VerifyTotp(user.TotpSecret, code, Now)) return;
        var recoveryHash = user.RecoveryCodeHashes.FirstOrDefault(hash => SecondFactorCodes.VerifyRecoveryCode(code, hash));
        if (recoveryHash is not null)
        {
            state.Users[state.Users.IndexOf(user)] = user with
            {
                RecoveryCodeHashes = user.RecoveryCodeHashes.Where(hash => hash != recoveryHash).ToList()
            };
            SaveLocked(state);
            return;
        }
        throw new SecurityOperationException(401, "invalid-second-factor", "The second factor is invalid.");
    }

    public StudioUser ChangePassword(HumanPrincipal principal, ChangePasswordRequest request)
    {
        ValidatePassword(request.NewPassword, principal.User.Username);
        StudioUser user;
        var userId = principal.User.Id;
        lock (_gate)
        {
            var state = StateLocked();
            user = state.Users.FirstOrDefault(x => x.Id == userId && !x.Disabled)
                ?? throw new SecurityOperationException(401, "authentication-required", "Sign in again.");
            if (_passwordChangeAttempts.TryGetValue(userId, out var attempt) && attempt.LockedUntil > Now)
                throw new SecurityOperationException(429, "password-change-throttled", "Too many failed attempts. Try again later.");
            if (!_clientsHashing.Add("password-change:" + userId))
                throw new SecurityOperationException(429, "password-change-throttled", "Password change is busy.");
        }
        try
        {
            var matches = (request.CurrentPassword?.Length ?? 0) <= 128
                && _verifyPassword(request.CurrentPassword ?? string.Empty, user.PasswordHash);
            var newHash = matches ? _hashPassword(request.NewPassword ?? string.Empty) : null;
            lock (_gate)
            {
                var state = StateLocked();
                var current = state.Users.FirstOrDefault(x => x.Id == userId && !x.Disabled);
                if (!ReferenceEquals(current, user))
                    throw new SecurityOperationException(409, "password-changed", "Password changed. Sign in again.");
                if (!matches)
                {
                    RegisterLoginFailureLocked(_passwordChangeAttempts, userId, Now,
                        _configuration.GetValue("Security:PasswordChange:MaxFailures", 5),
                        TimeSpan.FromMinutes(_configuration.GetValue("Security:PasswordChange:LockoutMinutes", 15)), MaxTrackedUsernames);
                    throw new SecurityOperationException(400, "current-password-invalid", "Current password is incorrect.");
                }
                _passwordChangeAttempts.Remove(userId);
                var updated = user with
                {
                    PasswordHash = newHash!,
                    PasswordChangedAt = Now,
                    MustChangePassword = false
                };
                state.Users[state.Users.IndexOf(user)] = updated;
                state.Sessions.RemoveAll(x => x.UserId == user.Id && x.Id != principal.Session.Id);
                SaveLocked(state);
                return updated;
            }
        }
        finally { lock (_gate) _clientsHashing.Remove("password-change:" + userId); }
    }

    public IReadOnlyList<StudioUser> ListUsers()
    {
        lock (_gate) return StateLocked().Users.OrderBy(x => x.Username).ToList();
    }

    public (StudioUser User, string TemporaryPassword) CreateUser(CreateUserRequest request, string? actingUserId = null)
    {
        ValidateUsername(request.Username);
        var grants = InitialGrants(request.Role, request.Projects, request.Bindings);
        var temporary = string.IsNullOrWhiteSpace(request.TemporaryPassword) ? GenerateTemporaryPassword() : request.TemporaryPassword;
        ValidatePassword(temporary, request.Username);
        lock (_gate)
        {
            var state = StateLocked();
            var username = NormalizeUsername(request.Username);
            if (state.Users.Any(x => x.Username == username)) throw new SecurityOperationException(409, "username-exists", "Username already exists.");
            var now = Now;
            var user = new StudioUser
            {
                Id = "usr_" + Guid.NewGuid().ToString("N"), Username = username,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? (request.Username ?? string.Empty).Trim() : request.DisplayName.Trim(), Role = request.Role,
                Projects = NormalizeProjects(request.Projects), PasswordHash = _hashPassword(temporary),
                MustChangePassword = true, CreatedAt = now, PasswordChangedAt = now
            };
            state.Users.Add(user);
            (user, var bindings) = AddUserGrantsLocked(state, user, grants, actingUserId);
            SaveLocked(state);
            AuditBindings(actingUserId, "binding.add", bindings);
            return (user, temporary);
        }
    }

    public (StudioUser User, string Token, DateTime ExpiresAt) InviteUser(InviteUserRequest request, string? actingUserId = null)
    {
        ValidateUsername(request.Username);
        var grants = InitialGrants(request.Role, request.Projects, request.Bindings);
        lock (_gate)
        {
            var state = StateLocked();
            var username = NormalizeUsername(request.Username);
            if (state.Users.Any(x => x.Username == username))
                throw new SecurityOperationException(409, "username-exists", "Username already exists.");
            var now = Now;
            var user = new StudioUser
            {
                Id = "usr_" + Guid.NewGuid().ToString("N"), Username = username,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(),
                Role = request.Role, Projects = NormalizeProjects(request.Projects),
                PasswordHash = _hashPassword(PasswordSecretHasher.RandomToken()),
                MustChangePassword = true, CreatedAt = now, PasswordChangedAt = now
            };
            var id = Guid.NewGuid().ToString("N");
            var token = Issued($"inv.{id}.{PasswordSecretHasher.RandomToken()}");
            var expiresAt = now.AddHours(24);
            state.Users.Add(user);
            (user, var bindings) = AddUserGrantsLocked(state, user, grants, actingUserId);
            state.Invitations.Add(new StudioInvitation
            {
                Id = id, UserId = user.Id, TokenHash = PasswordSecretHasher.HashSecret(token), ExpiresAt = expiresAt,
                InvitedBy = actingUserId
            });
            SaveLocked(state);
            AuditBindings(actingUserId, "binding.add", bindings);
            return (user, token, expiresAt);
        }
    }

    public (StudioUser User, string SessionToken, string CsrfToken) AcceptInvitation(AcceptInvitationRequest request, bool publicEntry = false)
    {
        if (!TryTokenParts(request.Token, "inv", out var id))
            throw new SecurityOperationException(401, "invalid-invitation", "Invitation is invalid or expired.");
        lock (_gate)
        {
            var state = StateLocked();
            var invitation = state.Invitations.FirstOrDefault(x => x.Id == id);
            if (invitation is null || invitation.UsedAt is not null || invitation.ExpiresAt <= Now
                || !PasswordSecretHasher.VerifySecret(request.Token, invitation.TokenHash))
                throw new SecurityOperationException(401, "invalid-invitation", "Invitation is invalid or expired.");
            var user = state.Users.FirstOrDefault(x => x.Id == invitation.UserId && !x.Disabled)
                ?? throw new SecurityOperationException(401, "invalid-invitation", "Invitation is invalid or expired.");
            ValidatePassword(request.NewPassword, user.Username);
            var updated = user with
            {
                PasswordHash = _hashPassword(request.NewPassword),
                MustChangePassword = false, PasswordChangedAt = Now
            };
            state.Users[state.Users.IndexOf(user)] = updated;
            state.Invitations[state.Invitations.IndexOf(invitation)] = invitation with { UsedAt = Now };
            var session = CreateSessionLocked(state, updated,
                enrollmentOnly: publicEntry && RequiresSecondFactor(state, updated.Id));
            SaveLocked(state);
            return (updated, session.Token, session.Csrf);
        }
    }

    public object PreviewInvitation(string token, AgentStudio.Registry.ProjectRegistry registry)
    {
        if (!TryTokenParts(token, "inv", out var id))
            throw new SecurityOperationException(401, "invalid-invitation", "Invitation is invalid or expired.");
        lock (_gate)
        {
            var state = StateLocked();
            var invitation = state.Invitations.FirstOrDefault(x => x.Id == id);
            if (invitation is null || invitation.UsedAt is not null || invitation.ExpiresAt <= Now
                || !PasswordSecretHasher.VerifySecret(token, invitation.TokenHash))
                throw new SecurityOperationException(401, "invalid-invitation", "Invitation is invalid or expired.");
            var user = state.Users.FirstOrDefault(x => x.Id == invitation.UserId && !x.Disabled)
                ?? throw new SecurityOperationException(401, "invalid-invitation", "Invitation is invalid or expired.");
            var bindings = state.RoleBindings.Where(x => x.PrincipalKind == IamPrincipalKind.User && x.PrincipalId == user.Id).ToList();
            var inviter = state.Users.FirstOrDefault(x => x.Id == invitation.InvitedBy)?.DisplayName ?? "Installation owner";
            return new { user.Username, user.DisplayName, Inviter = inviter,
                Role = string.Join(", ", bindings.Select(x => IamRoles.All.FirstOrDefault(role => role.Id == x.Role)?.DisplayName ?? x.Role).Distinct()),
                Projects = bindings.Select(x => x.Scope.Kind == IamScopeKind.Workspace ? "Workspace"
                        : registry.FindById(x.Scope.Project!)?.DisplayName ?? x.Scope.Project ?? "Workspace")
                    .Distinct().ToArray(),
                invitation.ExpiresAt, SecondFactorRequired = user.Role is StudioRoles.Owner or StudioRoles.Operator };
        }
    }

    /// <summary>
    /// Renames, disables, or assigns a legacy role. Validation runs on a copy
    /// and the change commits once: any refusal, last-owner protection
    /// included, leaves the state untouched. Bindings change only when the
    /// legacy role, projects, or approvals differ from the current summary.
    /// </summary>
    public StudioUser UpdateUser(string id, UpdateUserRequest request, string actingUserId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var user = next.Users.FirstOrDefault(x => x.Id == id)
                ?? throw new SecurityOperationException(404, "user-not-found", "User not found.");
            var disabled = request.Disabled ?? user.Disabled;
            if (id == actingUserId && disabled)
                throw new SecurityOperationException(409, "last-owner-protection", "An owner cannot disable or demote their current account.");
            var renamed = user with
            {
                DisplayName = request.DisplayName is null ? user.DisplayName : request.DisplayName.Trim(),
                Disabled = disabled,
            };
            var bindings = LegacyRoleBindings(next, user, request,
                grant => NewBinding(IamPrincipalKind.User, id, grant, IamBindingSources.RoleAssignment, actingUserId));
            var users = next.Users.Select(candidate => candidate.Id == id ? renamed : candidate).ToList();
            var previous = next.RoleBindings.Where(binding => binding.PrincipalKind == IamPrincipalKind.User && binding.PrincipalId == id).ToList();
            CommitIamLocked(next, users, bindings ?? next.RoleBindings.ToList(), next.Groups.ToList(), actingUserId);
            if (disabled) next.Sessions.RemoveAll(x => x.UserId == id);
            SaveLocked(next);
            if (request.Role is not null || request.Projects is not null || request.ProductionApprovalProjects is not null)
                AppendIamAudit(actingUserId, "user.legacy-role", "user", id, new
                {
                    request.Role, request.Projects, request.ProductionApprovalProjects,
                    bindings = bindings is null ? "unchanged" : "replaced",
                });
            if (bindings is not null)
            {
                AuditBindings(actingUserId, "binding.remove", previous);
                AuditBindings(actingUserId, "binding.add",
                    next.RoleBindings.Where(binding => binding.PrincipalKind == IamPrincipalKind.User && binding.PrincipalId == id));
            }
            if (disabled != user.Disabled)
                AppendIamAudit(actingUserId, disabled ? "user.disable" : "user.enable", "user", id, null);
            return next.Users.First(candidate => candidate.Id == id);
        }
    }

    public (StudioUser User, string TemporaryPassword) ResetPassword(string id, PasswordResetRequest request)
    {
        var temporary = string.IsNullOrWhiteSpace(request.TemporaryPassword) ? GenerateTemporaryPassword() : request.TemporaryPassword;
        lock (_gate)
        {
            var state = StateLocked();
            var user = state.Users.FirstOrDefault(x => x.Id == id)
                ?? throw new SecurityOperationException(404, "user-not-found", "User not found.");
            ValidatePassword(temporary, user.Username);
            var updated = user with { PasswordHash = _hashPassword(temporary), PasswordChangedAt = Now, MustChangePassword = true };
            state.Users[state.Users.IndexOf(user)] = updated;
            state.Sessions.RemoveAll(x => x.UserId == id);
            SaveLocked(state);
            return (updated, temporary);
        }
    }

    public (RunnerEnrollment Enrollment, string Code) CreateEnrollment(RunnerEnrollmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new SecurityOperationException(400, "name-required", "Runner name is required.");
        var scopes = ValidateScopes(request.Scopes);
        var now = Now;
        var expires = request.ExpiresAt ?? now.AddMinutes(15);
        if (expires <= now || expires > now.AddHours(24)) throw new SecurityOperationException(400, "invalid-expiry", "Enrollment expiry must be within the next 24 hours.");
        var id = Guid.NewGuid().ToString("N");
        var code = Issued($"enr.{id}.{PasswordSecretHasher.RandomToken()}");
        var enrollment = new RunnerEnrollment
        {
            Id = id, Name = request.Name.Trim(), CodeHash = PasswordSecretHasher.HashSecret(code), Scopes = scopes,
            CreatedAt = now, ExpiresAt = expires, CredentialExpiresAt = request.CredentialExpiresAt,
            // SM-9: an explicit project set; none named means none granted.
            Projects = NormalizeProjects(request.Projects)
        };
        lock (_gate) { var state = StateLocked(); state.Enrollments.Add(enrollment); SaveLocked(state); }
        return (enrollment, code);
    }

    public IReadOnlyList<RunnerEnrollment> ListEnrollments()
    {
        lock (_gate) return StateLocked().Enrollments.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public void RevokeEnrollment(string id)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var enrollment = state.Enrollments.FirstOrDefault(x => x.Id == id)
                ?? throw new SecurityOperationException(404, "enrollment-not-found", "Enrollment not found.");
            state.Enrollments[state.Enrollments.IndexOf(enrollment)] = enrollment with { ExpiresAt = Now };
            SaveLocked(state);
        }
    }

    public (RunnerServiceIdentity Runner, RunnerCredential Credential, string Secret) EnrollRunner(string code)
    {
        if (!TryTokenParts(code, "enr", out var id)) throw new SecurityOperationException(401, "invalid-enrollment", "Enrollment code is invalid.");
        lock (_gate)
        {
            var state = StateLocked();
            var enrollment = state.Enrollments.FirstOrDefault(x => x.Id == id);
            if (enrollment is null || enrollment.UsedAt is not null || enrollment.ExpiresAt <= Now
                || !PasswordSecretHasher.VerifySecret(code, enrollment.CodeHash))
                throw new SecurityOperationException(401, "invalid-enrollment", "Enrollment code is invalid, expired, or already used.");
            var runnerId = enrollment.Scopes.Contains(RunnerScopes.Orchestration, StringComparer.Ordinal)
                ? "engine_" + Guid.NewGuid().ToString("N")
                : "runner_" + Guid.NewGuid().ToString("N");
            var made = NewRunnerCredential(runnerId, enrollment.Scopes,
                enrollment.CredentialExpiresAt ?? DefaultRunnerCredentialExpiry());
            var runner = new RunnerServiceIdentity
            {
                Id = runnerId, Name = enrollment.Name, CreatedAt = Now, Credentials = [made.Credential],
                Projects = enrollment.Projects
            };
            state.Runners.Add(runner);
            state.Enrollments[state.Enrollments.IndexOf(enrollment)] = enrollment with { UsedAt = Now };
            SaveLocked(state);
            return (runner, made.Credential, made.Secret);
        }
    }

    public RunnerPrincipal? AuthenticateRunner(string? bearer)
    {
        if (!TryTokenParts(bearer, "rnr", out var credentialId)) return null;
        string secretHash;
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(r => r.Credentials.Any(c => c.Id == credentialId));
            var credential = runner?.Credentials.FirstOrDefault(c => c.Id == credentialId);
            if (runner is null || credential is null || runner.RevokedAt is not null || runner.RetiredAt is not null
                || credential.RevokedAt is not null || credential.ExpiresAt <= Now) return null;
            secretHash = credential.SecretHash;
        }
        // PBKDF2 is deliberately outside the store gate: concurrent requests
        // must not queue behind a credential hash or a filesystem save.
        if (!PasswordSecretHasher.VerifySecret(bearer!, secretHash)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(r => r.Credentials.Any(c => c.Id == credentialId));
            var credential = runner?.Credentials.FirstOrDefault(c => c.Id == credentialId);
            var now = Now;
            if (runner is null || credential is null || runner.RevokedAt is not null || runner.RetiredAt is not null
                || credential.RevokedAt is not null || credential.ExpiresAt <= now
                || credential.SecretHash != secretHash) return null;
            if (credential.LastUsedAt is null || now - credential.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                var updatedCredential = credential with { LastUsedAt = now };
                state.Runners[state.Runners.IndexOf(runner)] = runner with
                {
                    Credentials = runner.Credentials.Select(c => c.Id == credentialId ? updatedCredential : c).ToList()
                };
                _lastSeenDirty = true;
            }
            return new RunnerPrincipal(runner.Id, runner.Name, credential.Id,
                new HashSet<string>(credential.Scopes, StringComparer.Ordinal))
            {
                Projects = runner.Projects ?? [RunnerProjectBinding.AllProjects],
                CredentialExpiresAt = credential.ExpiresAt,
            };
        }
    }

    /// <summary>Persist authentication timestamps on the background flush tick.</summary>
    public void FlushLastSeen()
    {
        lock (_gate)
        {
            if (!_lastSeenDirty) return;
            try { SaveLocked(StateLocked()); _lastSeenDirty = false; }
            catch (Exception ex) { _logger.LogError(ex, "Failed to persist access last-seen timestamps"); }
        }
    }

    public IReadOnlyList<RunnerServiceIdentity> ListRunners()
    {
        lock (_gate) return StateLocked().Runners.OrderBy(x => x.Name).ToList();
    }

    public RunnerServiceIdentity? RecordRunnerActivity(
        string runnerId, int? activeSlots, int availableSlots, bool claimed)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(x => x.Id == runnerId);
            if (runner is null) return null;
            var now = Now;
            var reportedActive = activeSlots is null ? runner.ActiveSlots : Math.Max(0, activeSlots.Value);
            var retiredAt = runner.RetiredAt;
            if (runner.RetireRequestedAt is not null && reportedActive == 0)
                retiredAt ??= now;
            var updated = runner with
            {
                LastSeenAt = now,
                LastClaimAt = claimed ? now : runner.LastClaimAt,
                ActiveSlots = reportedActive,
                AvailableSlots = Math.Max(0, availableSlots),
                RetiredAt = retiredAt,
            };
            state.Runners[state.Runners.IndexOf(runner)] = updated;
            SaveLocked(state);
            return updated;
        }
    }

    public RunnerServiceIdentity RequestRunnerDrain(string runnerId, bool retireAfterDrain)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(x => x.Id == runnerId && x.RevokedAt is null && x.RetiredAt is null)
                ?? throw new SecurityOperationException(404, "runner-not-found", "Runner not found or no longer active.");
            var now = Now;
            var updated = runner with
            {
                DrainRequestedAt = runner.DrainRequestedAt ?? now,
                RetireRequestedAt = retireAfterDrain ? runner.RetireRequestedAt ?? now : runner.RetireRequestedAt,
                RetiredAt = retireAfterDrain && runner.ActiveSlots == 0 ? runner.RetiredAt ?? now : runner.RetiredAt,
            };
            state.Runners[state.Runners.IndexOf(runner)] = updated;
            SaveLocked(state);
            return updated;
        }
    }

    public bool RunnerAcceptsClaims(string runnerId)
    {
        lock (_gate)
        {
            var runner = StateLocked().Runners.FirstOrDefault(x => x.Id == runnerId);
            return runner is { RevokedAt: null, RetiredAt: null, DrainRequestedAt: null };
        }
    }

    public (RunnerServiceIdentity Runner, RunnerCredential Credential, string Secret) RotateRunner(string runnerId, RunnerRotateRequest request)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(x => x.Id == runnerId && x.RevokedAt is null)
                ?? throw new SecurityOperationException(404, "runner-not-found", "Runner not found.");
            var scopes = ValidateScopes(request.Scopes ?? runner.Credentials.LastOrDefault(x => x.RevokedAt is null)?.Scopes);
            var engineIdentity = runnerId.StartsWith("engine_", StringComparison.Ordinal);
            if (engineIdentity != scopes.Contains(RunnerScopes.Orchestration, StringComparer.Ordinal))
                throw new SecurityOperationException(400, "invalid-scope",
                    "Credential rotation cannot change a service principal's kind.");
            // Rotation keeps the previous credential until the Owner revokes it (overlap);
            // the new one starts the default lifetime unless the request names one.
            var made = NewRunnerCredential(runnerId, scopes, request.ExpiresAt ?? DefaultRunnerCredentialExpiry());
            var updated = runner with { Credentials = [.. runner.Credentials, made.Credential] };
            state.Runners[state.Runners.IndexOf(runner)] = updated;
            SaveLocked(state);
            return (updated, made.Credential, made.Secret);
        }
    }

    /// <summary>Replaces the projects a runner is bound to; this also ends a pre-binding notice.</summary>
    public RunnerServiceIdentity UpdateRunnerProjects(string runnerId, IReadOnlyList<string>? projects)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(x => x.Id == runnerId && x.RevokedAt is null)
                ?? throw new SecurityOperationException(404, "runner-not-found", "Runner not found.");
            var updated = runner with { Projects = NormalizeProjects(projects) };
            state.Runners[state.Runners.IndexOf(runner)] = updated;
            SaveLocked(state);
            return updated;
        }
    }

    /// <summary>SM-9, D7 A: a new runner credential lives 30 days unless configured otherwise.</summary>
    public TimeSpan RunnerCredentialLifetime
        => TimeSpan.FromDays(Math.Clamp(_configuration.GetValue("Security:RunnerCredentialLifetimeDays", 30), 1, 365));

    private DateTime DefaultRunnerCredentialExpiry() => Now + RunnerCredentialLifetime;

    public void RevokeCredential(string runnerId, string credentialId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(x => x.Id == runnerId)
                ?? throw new SecurityOperationException(404, "runner-not-found", "Runner not found.");
            if (!runner.Credentials.Any(x => x.Id == credentialId)) throw new SecurityOperationException(404, "credential-not-found", "Credential not found.");
            var updated = runner with { Credentials = runner.Credentials.Select(x => x.Id == credentialId ? x with { RevokedAt = Now } : x).ToList() };
            state.Runners[state.Runners.IndexOf(runner)] = updated;
            SaveLocked(state);
        }
    }

    public void RevokeRunner(string runnerId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var runner = state.Runners.FirstOrDefault(x => x.Id == runnerId)
                ?? throw new SecurityOperationException(404, "runner-not-found", "Runner not found.");
            state.Runners[state.Runners.IndexOf(runner)] = runner with { RevokedAt = Now };
            SaveLocked(state);
        }
    }

    public void AppendRunAudit(RunSecurityAuditEvent evt)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(SecurityFolder);
            RestrictDirectoryPermissions(SecurityFolder);
            var sanitized = evt with
            {
                Action = CredentialRedactor.Redact(evt.Action),
                TaskKey = CredentialRedactor.Redact(evt.TaskKey),
                Project = CredentialRedactor.Redact(evt.Project),
                InitiatingPrincipal = CredentialRedactor.Redact(evt.InitiatingPrincipal),
                ExecutingRunnerPrincipal = CredentialRedactor.Redact(evt.ExecutingRunnerPrincipal),
                Outcome = CredentialRedactor.Redact(evt.Outcome)
            };
            File.AppendAllText(Path.Combine(SecurityFolder, "run-audit.jsonl"), JsonSerializer.Serialize(sanitized) + Environment.NewLine);
            RestrictPermissions(Path.Combine(SecurityFolder, "run-audit.jsonl"));
            ControlPlaneAudit.Append(new ControlPlaneAuditRecord(
                ControlPlaneAuditStreams.Run, sanitized.InitiatingPrincipal, sanitized.Action, sanitized.Outcome ?? "recorded",
                Target: string.IsNullOrWhiteSpace(sanitized.Project) ? $"task:{sanitized.TaskKey}" : $"project:{sanitized.Project}/task:{sanitized.TaskKey}",
                Details: sanitized, At: sanitized.Timestamp));
        }
    }

    private (RunnerCredential Credential, string Secret) NewRunnerCredential(string runnerId, IReadOnlyList<string> scopes, DateTime? expiresAt)
    {
        if (expiresAt <= Now) throw new SecurityOperationException(400, "invalid-expiry", "Credential expiry must be in the future.");
        var id = Guid.NewGuid().ToString("N");
        var secret = Issued($"rnr.{id}.{PasswordSecretHasher.RandomToken()}");
        return (new RunnerCredential { Id = id, SecretHash = PasswordSecretHasher.HashSecret(secret), Scopes = scopes.ToList(), CreatedAt = Now, ExpiresAt = expiresAt }, secret);
    }

    private (StudioSession Session, string Token, string Csrf) CreateSessionLocked(SecurityState state, StudioUser user,
        string? device = null, string? address = null, bool enrollmentOnly = false)
    {
        var now = Now;
        var id = Guid.NewGuid().ToString("N");
        var token = Issued($"ssn.{id}.{PasswordSecretHasher.RandomToken()}");
        var csrf = Issued(PasswordSecretHasher.RandomToken());
        var absolute = now.AddMinutes(_configuration.GetValue("Security:Session:AbsoluteMinutes", 10080));
        var session = new StudioSession
        {
            Id = id, UserId = user.Id, TokenHash = PasswordSecretHasher.HashSecret(token), CsrfHash = PasswordSecretHasher.HashSecret(csrf),
            CreatedAt = now, LastSeenAt = now, Device = device, Address = address,
            SecondFactorEnrollmentOnly = enrollmentOnly,
            ExpiresAt = Min(now.AddMinutes(_configuration.GetValue("Security:Session:IdleMinutes", 480)), absolute), AbsoluteExpiresAt = absolute
        };
        state.Sessions.Add(session);
        return (session, token, csrf);
    }

    private TimeSpan LoginWindow => TimeSpan.FromMinutes(_configuration.GetValue("Security:LoginThrottle:WindowMinutes", 15));
    private TimeSpan ClientLockout => TimeSpan.FromMinutes(_configuration.GetValue("Security:LoginThrottle:ClientLockoutMinutes", 15));

    internal static string? NetworkThrottleKey(string clientKey)
    {
        var addressPart = clientKey.EndsWith("/64", StringComparison.Ordinal) ? clientKey[..^3] : clientKey;
        if (!IPAddress.TryParse(addressPart, out var address)) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        Array.Clear(bytes, 6, 10);
        return new IPAddress(bytes) + "/48";
    }

    private void RecordNetworkFailureLocked(string? key, DateTime now)
    {
        if (key is null) return;
        RegisterLoginFailureLocked(_networkLoginAttempts, key, now,
            _configuration.GetValue("Security:LoginThrottle:NetworkMaxFailures", 40), ClientLockout, MaxTrackedClients);
    }

    private bool RegisterLoginFailureLocked(Dictionary<string, LoginAttempt> attempts, string key, DateTime now,
        int maxFailures, TimeSpan? lockout, int capacity)
    {
        if (!EnsureLoginCapacityLocked(attempts, key, capacity)) return false;
        var prior = attempts.GetValueOrDefault(key);
        var newWindow = prior is null || now - prior.WindowStartedAt > LoginWindow;
        var failures = newWindow ? 1 : prior!.Failures + 1;
        attempts[key] = new LoginAttempt(failures, newWindow ? now : prior!.WindowStartedAt,
            failures >= maxFailures && lockout is { } duration ? now + duration : null);
        return true;
    }

    private static bool EnsureLoginCapacityLocked<T>(Dictionary<string, T> entries, string key, int capacity)
        => entries.ContainsKey(key) || entries.Count < capacity;

    private void PruneLoginStateLocked(DateTime now)
    {
        if (now < _nextLoginPruneAt) return;
        _nextLoginPruneAt = now.AddMinutes(1);
        foreach (var key in _clientLoginAttempts.Where(x => now - x.Value.WindowStartedAt > LoginWindow
            && (x.Value.LockedUntil is not { } until || until <= now)).Select(x => x.Key).ToArray())
            _clientLoginAttempts.Remove(key);
        foreach (var key in _usernameLoginAttempts.Where(x => now - x.Value.WindowStartedAt > LoginWindow)
            .Select(x => x.Key).ToArray())
            _usernameLoginAttempts.Remove(key);
        foreach (var key in _networkLoginAttempts.Where(x => now - x.Value.WindowStartedAt > LoginWindow
            && (x.Value.LockedUntil is not { } until || until <= now)).Select(x => x.Key).ToArray())
            _networkLoginAttempts.Remove(key);
        foreach (var key in _passwordChangeAttempts.Where(x => now - x.Value.WindowStartedAt > LoginWindow
            && (x.Value.LockedUntil is not { } until || until <= now)).Select(x => x.Key).ToArray())
            _passwordChangeAttempts.Remove(key);
        foreach (var key in _recentSuccessfulLogins.Where(x => now - x.Value > RecentSuccessWindow)
            .Select(x => x.Key).ToArray())
            _recentSuccessfulLogins.Remove(key);
        // Keep an elapsed delay long enough for a client to return and use it.
        foreach (var key in _usernameClientDelays.Where(x => now - x.Value > LoginWindow)
            .Select(x => x.Key).ToArray())
            _usernameClientDelays.Remove(key);
    }

    private SecurityState StateLocked()
    {
        if (_state is not null) return _state;
        Directory.CreateDirectory(SecurityFolder);
        RestrictDirectoryPermissions(SecurityFolder);
        var persisted = File.Exists(StatePath);
        SecurityState loaded;
        try { loaded = persisted ? JsonSerializer.Deserialize<SecurityState>(File.ReadAllText(StatePath), Json) ?? new() : new(); }
        catch (Exception ex) { throw new InvalidOperationException($"Security state at '{StatePath}' cannot be read safely.", ex); }
        if (!NeedsIamMigration(loaded))
        {
            _state = loaded;
            return _state;
        }

        // Migrate a copy. The live state is assigned only once the backup and
        // the save succeeded, so a crash in between retries from the
        // unchanged file on the next load.
        var fromVersion = loaded.IamVersion;
        var migrated = Copy(loaded);
        var tokenMigrations = MigrateToRoleBindings(migrated);
        if (!persisted)
        {
            _state = migrated;
            return _state;
        }
        var backup = WriteMigrationBackup(fromVersion);
        SaveLocked(migrated);
        AppendIamAudit(null, "migration", "state", $"v{fromVersion}-v{CurrentIamVersion}", new
        {
            users = migrated.Users.Count, bindings = migrated.RoleBindings.Count,
            serviceAccounts = migrated.ServiceAccounts.Count, backup = Path.GetFileName(backup),
        });
        AuditApiTokenRoleMigrations(tokenMigrations);
        _logger.LogInformation(
            "Migrated access state from IAM version {From} to {To}: {Users} users, {Bindings} bindings, {Accounts} service accounts; legacy copy at {Backup}",
            fromVersion, CurrentIamVersion, migrated.Users.Count, migrated.RoleBindings.Count, migrated.ServiceAccounts.Count, backup);
        return _state!;
    }

    private void SaveLocked(SecurityState state)
    {
        Directory.CreateDirectory(SecurityFolder);
        RestrictDirectoryPermissions(SecurityFolder);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        RestrictPermissions(temp);
        File.Move(temp, StatePath, true);
        RestrictPermissions(StatePath);
        _state = state;
    }

    private void RestrictPermissions(string path)
    {
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not restrict security file permissions for {Path}", path); }
    }

    private void RestrictDirectoryPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not restrict security directory permissions for {Path}", path); }
    }

    private static bool TryTokenParts(string? token, string prefix, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.', 3);
        if (parts.Length != 3 || parts[0] != prefix || parts[1].Length == 0 || parts[2].Length < 20) return false;
        id = parts[1];
        return true;
    }

    private static List<string> ValidateScopes(IReadOnlyList<string>? scopes)
    {
        var normalized = (scopes is null || scopes.Count == 0 ? RunnerScopes.Minimum : scopes)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var unknown = normalized.Where(x => !RunnerScopes.All.Contains(x)).ToList();
        if (unknown.Count > 0) throw new SecurityOperationException(400, "invalid-scope", $"Unknown Runner scope: {string.Join(", ", unknown)}.");
        // The replay scope is structurally exclusive so a public-demo credential
        // cannot be widened into an execution credential by configuration.
        if (normalized.Count > 1 && normalized.Any(RunnerScopes.IsExclusive))
            throw new SecurityOperationException(400, "invalid-scope", "Service-only scopes cannot be combined with other Runner scopes.");
        return normalized;
    }

    private static List<string> NormalizeProjects(IReadOnlyList<string>? projects)
        => projects?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    private static string NormalizeUsername(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static void ValidateUsername(string value)
    {
        var normalized = NormalizeUsername(value ?? string.Empty);
        if (!IsValidUsername(normalized))
            throw new SecurityOperationException(400, "invalid-username", "Username must be 3 to 64 letters, digits, dots, underscores, or hyphens.");
    }
    private static bool IsValidUsername(string normalized)
        => normalized.Length is >= 3 and <= 64
            && normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    private static void ValidatePassword(string? password, string username)
    {
        password ??= string.Empty;
        if (password.Length is < 12 or > 128) throw new SecurityOperationException(400, "password-policy", "Password must be 12 to 128 characters.");
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase)) throw new SecurityOperationException(400, "password-policy", "Password must not contain the username.");
    }
    private static string GenerateTemporaryPassword() => Issued("Tmp!" + PasswordSecretHasher.RandomToken(18));

    /// <summary>
    /// Registers a credential this store hands out with the redactor, so logs
    /// and reports mask the exact value even where no pattern matches.
    /// </summary>
    private static string Issued(string secret)
    {
        CredentialRedactor.RegisterLiteral(secret);
        return secret;
    }
    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
}

public sealed class SecurityOperationException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
