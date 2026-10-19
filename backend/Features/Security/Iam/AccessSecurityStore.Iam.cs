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
/// mutation re-derives the legacy role summary of each user and refuses to
/// leave the installation without an active Owner.
/// </summary>
public sealed partial class AccessSecurityStore
{
    public const int CurrentIamVersion = 1;
    private const string LegacyStateBackupName = "access-state.v0.json";
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

    public PermissionDecision AuthorizeUser(
        string userId, string permission, PermissionTarget target, Func<string, string, bool>? sameProject = null)
    {
        lock (_gate)
        {
            var state = StateLocked();
            return PermissionEvaluator.Evaluate(permission, UserSubjectLocked(state, userId), state.RoleBindings, target, sameProject);
        }
    }

    public string? StrongestUserRole(string userId, PermissionTarget target, Func<string, string, bool>? sameProject = null)
    {
        lock (_gate)
        {
            var state = StateLocked();
            return PermissionEvaluator.StrongestRole(UserSubjectLocked(state, userId), state.RoleBindings, target, sameProject);
        }
    }

    public bool UserHoldsRole(string userId, string role)
    {
        lock (_gate)
        {
            var state = StateLocked();
            return EffectiveBindings(userId, state.RoleBindings, state.Groups).Any(binding => binding.Role == role);
        }
    }

    public PermissionDecision AuthorizeServiceAccount(
        string accountId, string permission, PermissionTarget target, Func<string, string, bool>? sameProject = null)
    {
        lock (_gate)
        {
            var state = StateLocked();
            return PermissionEvaluator.Evaluate(permission, IamSubject.ForServiceAccount(accountId), state.RoleBindings, target, sameProject);
        }
    }

    public PermissionDecision CheckPermission(
        IamPrincipalKind kind, string principalId, string permission, PermissionTarget target,
        Func<string, string, bool>? sameProject = null)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var subject = kind switch
            {
                IamPrincipalKind.User => UserSubjectLocked(state, principalId),
                IamPrincipalKind.ServiceAccount => IamSubject.ForServiceAccount(principalId),
                _ => throw new SecurityOperationException(400, "invalid-principal", "Check a user or a service account."),
            };
            return PermissionEvaluator.Evaluate(permission, subject, state.RoleBindings, target, sameProject);
        }
    }

    public ServiceAccountPrincipal? AuthenticateServiceAccount(string? bearer)
    {
        if (!TryTokenParts(bearer, "sat", out var tokenId)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var account = state.ServiceAccounts.FirstOrDefault(candidate => candidate.Tokens.Any(token => token.Id == tokenId));
            var token = account?.Tokens.First(candidate => candidate.Id == tokenId);
            if (account is null || token is null || account.DisabledAt is not null || token.RevokedAt is not null
                || token.ExpiresAt <= now || !PasswordSecretHasher.VerifySecret(bearer!, token.SecretHash)) return null;
            if (token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                var updated = account with
                {
                    Tokens = account.Tokens.Select(candidate => candidate.Id == token.Id ? candidate with { LastUsedAt = now } : candidate).ToList()
                };
                state.ServiceAccounts[state.ServiceAccounts.IndexOf(account)] = updated;
                SaveLocked(state);
            }
            return new ServiceAccountPrincipal(account.Id, account.Name, token.Id);
        }
    }

    // --- Groups ------------------------------------------------------------

    public StudioGroup CreateGroup(CreateGroupRequest request, string actingUserId)
    {
        var name = ValidateName(request.Name, "group");
        lock (_gate)
        {
            var state = StateLocked();
            if (state.Groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityOperationException(409, "group-exists", "A group with this name already exists.");
            var group = new StudioGroup
            {
                Id = "grp_" + Guid.NewGuid().ToString("N"), Name = name, Description = request.Description?.Trim(),
                Members = ValidMembersLocked(state, request.Members), CreatedAt = Now,
            };
            CommitIamLocked(state, state.RoleBindings.ToList(), [.. state.Groups, group], actingUserId);
            SaveLocked(state);
            return group;
        }
    }

    public StudioGroup UpdateGroup(string id, UpdateGroupRequest request, string actingUserId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var group = state.Groups.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "group-not-found", "Group not found.");
            var name = request.Name is null ? group.Name : ValidateName(request.Name, "group");
            if (state.Groups.Any(other => other.Id != id && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityOperationException(409, "group-exists", "A group with this name already exists.");
            var updated = group with
            {
                Name = name,
                Description = request.Description is null ? group.Description : request.Description.Trim(),
                Members = request.Members is null ? group.Members : ValidMembersLocked(state, request.Members),
            };
            CommitIamLocked(state, state.RoleBindings.ToList(),
                state.Groups.Select(candidate => candidate.Id == id ? updated : candidate).ToList(), actingUserId);
            SaveLocked(state);
            return updated;
        }
    }

    public void DeleteGroup(string id, string actingUserId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            if (state.Groups.All(group => group.Id != id))
                throw new SecurityOperationException(404, "group-not-found", "Group not found.");
            CommitIamLocked(state,
                state.RoleBindings.Where(binding => !(binding.PrincipalKind == IamPrincipalKind.Group && binding.PrincipalId == id)).ToList(),
                state.Groups.Where(group => group.Id != id).ToList(), actingUserId);
            SaveLocked(state);
        }
    }

    // --- Role bindings -----------------------------------------------------

    public RoleBinding AddBinding(IamBindingRequest request, string actingUserId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            RequirePrincipalLocked(state, request.PrincipalKind, request.PrincipalId);
            var grant = ValidateGrant(request.PrincipalKind, new IamGrantRequest(request.Role, request.Scope));
            if (state.RoleBindings.Any(binding => binding.PrincipalKind == request.PrincipalKind
                && binding.PrincipalId == request.PrincipalId && binding.Role == grant.Role && SameScope(binding.Scope, grant.Scope)))
                throw new SecurityOperationException(409, "binding-exists", "This role binding already exists.");
            var binding = NewBinding(request.PrincipalKind, request.PrincipalId, grant, IamBindingSources.Manual, actingUserId);
            CommitIamLocked(state, [.. state.RoleBindings, binding], state.Groups.ToList(), actingUserId);
            SaveLocked(state);
            return binding;
        }
    }

    public void RemoveBinding(string id, string actingUserId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            if (state.RoleBindings.All(binding => binding.Id != id))
                throw new SecurityOperationException(404, "binding-not-found", "Role binding not found.");
            CommitIamLocked(state, state.RoleBindings.Where(binding => binding.Id != id).ToList(), state.Groups.ToList(), actingUserId);
            SaveLocked(state);
        }
    }

    // --- Service accounts --------------------------------------------------

    public ServiceAccount CreateServiceAccount(CreateServiceAccountRequest request, string actingUserId)
    {
        var name = ValidateName(request.Name, "service account");
        var grants = (request.Bindings ?? []).Select(grant => ValidateGrant(IamPrincipalKind.ServiceAccount, grant)).ToList();
        lock (_gate)
        {
            var state = StateLocked();
            if (state.ServiceAccounts.Any(account => string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new SecurityOperationException(409, "service-account-exists", "A service account with this name already exists.");
            var account = new ServiceAccount
            {
                Id = "sa_" + Guid.NewGuid().ToString("N"), Name = name, Description = request.Description?.Trim(), CreatedAt = Now,
            };
            state.ServiceAccounts.Add(account);
            state.RoleBindings.AddRange(grants.Select(grant =>
                NewBinding(IamPrincipalKind.ServiceAccount, account.Id, grant, IamBindingSources.Manual, actingUserId)));
            SaveLocked(state);
            return account;
        }
    }

    public (ServiceAccount Account, ServiceAccountToken Token, string Secret) CreateServiceAccountToken(
        string id, CreateServiceAccountTokenRequest request)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var account = state.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == id && candidate.DisabledAt is null)
                ?? throw new SecurityOperationException(404, "service-account-not-found", "Service account not found or disabled.");
            var now = Now;
            var expiresAt = request.ExpiresAt ?? now + DefaultServiceAccountTokenLifetime;
            if (expiresAt <= now || expiresAt > now + MaxServiceAccountTokenLifetime)
                throw new SecurityOperationException(400, "invalid-expiry", "Token expiry must be in the future and at most one year away.");
            var tokenId = Guid.NewGuid().ToString("N");
            var secret = $"sat.{tokenId}.{PasswordSecretHasher.RandomToken()}";
            var token = new ServiceAccountToken
            {
                Id = tokenId, Name = request.Name?.Trim(), SecretHash = PasswordSecretHasher.HashSecret(secret),
                CreatedAt = now, ExpiresAt = expiresAt,
            };
            var updated = account with { Tokens = [.. account.Tokens, token] };
            state.ServiceAccounts[state.ServiceAccounts.IndexOf(account)] = updated;
            SaveLocked(state);
            return (updated, token, secret);
        }
    }

    public void RevokeServiceAccountToken(string id, string tokenId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var account = state.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "service-account-not-found", "Service account not found.");
            if (account.Tokens.All(token => token.Id != tokenId))
                throw new SecurityOperationException(404, "token-not-found", "Token not found.");
            var now = Now;
            state.ServiceAccounts[state.ServiceAccounts.IndexOf(account)] = account with
            {
                Tokens = account.Tokens.Select(token => token.Id == tokenId ? token with { RevokedAt = token.RevokedAt ?? now } : token).ToList()
            };
            SaveLocked(state);
        }
    }

    public void DisableServiceAccount(string id)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var account = state.ServiceAccounts.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new SecurityOperationException(404, "service-account-not-found", "Service account not found.");
            var now = Now;
            state.ServiceAccounts[state.ServiceAccounts.IndexOf(account)] = account with
            {
                DisabledAt = account.DisabledAt ?? now,
                Tokens = account.Tokens.Select(token => token with { RevokedAt = token.RevokedAt ?? now }).ToList(),
            };
            SaveLocked(state);
        }
    }

    // --- Migration and legacy role assignment ------------------------------

    private void MigrateToRoleBindingsLocked(SecurityState state)
    {
        var persisted = File.Exists(StatePath);
        if (state.RoleBindings.Count == 0)
            state.RoleBindings.AddRange(LegacyRoleMapping.Migrate(state.Users, Now, NewBindingId));
        state.IamVersion = CurrentIamVersion;
        if (!persisted) return;
        var backup = Path.Combine(SecurityFolder, LegacyStateBackupName);
        if (!File.Exists(backup))
        {
            File.Copy(StatePath, backup);
            RestrictPermissions(backup);
        }
        SaveLocked(state);
        _logger.LogInformation(
            "Migrated access state to role bindings: {Users} users, {Bindings} bindings; legacy copy at {Backup}",
            state.Users.Count, state.RoleBindings.Count, backup);
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

    private StudioUser AddUserGrantsLocked(SecurityState state, StudioUser user, IEnumerable<IamGrantRequest> grants)
    {
        state.RoleBindings.AddRange(grants.Select(grant =>
            NewBinding(IamPrincipalKind.User, user.Id, grant, IamBindingSources.RoleAssignment, null)));
        state.IamVersion = CurrentIamVersion;
        ReprojectLocked(state);
        return state.Users.First(candidate => candidate.Id == user.Id);
    }

    private StudioUser ReplaceDirectGrantsLocked(
        SecurityState state, StudioUser user, IEnumerable<IamGrantRequest> grants, string actingUserId)
    {
        var bindings = state.RoleBindings
            .Where(binding => !(binding.PrincipalKind == IamPrincipalKind.User && binding.PrincipalId == user.Id))
            .Concat(grants.Select(grant => NewBinding(IamPrincipalKind.User, user.Id, grant, IamBindingSources.RoleAssignment, actingUserId)))
            .ToList();
        CommitIamLocked(state, bindings, state.Groups.ToList(), actingUserId);
        return state.Users.First(candidate => candidate.Id == user.Id);
    }

    /// <summary>
    /// Applies a prospective binding and group set after the Owner checks:
    /// an installation that had an active Owner keeps one, and the acting
    /// Owner cannot remove their own Owner role.
    /// </summary>
    private void CommitIamLocked(SecurityState state, List<RoleBinding> bindings, List<StudioGroup> groups, string? actingUserId)
    {
        var before = ActiveOwnerIds(state.Users, state.RoleBindings, state.Groups);
        var after = ActiveOwnerIds(state.Users, bindings, groups);
        if (before.Count > 0 && after.Count == 0)
            throw new SecurityOperationException(409, "last-owner-protection", "At least one active owner is required.");
        if (actingUserId is not null && before.Contains(actingUserId) && !after.Contains(actingUserId))
            throw new SecurityOperationException(409, "last-owner-protection", "An owner cannot remove their own Owner role.");
        state.RoleBindings.Clear();
        state.RoleBindings.AddRange(bindings);
        state.Groups.Clear();
        state.Groups.AddRange(groups);
        state.IamVersion = CurrentIamVersion;
        ReprojectLocked(state);
    }

    /// <summary>
    /// Re-derives the legacy summary of every user from its bindings. A user
    /// without any binding keeps its stored fields: it holds no permission, and
    /// a legacy operator or viewer with an empty project list stays as it was.
    /// </summary>
    private static void ReprojectLocked(SecurityState state)
    {
        for (var index = 0; index < state.Users.Count; index++)
        {
            var user = state.Users[index];
            var effective = EffectiveBindings(user.Id, state.RoleBindings, state.Groups).ToList();
            if (effective.Count == 0) continue;
            var summary = LegacyRoleMapping.Summarize(effective);
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
        var environment = scope.Environment?.Trim().ToLowerInvariant();
        var normalized = scope.Kind switch
        {
            IamScopeKind.Workspace => IamScope.Workspace,
            IamScopeKind.Project when IsScopeName(project) && project != ProjectAccessAuthorization.AllProjectsGrant
                => IamScope.ForProject(project!),
            IamScopeKind.Environment when IsScopeName(project) && project != ProjectAccessAuthorization.AllProjectsGrant
                && IsScopeName(environment) => IamScope.ForEnvironment(project!, environment!),
            _ => throw new SecurityOperationException(400, "invalid-scope",
                "A project scope names one project; an environment scope names a project and an environment."),
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
}
