using System.Text.Json;
using System.Text.Json.Serialization;

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
    // __Host- verlangt Secure+HTTPS; im lokalen HTTP-Dev-Betrieb verwirft der
    // Browser solche Cookies stumm. HTTP-Requests bekommen daher diese Namen.
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
    private readonly Dictionary<string, LoginAttempt> _loginAttempts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string DummyPasswordHash = PasswordSecretHasher.HashPassword(PasswordSecretHasher.RandomToken());
    private SecurityState? _state;

    public AccessSecurityStore(IConfiguration configuration, ILogger<AccessSecurityStore> logger, TimeProvider? clock = null)
    {
        _configuration = configuration;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public string SecurityFolder => Path.Combine(
        string.IsNullOrWhiteSpace(_configuration["TaskRepository"])
            ? Path.Combine(AppContext.BaseDirectory, "workspace")
            : _configuration["TaskRepository"]!, ".security");
    private string StatePath => Path.Combine(SecurityFolder, "access-state.json");
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public bool BootstrapRequired
    {
        get { lock (_gate) return StateLocked().Users.Count == 0; }
    }

    public (StudioUser User, string SessionToken, string CsrfToken) Bootstrap(BootstrapRequest request)
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
                PasswordHash = PasswordSecretHasher.HashPassword(request.Password ?? string.Empty),
                CreatedAt = now,
                PasswordChangedAt = now
            };
            state.Users.Add(user);
            var binding = NewBinding(IamPrincipalKind.User, user.Id,
                new IamGrantRequest(IamRoles.Owner, IamScope.Workspace), IamBindingSources.Bootstrap, user.Id);
            state.RoleBindings.Add(binding);
            state.IamVersion = CurrentIamVersion;
            var created = CreateSessionLocked(state, user);
            SaveLocked(state);
            AuditBindings(user.Id, "binding.add", [binding]);
            return (user, created.Token, created.Csrf);
        }
    }

    public (StudioUser User, string SessionToken, string CsrfToken) Login(string username, string password, string throttleKey)
    {
        var normalized = NormalizeUsername(username ?? string.Empty);
        lock (_gate)
        {
            var now = Now;
            if (_loginAttempts.TryGetValue(throttleKey, out var attempt) && attempt.LockedUntil > now)
                throw new SecurityOperationException(429, "login-throttled", "Too many failed attempts. Try again later.");

            var state = StateLocked();
            var user = state.Users.FirstOrDefault(x => x.Username == normalized);
            var passwordCandidate = password ?? string.Empty;
            var passwordLengthAllowed = passwordCandidate.Length <= 128;
            var passwordMatches = passwordLengthAllowed && PasswordSecretHasher.VerifyPassword(
                passwordCandidate,
                user?.PasswordHash ?? DummyPasswordHash);
            var valid = user is not null && !user.Disabled && passwordMatches;
            if (!valid)
            {
                RegisterLoginFailureLocked(throttleKey, now);
                throw new SecurityOperationException(401, "invalid-credentials", "Username or password is incorrect.");
            }

            _loginAttempts.Remove(throttleKey);
            var created = CreateSessionLocked(state, user!);
            SaveLocked(state);
            return (user!, created.Token, created.Csrf);
        }
    }

    public HumanPrincipal? AuthenticateSession(string? token, bool touch = true)
    {
        if (!TryTokenParts(token, "ssn", out var id)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var session = state.Sessions.FirstOrDefault(x => x.Id == id);
            if (session is null || session.ExpiresAt <= now || session.AbsoluteExpiresAt <= now
                || !PasswordSecretHasher.VerifySecret(token!, session.TokenHash))
            {
                if (session is not null) { state.Sessions.Remove(session); SaveLocked(state); }
                return null;
            }
            var user = state.Users.FirstOrDefault(x => x.Id == session.UserId && !x.Disabled);
            if (user is null) return null;
            if (touch && now - session.LastSeenAt >= TimeSpan.FromMinutes(1))
            {
                var sliding = TimeSpan.FromMinutes(_configuration.GetValue("Security:Session:IdleMinutes", 480));
                var updated = session with { LastSeenAt = now, ExpiresAt = Min(now + sliding, session.AbsoluteExpiresAt) };
                state.Sessions[state.Sessions.IndexOf(session)] = updated;
                session = updated;
                SaveLocked(state);
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

    public StudioUser ChangePassword(HumanPrincipal principal, ChangePasswordRequest request)
    {
        ValidatePassword(request.NewPassword, principal.User.Username);
        lock (_gate)
        {
            var state = StateLocked();
            var user = state.Users.First(x => x.Id == principal.User.Id);
            if (!PasswordSecretHasher.VerifyPassword(request.CurrentPassword ?? string.Empty, user.PasswordHash))
                throw new SecurityOperationException(400, "current-password-invalid", "Current password is incorrect.");
            var updated = user with
            {
                PasswordHash = PasswordSecretHasher.HashPassword(request.NewPassword ?? string.Empty),
                PasswordChangedAt = Now,
                MustChangePassword = false
            };
            state.Users[state.Users.IndexOf(user)] = updated;
            state.Sessions.RemoveAll(x => x.UserId == user.Id && x.Id != principal.Session.Id);
            SaveLocked(state);
            return updated;
        }
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
                Projects = NormalizeProjects(request.Projects), PasswordHash = PasswordSecretHasher.HashPassword(temporary),
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
                PasswordHash = PasswordSecretHasher.HashPassword(PasswordSecretHasher.RandomToken()),
                MustChangePassword = true, CreatedAt = now, PasswordChangedAt = now
            };
            var id = Guid.NewGuid().ToString("N");
            var token = $"inv.{id}.{PasswordSecretHasher.RandomToken()}";
            var expiresAt = now.AddHours(24);
            state.Users.Add(user);
            (user, var bindings) = AddUserGrantsLocked(state, user, grants, actingUserId);
            state.Invitations.Add(new StudioInvitation
            {
                Id = id, UserId = user.Id, TokenHash = PasswordSecretHasher.HashSecret(token), ExpiresAt = expiresAt
            });
            SaveLocked(state);
            AuditBindings(actingUserId, "binding.add", bindings);
            return (user, token, expiresAt);
        }
    }

    public (StudioUser User, string SessionToken, string CsrfToken) AcceptInvitation(AcceptInvitationRequest request)
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
                PasswordHash = PasswordSecretHasher.HashPassword(request.NewPassword),
                MustChangePassword = false, PasswordChangedAt = Now
            };
            state.Users[state.Users.IndexOf(user)] = updated;
            state.Invitations[state.Invitations.IndexOf(invitation)] = invitation with { UsedAt = Now };
            var session = CreateSessionLocked(state, updated);
            SaveLocked(state);
            return (updated, session.Token, session.Csrf);
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
            var updated = user with { PasswordHash = PasswordSecretHasher.HashPassword(temporary), PasswordChangedAt = Now, MustChangePassword = true };
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
        var code = $"enr.{id}.{PasswordSecretHasher.RandomToken()}";
        var enrollment = new RunnerEnrollment
        {
            Id = id, Name = request.Name.Trim(), CodeHash = PasswordSecretHasher.HashSecret(code), Scopes = scopes,
            CreatedAt = now, ExpiresAt = expires, CredentialExpiresAt = request.CredentialExpiresAt
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
            var made = NewRunnerCredential(runnerId, enrollment.Scopes, enrollment.CredentialExpiresAt);
            var runner = new RunnerServiceIdentity { Id = runnerId, Name = enrollment.Name, CreatedAt = Now, Credentials = [made.Credential] };
            state.Runners.Add(runner);
            state.Enrollments[state.Enrollments.IndexOf(enrollment)] = enrollment with { UsedAt = Now };
            SaveLocked(state);
            return (runner, made.Credential, made.Secret);
        }
    }

    public RunnerPrincipal? AuthenticateRunner(string? bearer)
    {
        if (!TryTokenParts(bearer, "rnr", out var credentialId)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            foreach (var runner in state.Runners)
            {
                var credential = runner.Credentials.FirstOrDefault(x => x.Id == credentialId);
                if (credential is null) continue;
                if (runner.RevokedAt is not null || runner.RetiredAt is not null
                    || credential.RevokedAt is not null || credential.ExpiresAt <= now
                    || !PasswordSecretHasher.VerifySecret(bearer!, credential.SecretHash)) return null;
                if (credential.LastUsedAt is null || now - credential.LastUsedAt >= TimeSpan.FromMinutes(1))
                {
                    var updatedCredential = credential with { LastUsedAt = now };
                    var updatedRunner = runner with { Credentials = runner.Credentials.Select(x => x.Id == credential.Id ? updatedCredential : x).ToList() };
                    state.Runners[state.Runners.IndexOf(runner)] = updatedRunner;
                    SaveLocked(state);
                }
                return new RunnerPrincipal(runner.Id, runner.Name, credential.Id, new HashSet<string>(credential.Scopes, StringComparer.Ordinal));
            }
            return null;
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
            var made = NewRunnerCredential(runnerId, scopes, request.ExpiresAt);
            var updated = runner with { Credentials = [.. runner.Credentials, made.Credential] };
            state.Runners[state.Runners.IndexOf(runner)] = updated;
            SaveLocked(state);
            return (updated, made.Credential, made.Secret);
        }
    }

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
        }
    }

    private (RunnerCredential Credential, string Secret) NewRunnerCredential(string runnerId, IReadOnlyList<string> scopes, DateTime? expiresAt)
    {
        if (expiresAt <= Now) throw new SecurityOperationException(400, "invalid-expiry", "Credential expiry must be in the future.");
        var id = Guid.NewGuid().ToString("N");
        var secret = $"rnr.{id}.{PasswordSecretHasher.RandomToken()}";
        return (new RunnerCredential { Id = id, SecretHash = PasswordSecretHasher.HashSecret(secret), Scopes = scopes.ToList(), CreatedAt = Now, ExpiresAt = expiresAt }, secret);
    }

    private (StudioSession Session, string Token, string Csrf) CreateSessionLocked(SecurityState state, StudioUser user)
    {
        var now = Now;
        var id = Guid.NewGuid().ToString("N");
        var token = $"ssn.{id}.{PasswordSecretHasher.RandomToken()}";
        var csrf = PasswordSecretHasher.RandomToken();
        var absolute = now.AddMinutes(_configuration.GetValue("Security:Session:AbsoluteMinutes", 10080));
        var session = new StudioSession
        {
            Id = id, UserId = user.Id, TokenHash = PasswordSecretHasher.HashSecret(token), CsrfHash = PasswordSecretHasher.HashSecret(csrf),
            CreatedAt = now, LastSeenAt = now,
            ExpiresAt = Min(now.AddMinutes(_configuration.GetValue("Security:Session:IdleMinutes", 480)), absolute), AbsoluteExpiresAt = absolute
        };
        state.Sessions.Add(session);
        return (session, token, csrf);
    }

    private void RegisterLoginFailureLocked(string key, DateTime now)
    {
        var window = TimeSpan.FromMinutes(_configuration.GetValue("Security:LoginThrottle:WindowMinutes", 15));
        var max = _configuration.GetValue("Security:LoginThrottle:MaxFailures", 5);
        var lockout = TimeSpan.FromMinutes(_configuration.GetValue("Security:LoginThrottle:LockoutMinutes", 15));
        var prior = _loginAttempts.GetValueOrDefault(key);
        var failures = prior is null || now - prior.WindowStartedAt > window ? 1 : prior.Failures + 1;
        _loginAttempts[key] = new LoginAttempt(failures, prior is null || now - prior.WindowStartedAt > window ? now : prior.WindowStartedAt, failures >= max ? now + lockout : null);
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
        MigrateToRoleBindings(migrated);
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
        if (normalized.Length is < 3 or > 64 || normalized.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')))
            throw new SecurityOperationException(400, "invalid-username", "Username must be 3 to 64 letters, digits, dots, underscores, or hyphens.");
    }
    private static void ValidatePassword(string? password, string username)
    {
        password ??= string.Empty;
        if (password.Length is < 12 or > 128) throw new SecurityOperationException(400, "password-policy", "Password must be 12 to 128 characters.");
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase)) throw new SecurityOperationException(400, "password-policy", "Password must not contain the username.");
    }
    private static string GenerateTemporaryPassword() => "Tmp!" + PasswordSecretHasher.RandomToken(18);
    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
}

public sealed class SecurityOperationException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
