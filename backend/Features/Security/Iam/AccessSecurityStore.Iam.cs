using System.Text.Json;

namespace AgentStudio.Security;

public sealed record IamSnapshot(
    IReadOnlyList<StudioUser> Users,
    IReadOnlyList<StudioGroup> Groups,
    IReadOnlyList<ServiceAccount> ServiceAccounts,
    IReadOnlyList<RoleBinding> Bindings,
    IReadOnlyList<StudioInvitation> Invitations,
    int IamVersion);

/// <summary>
/// Rights model of the access store: role bindings, groups, service accounts,
/// the legacy migration, and permission checks against the live state. Every
/// mutation works on a copy, re-derives the legacy role summary of each user,
/// refuses to leave the installation without an active Owner, and replaces the
/// live state only after the file is saved. Each change is appended to the
/// IAM audit log.
/// </summary>
public sealed partial class AccessSecurityStore
{
    /// <summary>1: users hold role bindings. 2: API tokens are service accounts.</summary>
    public const int CurrentIamVersion = 2;
    public const string IamAuditFileName = "iam-audit.jsonl";
    public const string ServiceAccountAuditFileName = "service-account-audit.jsonl";
    public const string ApiTokenAuditFileName = "api-token-audit.jsonl";
    private const int MaxDescriptionLength = 500;
    private const int MaxTokenNameLength = 100;
    private static readonly TimeSpan DefaultServiceAccountTokenLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan MaxServiceAccountTokenLifetime = TimeSpan.FromDays(366);

    public IamSnapshot ReadIamSnapshot()
    {
        lock (_gate)
        {
            var state = StateLocked();
            return new IamSnapshot(
                state.Users.OrderBy(user => user.Username, StringComparer.Ordinal).ToList(),
                state.Groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                state.ServiceAccounts.OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                state.RoleBindings.ToList(),
                state.Invitations.ToList(),
                state.IamVersion);
        }
    }

    /// <summary>
    /// The decision subject and the bindings that apply to it, direct and
    /// through groups. A disabled or unknown service account has none.
    /// </summary>
    public (IamSubject Subject, IReadOnlyList<RoleBinding> Bindings) PrincipalBindings(IamPrincipalKind kind, string principalId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var subject = kind == IamPrincipalKind.ServiceAccount
                ? IamSubject.ForServiceAccount(principalId)
                : UserSubjectLocked(state, principalId);
            if (kind == IamPrincipalKind.ServiceAccount
                && !state.ServiceAccounts.Any(account => account.Id == principalId && account.DisabledAt is null))
                return (subject, []);
            return (subject, state.RoleBindings.Where(binding => PermissionEvaluator.AppliesTo(binding, subject)).ToList());
        }
    }

    public PermissionDecision AuthorizeUser(
        string userId, string permission, PermissionTarget target, Func<string, string, bool>? sameProject = null)
    {
        var (subject, bindings) = PrincipalBindings(IamPrincipalKind.User, userId);
        return PermissionEvaluator.Evaluate(permission, subject, bindings, target, sameProject);
    }

    public PermissionDecision AuthorizeServiceAccount(
        string accountId, string permission, PermissionTarget target, Func<string, string, bool>? sameProject = null)
        => CheckPermission(IamPrincipalKind.ServiceAccount, accountId, permission, target, sameProject);

    public PermissionDecision CheckPermission(
        IamPrincipalKind kind, string principalId, string permission, PermissionTarget target,
        Func<string, string, bool>? sameProject = null)
    {
        if (kind is not (IamPrincipalKind.User or IamPrincipalKind.ServiceAccount))
            throw new SecurityOperationException(400, "invalid-principal", "Check a user or a service account.");
        if (kind == IamPrincipalKind.ServiceAccount)
        {
            lock (_gate)
            {
                var account = StateLocked().ServiceAccounts.FirstOrDefault(candidate => candidate.Id == principalId);
                if (account is null) return PermissionDecision.Deny("Service account not found.");
                if (account.DisabledAt is not null) return PermissionDecision.Deny("The service account is disabled.");
            }
        }
        var (subject, bindings) = PrincipalBindings(kind, principalId);
        return PermissionEvaluator.Evaluate(permission, subject, bindings, target, sameProject);
    }

    /// <summary>True for a bearer this store issues to service accounts: <c>sat.*</c> or a Studio API token <c>api.*</c>.</summary>
    public static bool IsServiceAccountBearer(string? bearer)
        => bearer is not null
           && (bearer.StartsWith("sat.", StringComparison.Ordinal) || bearer.StartsWith("api.", StringComparison.Ordinal));

    /// <summary>
    /// Authenticates a <c>sat.*</c> service-account token or an <c>api.*</c>
    /// API token. An API token stays valid only while its creator is an active
    /// user, as before its migration.
    /// </summary>
    public ServiceAccountPrincipal? AuthenticateServiceAccount(string? bearer)
    {
        var prefix = bearer?.StartsWith("api.", StringComparison.Ordinal) == true ? "api" : "sat";
        if (!TryTokenParts(bearer, prefix, out var tokenId)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var account = state.ServiceAccounts.FirstOrDefault(candidate => candidate.Tokens.Any(token => token.Id == tokenId));
            var token = account?.Tokens.First(candidate => candidate.Id == tokenId);
            if (account is null || token is null || account.DisabledAt is not null || token.RevokedAt is not null
                || token.ExpiresAt <= now || !PasswordSecretHasher.VerifySecret(bearer!, token.SecretHash)) return null;
            var apiToken = account.Origin == ServiceAccountOrigins.ApiToken;
            if (apiToken != (prefix == "api")) return null;
            if (apiToken && !state.Users.Any(user => user.Id == account.CreatedBy && !user.Disabled)) return null;
            if (token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                var updated = account with
                {
                    Tokens = account.Tokens.Select(candidate => candidate.Id == token.Id ? candidate with { LastUsedAt = now } : candidate).ToList()
                };
                state.ServiceAccounts[state.ServiceAccounts.IndexOf(account)] = updated;
                SaveLocked(state);
            }
            return new ServiceAccountPrincipal(account.Id, account.Name, token.Id, account.Origin, account.CreatedBy);
        }
    }

    /// <summary>One line per service-account or API-token request, refused requests included.</summary>
    public void AppendServiceAccountAudit(ServiceAccountPrincipal principal, string method, string path, int status)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(SecurityFolder);
            var file = Path.Combine(SecurityFolder,
                principal.Origin == ServiceAccountOrigins.ApiToken ? ApiTokenAuditFileName : ServiceAccountAuditFileName);
            File.AppendAllText(file, JsonSerializer.Serialize(new
            {
                timestamp = Now, serviceAccountId = principal.AccountId, tokenId = principal.TokenId,
                tokenName = principal.AccountName, actor = principal.ActorId, createdBy = principal.CreatedBy,
                method, path, status,
            }) + Environment.NewLine);
            RestrictPermissions(file);
        }
    }

    // --- Groups ------------------------------------------------------------

    public StudioGroup CreateGroup(CreateGroupRequest request, string actingUserId)
    {
        var name = ValidateName(request.Name, "group");
        var description = ValidateDescription(request.Description);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            if (next.Groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityOperationException(409, "group-exists", "A group with this name already exists.");
            var group = new StudioGroup
            {
                Id = "grp_" + Guid.NewGuid().ToString("N"), Name = name, Description = description,
                Members = ValidMembersLocked(next, request.Members), CreatedAt = Now,
            };
            CommitIamLocked(next, next.RoleBindings.ToList(), [.. next.Groups, group], actingUserId);
            SaveLocked(next);
            AppendIamAudit(actingUserId, "group.create", "group", group.Id, new { group.Name, members = group.Members });
            return group;
        }
    }

    public StudioGroup UpdateGroup(string id, UpdateGroupRequest request, string actingUserId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var group = next.Groups.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "group-not-found", "Group not found.");
            var name = request.Name is null ? group.Name : ValidateName(request.Name, "group");
            if (next.Groups.Any(other => other.Id != id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityOperationException(409, "group-exists", "A group with this name already exists.");
            var updated = group with
            {
                Name = name,
                Description = request.Description is null ? group.Description : ValidateDescription(request.Description),
                Members = request.Members is null ? group.Members : ValidMembersLocked(next, request.Members),
            };
            CommitIamLocked(next, next.RoleBindings.ToList(),
                next.Groups.Select(candidate => candidate.Id == id ? updated : candidate).ToList(), actingUserId);
            SaveLocked(next);
            AppendIamAudit(actingUserId, "group.update", "group", id, new
            {
                updated.Name,
                membersAdded = updated.Members.Except(group.Members, StringComparer.Ordinal).ToList(),
                membersRemoved = group.Members.Except(updated.Members, StringComparer.Ordinal).ToList(),
            });
            return updated;
        }
    }

    public void DeleteGroup(string id, string actingUserId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            if (next.Groups.All(group => group.Id != id))
                throw new SecurityOperationException(404, "group-not-found", "Group not found.");
            var removed = next.RoleBindings.Where(binding => binding.PrincipalKind == IamPrincipalKind.Group && binding.PrincipalId == id).ToList();
            CommitIamLocked(next, next.RoleBindings.Except(removed).ToList(),
                next.Groups.Where(group => group.Id != id).ToList(), actingUserId);
            SaveLocked(next);
            AppendIamAudit(actingUserId, "group.delete", "group", id, new { removedBindings = removed.Select(binding => binding.Id).ToList() });
        }
    }

    // --- Role bindings -----------------------------------------------------

    public RoleBinding AddBinding(IamBindingRequest request, string actingUserId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequirePrincipalLocked(next, request.PrincipalKind, request.PrincipalId);
            var grant = ValidateGrant(request.PrincipalKind, new IamGrantRequest(request.Role, request.Scope));
            if (next.RoleBindings.Any(binding => binding.PrincipalKind == request.PrincipalKind
                && binding.PrincipalId == request.PrincipalId && binding.Role == grant.Role && SameScope(binding.Scope, grant.Scope)))
                throw new SecurityOperationException(409, "binding-exists", "This role binding already exists.");
            var binding = NewBinding(request.PrincipalKind, request.PrincipalId, grant, IamBindingSources.Manual, actingUserId);
            CommitIamLocked(next, [.. next.RoleBindings, binding], next.Groups.ToList(), actingUserId);
            SaveLocked(next);
            AuditBindings(actingUserId, "binding.add", [binding]);
            return binding;
        }
    }

    public void RemoveBinding(string id, string actingUserId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var binding = next.RoleBindings.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "binding-not-found", "Role binding not found.");
            CommitIamLocked(next, next.RoleBindings.Where(candidate => candidate.Id != id).ToList(), next.Groups.ToList(), actingUserId);
            SaveLocked(next);
            AuditBindings(actingUserId, "binding.remove", [binding]);
        }
    }

    // --- Service accounts --------------------------------------------------

    public ServiceAccount CreateServiceAccount(CreateServiceAccountRequest request, string actingUserId)
    {
        var name = ValidateName(request.Name, "service account");
        var description = ValidateDescription(request.Description);
        var grants = (request.Bindings ?? []).Select(grant => ValidateGrant(IamPrincipalKind.ServiceAccount, grant)).ToList();
        lock (_gate)
        {
            var next = Copy(StateLocked());
            if (next.ServiceAccounts.Any(account => account.Origin is null
                && string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityOperationException(409, "service-account-exists", "A service account with this name already exists.");
            var account = new ServiceAccount
            {
                Id = "sa_" + Guid.NewGuid().ToString("N"), Name = name, Description = description, CreatedAt = Now,
                CreatedBy = actingUserId,
            };
            var bindings = grants.Select(grant =>
                NewBinding(IamPrincipalKind.ServiceAccount, account.Id, grant, IamBindingSources.Manual, actingUserId)).ToList();
            next.ServiceAccounts.Add(account);
            next.RoleBindings.AddRange(bindings);
            SaveLocked(next);
            AppendIamAudit(actingUserId, "service-account.create", "service-account", account.Id, new { account.Name });
            AuditBindings(actingUserId, "binding.add", bindings);
            return account;
        }
    }

    public (ServiceAccount Account, ServiceAccountToken Token, string Secret) CreateServiceAccountToken(
        string id, CreateServiceAccountTokenRequest request, string? actingUserId = null)
    {
        var tokenName = ValidateTokenName(request.Name);
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var account = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == id && candidate.DisabledAt is null)
                ?? throw new SecurityOperationException(404, "service-account-not-found", "Service account not found or disabled.");
            if (account.Origin == ServiceAccountOrigins.ApiToken)
                throw new SecurityOperationException(409, "api-token-account",
                    "A migrated API token keeps its single token; issue a new API token instead.");
            var now = Now;
            var expiresAt = request.ExpiresAt ?? now + DefaultServiceAccountTokenLifetime;
            if (expiresAt <= now || expiresAt > now + MaxServiceAccountTokenLifetime)
                throw new SecurityOperationException(400, "invalid-expiry", "Token expiry must be in the future and at most one year away.");
            var (token, secret) = NewServiceAccountToken("sat", tokenName, now, expiresAt);
            var updated = account with { Tokens = [.. account.Tokens, token] };
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(account)] = updated;
            SaveLocked(next);
            AppendIamAudit(actingUserId, "token.issue", "service-account", id, new { tokenId = token.Id, token.Name, token.ExpiresAt });
            return (updated, token, secret);
        }
    }

    public void RevokeServiceAccountToken(string id, string tokenId, string? actingUserId = null)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var account = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "service-account-not-found", "Service account not found.");
            if (account.Tokens.All(token => token.Id != tokenId))
                throw new SecurityOperationException(404, "token-not-found", "Token not found.");
            var now = Now;
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(account)] = account with
            {
                Tokens = account.Tokens.Select(token => token.Id == tokenId ? token with { RevokedAt = token.RevokedAt ?? now } : token).ToList()
            };
            SaveLocked(next);
            AppendIamAudit(actingUserId, "token.revoke", "service-account", id, new { tokenId });
        }
    }

    public void DisableServiceAccount(string id, string? actingUserId = null)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var account = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "service-account-not-found", "Service account not found.");
            var now = Now;
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(account)] = account with
            {
                DisabledAt = account.DisabledAt ?? now,
                Tokens = account.Tokens.Select(token => token with { RevokedAt = token.RevokedAt ?? now }).ToList(),
            };
            SaveLocked(next);
            AppendIamAudit(actingUserId, "service-account.disable", "service-account", id, null);
        }
    }

    // --- API tokens (AGT-3186) as service accounts ------------------------

    /// <summary>
    /// Issues a Studio API token: a service account with one <c>api.*</c>
    /// token and the bindings of the legacy role and projects it was asked for.
    /// </summary>
    public (StudioApiToken Token, string Secret) CreateApiToken(CreateApiTokenRequest request, string creatorId)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > MaxTokenNameLength)
            throw new SecurityOperationException(400, "invalid-name", "Token name must be 1 to 100 characters.");
        if (request.Role is not StudioRoles.Operator and not StudioRoles.Viewer)
            throw new SecurityOperationException(400, "invalid-role", "API tokens must use the operator or viewer role.");
        var now = Now;
        if (request.ExpiresAt <= now || request.ExpiresAt > now.AddDays(365))
            throw new SecurityOperationException(400, "invalid-expiry", "Token expiry must be within the next 365 days.");
        var projects = NormalizeProjects(request.Projects);
        if (projects.Count == 0)
            throw new SecurityOperationException(400, "project-required", "Choose at least one project or the all-projects grant.");
        var grants = LegacyRoleMapping.Grants(request.Role, projects, null)
            .Select(grant => ValidateGrant(IamPrincipalKind.ServiceAccount, grant)).ToList();
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var (token, secret) = NewServiceAccountToken("api", request.Name.Trim(), now, request.ExpiresAt);
            var account = new ServiceAccount
            {
                Id = "sa_" + token.Id, Name = request.Name.Trim(), CreatedAt = now, CreatedBy = creatorId,
                Origin = ServiceAccountOrigins.ApiToken, Tokens = [token],
            };
            var bindings = grants.Select(grant =>
                NewBinding(IamPrincipalKind.ServiceAccount, account.Id, grant, IamBindingSources.RoleAssignment, creatorId)).ToList();
            next.ServiceAccounts.Add(account);
            next.RoleBindings.AddRange(bindings);
            SaveLocked(next);
            AppendIamAudit(creatorId, "token.issue", "service-account", account.Id,
                new { tokenId = token.Id, token.Name, token.ExpiresAt, origin = ServiceAccountOrigins.ApiToken });
            AuditBindings(creatorId, "binding.add", bindings);
            return (ApiTokenView(account, token, next.RoleBindings), secret);
        }
    }

    public IReadOnlyList<StudioApiToken> ListApiTokens()
    {
        lock (_gate)
        {
            var state = StateLocked();
            return state.ServiceAccounts.Where(account => account.Origin == ServiceAccountOrigins.ApiToken)
                .SelectMany(account => account.Tokens.Select(token => ApiTokenView(account, token, state.RoleBindings)))
                .OrderBy(token => token.Name, StringComparer.Ordinal).ToList();
        }
    }

    public void RevokeApiToken(string id, string? actingUserId = null)
    {
        string accountId;
        lock (_gate)
        {
            accountId = StateLocked().ServiceAccounts
                .FirstOrDefault(account => account.Origin == ServiceAccountOrigins.ApiToken && account.Tokens.Any(token => token.Id == id))?.Id
                ?? throw new SecurityOperationException(404, "token-not-found", "API token not found.");
        }
        RevokeServiceAccountToken(accountId, id, actingUserId);
    }

    private static StudioApiToken ApiTokenView(ServiceAccount account, ServiceAccountToken token, IEnumerable<RoleBinding> bindings)
    {
        var summary = LegacyRoleMapping.Summarize(bindings.Where(binding =>
            binding.PrincipalKind == IamPrincipalKind.ServiceAccount && binding.PrincipalId == account.Id));
        return new StudioApiToken
        {
            Id = token.Id, Name = account.Name, Role = summary.Role, Projects = summary.Projects,
            SecretHash = token.SecretHash, CreatedBy = account.CreatedBy ?? string.Empty, CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt ?? DateTime.MaxValue, LastUsedAt = token.LastUsedAt, RevokedAt = token.RevokedAt,
        };
    }

    private (ServiceAccountToken Token, string Secret) NewServiceAccountToken(string prefix, string? name, DateTime now, DateTime expiresAt)
    {
        var tokenId = Guid.NewGuid().ToString("N");
        var secret = $"{prefix}.{tokenId}.{PasswordSecretHasher.RandomToken()}";
        return (new ServiceAccountToken
        {
            Id = tokenId, Name = name, SecretHash = PasswordSecretHasher.HashSecret(secret), CreatedAt = now, ExpiresAt = expiresAt,
        }, secret);
    }

    // --- Migration and legacy role assignment ------------------------------

    private static bool NeedsIamMigration(SecurityState state)
        => state.IamVersion < CurrentIamVersion || state.ApiTokens.Count > 0;

    /// <summary>
    /// Migrates a loaded state (a copy; the caller assigns it only after the
    /// backup and the save succeed): legacy roles become user bindings
    /// (version 0), and API tokens become service accounts that keep their
    /// token id and secret hash, bound per <see cref="LegacyRoleMapping.Grants"/>.
    /// </summary>
    private void MigrateToRoleBindings(SecurityState state)
    {
        var now = Now;
        if (state.IamVersion < 1)
            state.RoleBindings.AddRange(LegacyRoleMapping.Migrate(state.Users, now, NewBindingId));
        foreach (var token in state.ApiTokens)
        {
            var id = "sa_" + token.Id;
            if (state.ServiceAccounts.Any(account => account.Id == id)) continue;
            state.ServiceAccounts.Add(new ServiceAccount
            {
                Id = id, Name = token.Name, Description = "Migrated API token.", CreatedAt = token.CreatedAt,
                CreatedBy = token.CreatedBy, Origin = ServiceAccountOrigins.ApiToken,
                Tokens =
                [
                    new ServiceAccountToken
                    {
                        Id = token.Id, Name = token.Name, SecretHash = token.SecretHash, CreatedAt = token.CreatedAt,
                        ExpiresAt = token.ExpiresAt, LastUsedAt = token.LastUsedAt, RevokedAt = token.RevokedAt,
                    },
                ],
            });
            state.RoleBindings.AddRange(LegacyRoleMapping.Grants(token.Role, token.Projects, null)
                .Where(grant => IamRoles.Find(grant.Role)?.ServiceAccountBindable == true)
                .Select(grant => new RoleBinding
                {
                    Id = NewBindingId(), PrincipalKind = IamPrincipalKind.ServiceAccount, PrincipalId = id,
                    Role = grant.Role, Scope = grant.Scope, Source = IamBindingSources.Migration,
                    CreatedAt = now, CreatedBy = IamBindingSources.Migration,
                }));
        }
        state.ApiTokens.Clear();
        state.IamVersion = CurrentIamVersion;
        ReprojectLocked(state);
    }

    /// <summary>A copy of the state file as it was before this migration, one per migration.</summary>
    private string WriteMigrationBackup(int fromVersion)
    {
        var stamp = Now.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var backup = Path.Combine(SecurityFolder, $"access-state.v{fromVersion}.{stamp}.json");
        for (var attempt = 2; File.Exists(backup); attempt++)
            backup = Path.Combine(SecurityFolder, $"access-state.v{fromVersion}.{stamp}-{attempt}.json");
        File.Copy(StatePath, backup);
        RestrictPermissions(backup);
        return backup;
    }

    private static IReadOnlyList<IamGrantRequest> InitialGrants(
        string? role, IReadOnlyList<string>? projects, IReadOnlyList<IamGrantRequest>? bindings)
    {
        if (bindings is { Count: > 0 })
            return bindings.Select(grant => ValidateGrant(IamPrincipalKind.User, grant)).ToList();
        if (!StudioRoles.IsValid(role))
            throw new SecurityOperationException(400, "invalid-role", "Role must be owner, operator, contributor, or viewer.");
        if (role == StudioRoles.Contributor && (projects is null || projects.Count == 0
            || projects.Contains(ProjectAccessAuthorization.AllProjectsGrant, StringComparer.Ordinal)))
            throw new SecurityOperationException(400, "invalid-projects", "Product users need explicit assigned projects.");
        return LegacyRoleMapping.Grants(role, projects, null);
    }

    private (StudioUser User, List<RoleBinding> Bindings) AddUserGrantsLocked(
        SecurityState state, StudioUser user, IEnumerable<IamGrantRequest> grants, string? actingUserId)
    {
        var bindings = grants.Select(grant =>
            NewBinding(IamPrincipalKind.User, user.Id, grant, IamBindingSources.RoleAssignment, actingUserId)).ToList();
        state.RoleBindings.AddRange(bindings);
        state.IamVersion = CurrentIamVersion;
        ReprojectLocked(state);
        return (state.Users.First(candidate => candidate.Id == user.Id), bindings);
    }

    /// <summary>
    /// The binding side of a legacy role PUT (review item 3). The bindings are
    /// never rewritten from an unchanged summary, and a user whose direct
    /// bindings a legacy role cannot express is refused with 409.
    /// </summary>
    private static List<RoleBinding>? LegacyRoleBindings(
        SecurityState state, StudioUser user, UpdateUserRequest request, Func<IamGrantRequest, RoleBinding> bind)
    {
        if (request.Role is null && request.Projects is null && request.ProductionApprovalProjects is null) return null;
        var role = request.Role ?? user.Role;
        if (!StudioRoles.IsValid(role)) throw new SecurityOperationException(400, "invalid-role", "Role must be owner, operator, contributor, or viewer.");
        var memberships = request.Projects is null ? user.Projects : NormalizeProjects(request.Projects);
        var approvals = role != StudioRoles.Contributor ? []
            : request.ProductionApprovalProjects is null ? user.ProductionApprovalProjects : NormalizeProjects(request.ProductionApprovalProjects);
        if (role == user.Role && SameProjects(memberships, user.Projects)
            && SameProjects(approvals, role == StudioRoles.Contributor ? user.ProductionApprovalProjects : []))
            return null;

        var direct = state.RoleBindings.Where(binding => binding.PrincipalKind == IamPrincipalKind.User && binding.PrincipalId == user.Id).ToList();
        if (!LegacyRoleMapping.IsLegacyRepresentable(direct, LegacyRoleMapping.Summarize(direct)))
            throw new SecurityOperationException(409, "bindings-managed",
                "This user's access is managed through role bindings. Change it in the Access area.");
        if (role == StudioRoles.Contributor && (memberships.Count == 0
            || memberships.Contains(ProjectAccessAuthorization.AllProjectsGrant, StringComparer.Ordinal)))
            throw new SecurityOperationException(400, "invalid-projects", "Product users need explicit assigned projects.");
        if (approvals.Any(project => project == ProjectAccessAuthorization.AllProjectsGrant
            || !memberships.Contains(project, StringComparer.OrdinalIgnoreCase)))
            throw new SecurityOperationException(400, "invalid-production-approval", "Production approval must name an assigned project.");
        return state.RoleBindings.Except(direct)
            .Concat(LegacyRoleMapping.Grants(role, memberships, approvals).Select(bind))
            .ToList();
    }

    private static bool SameProjects(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && !left.Except(right, StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>
    /// Applies a prospective binding and group set after the Owner checks:
    /// an installation that had an active Owner keeps one, and the acting
    /// Owner cannot remove their own Owner role. Callers pass a copy of the
    /// live state, so a refused change leaves the live state untouched.
    /// </summary>
    private static void CommitIamLocked(SecurityState state, List<RoleBinding> bindings, List<StudioGroup> groups, string? actingUserId)
        => CommitIamLocked(state, state.Users.ToList(), bindings, groups, actingUserId);

    private static void CommitIamLocked(
        SecurityState state, List<StudioUser> users, List<RoleBinding> bindings, List<StudioGroup> groups, string? actingUserId)
    {
        var before = ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups);
        var after = ActiveOwnerIds(users, bindings, groups);
        if (before.Count > 0 && after.Count == 0)
            throw new SecurityOperationException(409, "last-owner-protection", "At least one active owner is required.");
        if (actingUserId is not null && before.Contains(actingUserId) && !after.Contains(actingUserId))
            throw new SecurityOperationException(409, "last-owner-protection", "An owner cannot remove their own Owner role.");
        state.Users.Clear();
        state.Users.AddRange(users);
        state.RoleBindings.Clear();
        state.RoleBindings.AddRange(bindings);
        state.Groups.Clear();
        state.Groups.AddRange(groups);
        state.IamVersion = CurrentIamVersion;
        ReprojectLocked(state);
    }

    /// <summary>
    /// Re-derives the legacy summary of every user from its bindings. A user
    /// without any effective binding holds no permission and is summarized as
    /// a viewer of no project, never with a stale owner, operator, or "*".
    /// </summary>
    private static void ReprojectLocked(SecurityState state)
    {
        for (var index = 0; index < state.Users.Count; index++)
        {
            var user = state.Users[index];
            var summary = LegacyRoleMapping.Summarize(EffectiveBindings(user.Id, state.RoleBindings, state.Groups));
            state.Users[index] = user with
            {
                Role = summary.Role, Projects = summary.Projects, ProductionApprovalProjects = summary.ProductionApprovalProjects,
            };
        }
    }

    private static HashSet<string> ActiveOwnerIds(
        IEnumerable<StudioUser> users, IReadOnlyList<RoleBinding> bindings, IReadOnlyList<StudioGroup> groups)
        => users.Where(user => !user.Disabled && EffectiveBindings(user.Id, bindings, groups)
                .Any(binding => binding.Role == IamRoles.Owner && binding.Scope.Kind == IamScopeKind.Workspace))
            .Select(user => user.Id).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<RoleBinding> EffectiveBindings(
        string userId, IReadOnlyList<RoleBinding> bindings, IReadOnlyList<StudioGroup> groups)
    {
        var subject = new IamSubject(IamPrincipalKind.User, userId,
            groups.Where(group => group.Members.Contains(userId, StringComparer.Ordinal)).Select(group => group.Id)
                .ToHashSet(StringComparer.Ordinal));
        return bindings.Where(binding => PermissionEvaluator.AppliesTo(binding, subject));
    }

    private static IamSubject UserSubjectLocked(SecurityState state, string userId)
        => new(IamPrincipalKind.User, userId,
            state.Groups.Where(group => group.Members.Contains(userId, StringComparer.Ordinal)).Select(group => group.Id)
                .ToHashSet(StringComparer.Ordinal));

    private RoleBinding NewBinding(IamPrincipalKind kind, string principalId, IamGrantRequest grant, string source, string? actingUserId)
        => new()
        {
            Id = NewBindingId(), PrincipalKind = kind, PrincipalId = principalId, Role = grant.Role, Scope = grant.Scope,
            Source = source, CreatedAt = Now, CreatedBy = actingUserId,
        };

    private static string NewBindingId() => "rb_" + Guid.NewGuid().ToString("N");

    /// <summary>A detached copy to mutate; <see cref="SaveLocked"/> makes it the live state.</summary>
    private static SecurityState Copy(SecurityState state)
        => JsonSerializer.Deserialize<SecurityState>(JsonSerializer.Serialize(state, Json), Json)!;

    // --- Audit ---------------------------------------------------------------

    private void AuditBindings(string? actingUserId, string action, IEnumerable<RoleBinding> bindings)
    {
        foreach (var binding in bindings)
            AppendIamAudit(actingUserId, action, binding.PrincipalKind switch
            {
                IamPrincipalKind.Group => "group",
                IamPrincipalKind.ServiceAccount => "service-account",
                _ => "user",
            }, binding.PrincipalId, new { bindingId = binding.Id, binding.Role, binding.Scope, binding.Source });
    }

    /// <summary>
    /// Append-only IAM audit: who changed which binding, group, service
    /// account, token, or legacy role, and when (injected clock).
    /// </summary>
    private void AppendIamAudit(string? actingUserId, string action, string targetKind, string targetId, object? details)
    {
        Directory.CreateDirectory(SecurityFolder);
        var file = Path.Combine(SecurityFolder, IamAuditFileName);
        File.AppendAllText(file, JsonSerializer.Serialize(new
        {
            timestamp = Now, actor = actingUserId ?? "system", action, targetKind, targetId, details,
        }) + Environment.NewLine);
        RestrictPermissions(file);
    }

    /// <summary>Validates a role and scope for a principal kind and returns the normalized grant.</summary>
    public static IamGrantRequest ValidateGrant(IamPrincipalKind kind, IamGrantRequest grant)
    {
        var role = IamRoles.Find(grant.Role?.Trim().ToLowerInvariant())
            ?? throw new SecurityOperationException(400, "invalid-role",
                "Role must be one of " + string.Join(", ", IamRoles.All.Select(candidate => candidate.Id)) + ".");
        var scope = grant.Scope ?? throw new SecurityOperationException(400, "invalid-scope", "A scope is required.");
        if (!role.BindableScopes.Contains(scope.Kind))
            throw new SecurityOperationException(400, "invalid-scope",
                $"{role.DisplayName} cannot be bound to a {scope.Kind.ToString().ToLowerInvariant()} scope.");
        if (kind == IamPrincipalKind.ServiceAccount && !role.ServiceAccountBindable)
            throw new SecurityOperationException(400, "invalid-role", $"{role.DisplayName} cannot be bound to a service account.");
        var project = scope.Project?.Trim();
        var environment = IamEnvironments.Canonical(scope.Environment);
        var normalized = scope.Kind switch
        {
            IamScopeKind.Workspace => IamScope.Workspace,
            IamScopeKind.Project when IsScopeName(project) && project != ProjectAccessAuthorization.AllProjectsGrant
                => IamScope.ForProject(project!),
            IamScopeKind.Environment when IsScopeName(project) && project != ProjectAccessAuthorization.AllProjectsGrant
                && environment is not null => IamScope.ForEnvironment(project!, environment),
            _ => throw new SecurityOperationException(400, "invalid-scope",
                "A project scope names one project; an environment scope names a project and an environment kind: "
                + string.Join(", ", IamEnvironments.All) + "."),
        };
        return new IamGrantRequest(role.Id, normalized);
    }

    private static bool IsScopeName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;

    private static bool SameScope(IamScope left, IamScope right)
        => left.Kind == right.Kind
           && string.Equals(left.Project, right.Project, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Environment, right.Environment, StringComparison.OrdinalIgnoreCase);

    private static void RequirePrincipalLocked(SecurityState state, IamPrincipalKind kind, string? id)
    {
        var exists = kind switch
        {
            IamPrincipalKind.User => state.Users.Any(user => user.Id == id),
            IamPrincipalKind.Group => state.Groups.Any(group => group.Id == id),
            IamPrincipalKind.ServiceAccount => state.ServiceAccounts.Any(account => account.Id == id && account.DisabledAt is null),
            _ => false,
        };
        if (!exists) throw new SecurityOperationException(404, "principal-not-found", "User, group, or service account not found.");
    }

    private static List<string> ValidMembersLocked(SecurityState state, IReadOnlyList<string>? members)
    {
        var ids = members?.Where(member => !string.IsNullOrWhiteSpace(member)).Select(member => member.Trim())
            .Distinct(StringComparer.Ordinal).ToList() ?? [];
        var unknown = ids.Where(member => state.Users.All(user => user.Id != member)).ToList();
        if (unknown.Count > 0)
            throw new SecurityOperationException(400, "invalid-members", $"Unknown user id: {string.Join(", ", unknown)}.");
        return ids;
    }

    private static string ValidateName(string? value, string kind)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 64)
            throw new SecurityOperationException(400, "invalid-name", $"A {kind} name must be 1 to 64 characters.");
        return name;
    }

    private static string? ValidateDescription(string? value)
    {
        var description = value?.Trim();
        if (description?.Length > MaxDescriptionLength)
            throw new SecurityOperationException(400, "invalid-description", $"A description must be at most {MaxDescriptionLength} characters.");
        return string.IsNullOrEmpty(description) ? null : description;
    }

    private static string? ValidateTokenName(string? value)
    {
        var name = value?.Trim();
        if (name?.Length > MaxTokenNameLength)
            throw new SecurityOperationException(400, "invalid-name", $"A token name must be at most {MaxTokenNameLength} characters.");
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
