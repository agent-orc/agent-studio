using AgentStudio.Personas;

namespace AgentStudio.Security;

/// <summary>Persona identities use the operator session and standing-grant policy (PT-D10 B).</summary>
public sealed partial class AccessSecurityStore
{
    public static readonly TimeSpan MaxPersonaOccurrenceLifetime = TimeSpan.FromMinutes(30);

    public ServiceAccount? FindPersonaPrincipal(string workspaceId, string personaId)
    {
        lock (_gate)
            return StateLocked().ServiceAccounts.FirstOrDefault(account => account.Origin == ServiceAccountOrigins.Persona
                && account.PersonaWorkspaceId == workspaceId && account.PersonaId == personaId);
    }

    /// <summary>Admission checks the current grant before reserving a runner slot.</summary>
    public bool HasLivePersonaGrant(string workspaceId, string personaId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var account = state.ServiceAccounts.FirstOrDefault(candidate => candidate.Origin == ServiceAccountOrigins.Persona
                && candidate.PersonaWorkspaceId == workspaceId && candidate.PersonaId == personaId
                && candidate.DisabledAt is null);
            return account is not null && state.StandingGrants.Any(grant => grant.PrincipalId == account.Id
                && OperatorPolicy.IsLive(grant, Now));
        }
    }

    public bool PersonaProjectsCover(string principalId, IReadOnlyList<string> requested,
        Func<string, string, bool> sameProject)
    {
        lock (_gate)
        {
            var projects = StateLocked().ServiceAccounts.FirstOrDefault(account => account.Id == principalId)?.PersonaProjects;
            return projects is not null && (projects.Contains("*") || requested.All(project =>
                projects.Any(allowed => sameProject(allowed, project))));
        }
    }

    /// <summary>Activation's consumed step-up authorizes this one principal and its one T1 grant.</summary>
    public ServiceAccount ActivatePersonaPrincipal(string workspaceId, PersonaActivationV1 activation, string ownerId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var existing = next.ServiceAccounts.FirstOrDefault(account => account.Origin == ServiceAccountOrigins.Persona
                && account.PersonaWorkspaceId == workspaceId && account.PersonaId == activation.PersonaId);
            var now = Now;
            var account = existing is null ? new ServiceAccount
            {
                Id = "opa_persona_" + Guid.NewGuid().ToString("N"), Name = activation.Descriptor.Title,
                Description = activation.Descriptor.Charter, CreatedAt = now, CreatedBy = ownerId,
                Origin = ServiceAccountOrigins.Persona, PersonaWorkspaceId = workspaceId,
                PersonaId = activation.PersonaId, PersonaRoleId = activation.RoleId ?? activation.Descriptor.Id,
                PersonaProjects = [.. activation.Scope.Projects], Sessions = [],
            } : existing with
            {
                DisabledAt = null, Name = activation.Descriptor.Title, Description = activation.Descriptor.Charter,
                CreatedBy = ownerId, PersonaRoleId = activation.RoleId ?? activation.Descriptor.Id,
                PersonaProjects = [.. activation.Scope.Projects],
            };
            if (existing is null) next.ServiceAccounts.Add(account);
            else next.ServiceAccounts[next.ServiceAccounts.IndexOf(existing)] = account;
            var oldGrants = existing is null ? [] : RevokeAgentGrantsLocked(next, account.Id, now, ownerId,
                "persona activation replaced");
            // The existing role catalogue is a route floor. The persona grant remains the operation authority.
            if (!next.RoleBindings.Any(binding => binding.PrincipalId == account.Id && binding.Role == IamRoles.OperatorAgent))
                next.RoleBindings.Add(NewBinding(IamPrincipalKind.ServiceAccount, account.Id,
                    new IamGrantRequest(IamRoles.OperatorAgent, IamScope.Workspace), IamBindingSources.RoleAssignment, ownerId));
            var operations = ValidateGrantOperations(activation.Operations.Where(id => id != OperatorOperationIds.Observe).ToList());
            var grant = NewPersonaGrant(account.Id, operations, activation.Scope.Projects, ownerId, now);
            next.StandingGrants.Add(grant);
            SaveLocked(next);
            foreach (var oldGrant in oldGrants) AuditPersonaGrant(ownerId, "standing-grant.revoke", oldGrant);
            AppendIamAudit(ownerId, existing is null ? "persona-principal.create" : "persona-principal.enable",
                "persona-principal", account.Id, new { workspaceId, activation.PersonaId, account.PersonaRoleId });
            AuditPersonaGrant(ownerId, "standing-grant.create", grant);
            return account;
        }
    }

    /// <summary>The platform alone calls this when it admits an occurrence; no public session-start route exists.</summary>
    public (ServiceAccount Principal, OperatorAgentSession Session, string Secret) MintPersonaOccurrenceSession(
        string workspaceId, string personaId, string occurrenceId, int minutes = 30)
    {
        if (string.IsNullOrWhiteSpace(occurrenceId) || occurrenceId.Length > 128 || minutes is < 1 or > 30)
            throw new SecurityOperationException(400, "invalid-occurrence", "An occurrence id and a lifetime of 1 to 30 minutes are required.");
        RequireNoOperatorKillRequest();
        lock (_gate)
        {
            var next = Copy(StateLocked());
            var account = ActivePersonaLocked(next, workspaceId, personaId);
            var now = Now;
            if (!next.StandingGrants.Any(grant => grant.PrincipalId == account.Id && OperatorPolicy.IsLive(grant, now)))
                throw new SecurityOperationException(403, OperatorPolicyReasons.GrantRequired, "The persona has no live grant.");
            if (!ActiveOwnerIds(next.Users, next.RoleBindings, next.Groups).Contains(account.CreatedBy!))
                throw new SecurityOperationException(403, "owner-required", "The activating Owner is no longer active.");
            var sessionId = Guid.NewGuid().ToString("N");
            var secret = $"{OperatorSessionTokenPrefix}.{sessionId}.{PasswordSecretHasher.RandomToken()}";
            var session = new OperatorAgentSession
            {
                Id = sessionId, Name = "Occurrence " + occurrenceId, OccurrenceId = occurrenceId,
                SecretHash = PasswordSecretHasher.HashSecret(secret), CreatedAt = now,
                CreatedBy = account.CreatedBy!, ExpiresAt = now.AddMinutes(minutes),
            };
            account = account with { Sessions = [.. account.Sessions ?? [], session] };
            next.ServiceAccounts[next.ServiceAccounts.FindIndex(candidate => candidate.Id == account.Id)] = account;
            SaveLocked(next);
            AppendIamAudit(account.CreatedBy, "persona-session.start", PersonaAuditTargetKind,
                $"{workspaceId}/{personaId}", new { sessionId, occurrenceId, session.ExpiresAt });
            return (account, session, secret);
        }
    }

    public void StopPersonaPrincipal(string workspaceId, string personaId, string ownerId, string reason)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var account = next.ServiceAccounts.FirstOrDefault(candidate => candidate.Origin == ServiceAccountOrigins.Persona
                && candidate.PersonaWorkspaceId == workspaceId && candidate.PersonaId == personaId);
            if (account is null) return;
            var now = Now;
            next.ServiceAccounts[next.ServiceAccounts.IndexOf(account)] = DisabledLocked(account, now, ownerId);
            var grants = RevokeAgentGrantsLocked(next, account.Id, now, ownerId, reason);
            SaveLocked(next);
            foreach (var grant in grants) AuditPersonaGrant(ownerId, "standing-grant.revoke", grant);
            AppendIamAudit(ownerId, "persona-principal.stop", "persona-principal", account.Id,
                new { account.Id, reason });
        }
    }

    public IReadOnlyList<string> StopAllPersonaPrincipals(string workspaceId, string ownerId)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, ownerId);
            var targets = next.ServiceAccounts.Where(account => account.Origin == ServiceAccountOrigins.Persona
                && account.PersonaWorkspaceId == workspaceId).ToList();
            var now = Now;
            var grants = new List<StandingGrant>();
            foreach (var account in targets)
            {
                next.ServiceAccounts[next.ServiceAccounts.IndexOf(account)] = DisabledLocked(account, now, ownerId);
                grants.AddRange(RevokeAgentGrantsLocked(next, account.Id, now, ownerId, "persona team stopped"));
            }
            SaveLocked(next);
            foreach (var grant in grants) AuditPersonaGrant(ownerId, "standing-grant.revoke", grant);
            AppendIamAudit(ownerId, "persona.stop-all", PersonaAuditTargetKind, workspaceId,
                new { principalIds = targets.Select(account => account.Id).ToList(), grantIds = grants.Select(grant => grant.Id).ToList() });
            return targets.Select(account => account.Id).ToList();
        }
    }

    public IReadOnlyList<StandingGrant> RenewPersonaTeamGrants(string workspaceId, IReadOnlyCollection<string> activePersonaIds,
        HumanPrincipal owner)
    {
        lock (_gate)
        {
            var next = Copy(StateLocked());
            RequireActiveOwnerLocked(next, owner.User.Id);
            var accounts = activePersonaIds.Distinct(StringComparer.Ordinal)
                .Select(id => ActivePersonaLocked(next, workspaceId, id)).ToList();
            ConsumeStepUpLocked(next, owner);
            var now = Now;
            var renewed = new List<StandingGrant>();
            foreach (var account in accounts)
            {
                var grant = next.StandingGrants.Where(candidate => candidate.PrincipalId == account.Id
                        && candidate.RevokedAt is null)
                    .OrderByDescending(candidate => candidate.IssuedAt).FirstOrDefault();
                if (grant is null) continue;
                if (OperatorPolicy.IsLive(grant, now))
                {
                    var updated = grant with { ExpiresAt = now.AddDays(DefaultStandingGrantDays), RenewedAt = now,
                        RenewedBy = owner.User.Id };
                    next.StandingGrants[next.StandingGrants.IndexOf(grant)] = updated;
                    renewed.Add(updated);
                }
                else
                {
                    var replacement = NewPersonaGrant(account.Id, grant.Operations,
                        grant.Scope.Projects ?? account.PersonaProjects ?? ["*"], owner.User.Id, now);
                    next.StandingGrants.Add(replacement);
                    renewed.Add(replacement);
                }
            }
            SaveLocked(next);
            foreach (var grant in renewed) AuditPersonaGrant(owner.User.Id,
                grant.RenewedAt == now ? "standing-grant.renew" : "standing-grant.create", grant);
            AppendIamAudit(owner.User.Id, "persona.grants.renew-team", PersonaAuditTargetKind, workspaceId,
                new { grantIds = renewed.Select(grant => grant.Id).ToList() });
            return renewed;
        }
    }

    private static ServiceAccount ActivePersonaLocked(SecurityState state, string workspaceId, string personaId)
        => state.ServiceAccounts.FirstOrDefault(account => account.Origin == ServiceAccountOrigins.Persona
                && account.PersonaWorkspaceId == workspaceId && account.PersonaId == personaId && account.DisabledAt is null)
           ?? throw new SecurityOperationException(404, "persona-principal-not-found", "Active persona principal not found.");

    private static StandingGrant NewPersonaGrant(string principalId, IReadOnlyList<string> operations,
        IReadOnlyList<string> projects, string ownerId, DateTime now)
        => new()
        {
            Id = "sgr_" + Guid.NewGuid().ToString("N"), PrincipalId = principalId, Operations = [.. operations],
            Scope = new StandingGrantScope(OperatorScopeKinds.Workspace, Projects: [.. projects]),
            RatePerHour = DefaultStandingGrantRatePerHour,
            IssuedBy = ownerId, IssuedAt = now, ExpiresAt = now.AddDays(DefaultStandingGrantDays),
            Reason = "Persona role activation",
        };

    private void AuditPersonaGrant(string actor, string action, StandingGrant grant)
        => AppendIamAudit(actor, action, "standing-grant", grant.Id,
            new { grant.PrincipalId, grant.Operations, grant.Scope, grant.ExpiresAt, grant.RevokeReason });
}
