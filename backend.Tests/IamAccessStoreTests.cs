using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Rights-model operations of the access store: groups, role bindings,
/// service accounts with their own tokens, invitations with explicit
/// bindings, and the Owner break-glass protection.
/// </summary>
public sealed class IamAccessStoreTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iam-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Group_bindings_grant_their_members_and_re_derive_the_summary()
    {
        var (store, owner) = Bootstrapped();
        var member = store.CreateUser(new CreateUserRequest("team.member", "Member", StudioRoles.Viewer, ["alpha"], Password)).User;

        var group = store.CreateGroup(new CreateGroupRequest("Team Website", "Website crew", [member.Id]), owner);
        store.AddBinding(new IamBindingRequest(IamPrincipalKind.Group, group.Id, IamRoles.Developer, IamScope.ForProject("beta")), owner);

        Assert.True(store.AuthorizeUser(member.Id, IamPermissions.ProjectCardsWrite, PermissionTarget.ForProject("beta")).Allowed);
        Assert.False(store.AuthorizeUser(member.Id, IamPermissions.ProjectCardsWrite, PermissionTarget.ForProject("alpha")).Allowed);
        // The persisted summary is downgrade-safe: Developer never becomes operator.
        var summary = store.ListUsers().Single(user => user.Id == member.Id);
        Assert.Equal(StudioRoles.Viewer, summary.Role);
        Assert.Equal(["alpha", "beta"], summary.Projects.Order(StringComparer.Ordinal));

        store.UpdateGroup(group.Id, new UpdateGroupRequest(null, null, []), owner);

        Assert.False(store.AuthorizeUser(member.Id, IamPermissions.ProjectCardsWrite, PermissionTarget.ForProject("beta")).Allowed);
        Assert.Equal(["alpha"], store.ListUsers().Single(user => user.Id == member.Id).Projects);
    }

    [Fact]
    public void Deleting_a_group_removes_its_bindings()
    {
        var (store, owner) = Bootstrapped();
        var group = store.CreateGroup(new CreateGroupRequest("Reviewers", null, null), owner);
        store.AddBinding(new IamBindingRequest(IamPrincipalKind.Group, group.Id, IamRoles.Viewer, IamScope.Workspace), owner);

        store.DeleteGroup(group.Id, owner);

        Assert.DoesNotContain(store.ReadIamSnapshot().Bindings, binding => binding.PrincipalId == group.Id);
        Assert.Throws<SecurityOperationException>(() => store.DeleteGroup(group.Id, owner));
    }

    [Fact]
    public void The_installation_keeps_an_active_owner_and_owners_cannot_demote_themselves()
    {
        var (store, owner) = Bootstrapped();
        var ownerBinding = store.ReadIamSnapshot().Bindings.Single();

        var self = Assert.Throws<SecurityOperationException>(() => store.RemoveBinding(ownerBinding.Id, owner));
        Assert.Equal("last-owner-protection", self.Code);

        var second = store.CreateUser(new CreateUserRequest("second.owner", "Second", StudioRoles.Owner, null, Password)).User;
        var demoteSelf = Assert.Throws<SecurityOperationException>(() => store.RemoveBinding(ownerBinding.Id, owner));
        Assert.Equal("last-owner-protection", demoteSelf.Code);

        store.RemoveBinding(ownerBinding.Id, second.Id);
        Assert.False(store.AuthorizeUser(owner, IamPermissions.AccessManage, PermissionTarget.WorkspaceWide).Allowed);
        var last = store.ReadIamSnapshot().Bindings.Single(binding => binding.Role == IamRoles.Owner);
        Assert.Throws<SecurityOperationException>(() => store.RemoveBinding(last.Id, owner));
    }

    [Fact]
    public void Owner_through_a_group_counts_and_removing_the_last_member_is_refused()
    {
        var (store, owner) = Bootstrapped();
        var group = store.CreateGroup(new CreateGroupRequest("Owners", null, [owner]), owner);
        store.AddBinding(new IamBindingRequest(IamPrincipalKind.Group, group.Id, IamRoles.Owner, IamScope.Workspace), owner);
        var direct = store.ReadIamSnapshot().Bindings.Single(binding => binding.PrincipalKind == IamPrincipalKind.User);

        store.RemoveBinding(direct.Id, owner);

        Assert.True(store.AuthorizeUser(owner, IamPermissions.AccessManage, PermissionTarget.WorkspaceWide).Allowed);
        var refused = Assert.Throws<SecurityOperationException>(() =>
            store.UpdateGroup(group.Id, new UpdateGroupRequest(null, null, []), owner));
        Assert.Equal("last-owner-protection", refused.Code);
    }

    [Theory]
    [InlineData(IamPrincipalKind.User, IamRoles.Owner, IamScopeKind.Project)]
    [InlineData(IamPrincipalKind.User, IamRoles.Contributor, IamScopeKind.Workspace)]
    [InlineData(IamPrincipalKind.User, IamRoles.Admin, IamScopeKind.Environment)]
    [InlineData(IamPrincipalKind.User, "maintainer", IamScopeKind.Project)]
    [InlineData(IamPrincipalKind.ServiceAccount, IamRoles.Owner, IamScopeKind.Workspace)]
    [InlineData(IamPrincipalKind.ServiceAccount, IamRoles.Contributor, IamScopeKind.Project)]
    [InlineData(IamPrincipalKind.ServiceAccount, IamRoles.Approver, IamScopeKind.Environment)]
    public void Invalid_role_scope_combinations_are_rejected(IamPrincipalKind kind, string role, IamScopeKind scopeKind)
    {
        var scope = new IamScope(scopeKind, scopeKind == IamScopeKind.Workspace ? null : "alpha",
            scopeKind == IamScopeKind.Environment ? "production" : null);

        var error = Assert.Throws<SecurityOperationException>(() => AccessSecurityStore.ValidateGrant(kind, new IamGrantRequest(role, scope)));

        Assert.Equal(400, error.Status);
    }

    [Fact]
    public void Scopes_are_normalized_and_must_name_their_project_and_environment()
    {
        var grant = AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(" Approver ", new IamScope(IamScopeKind.Environment, " alpha ", " Production ")));
        Assert.Equal(new IamGrantRequest(IamRoles.Approver, IamScope.ForEnvironment("alpha", "production")), grant);
        Assert.Equal(IamScope.Workspace, AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(IamRoles.Viewer, new IamScope(IamScopeKind.Workspace, "ignored"))).Scope);

        Assert.Throws<SecurityOperationException>(() => AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(IamRoles.Viewer, new IamScope(IamScopeKind.Project, " "))));
        Assert.Throws<SecurityOperationException>(() => AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(IamRoles.Viewer, new IamScope(IamScopeKind.Project, "*"))));
        Assert.Throws<SecurityOperationException>(() => AccessSecurityStore.ValidateGrant(IamPrincipalKind.User,
            new IamGrantRequest(IamRoles.Viewer, new IamScope(IamScopeKind.Environment, "alpha"))));
    }

    [Fact]
    public void Duplicate_bindings_and_unknown_principals_are_refused()
    {
        var (store, owner) = Bootstrapped();
        var viewer = store.CreateUser(new CreateUserRequest("plain.viewer", "Viewer", StudioRoles.Viewer, ["alpha"], Password)).User;

        var duplicate = Assert.Throws<SecurityOperationException>(() => store.AddBinding(
            new IamBindingRequest(IamPrincipalKind.User, viewer.Id, IamRoles.Viewer, IamScope.ForProject("ALPHA")), owner));
        var missing = Assert.Throws<SecurityOperationException>(() => store.AddBinding(
            new IamBindingRequest(IamPrincipalKind.Group, "grp_missing", IamRoles.Viewer, IamScope.Workspace), owner));

        Assert.Equal(409, duplicate.Status);
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public void Service_accounts_hold_hashed_tokens_with_expiry_and_revocation()
    {
        var (store, owner) = Bootstrapped();
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("deploy-bot", "Stage deploys",
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))]), owner);
        var created = store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest("ci", null));

        Assert.DoesNotContain(created.Secret, File.ReadAllText(Path.Combine(store.SecurityFolder, "access-state.json")));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(90), created.Token.ExpiresAt);
        var principal = store.AuthenticateServiceAccount(created.Secret);
        Assert.Equal((account.Id, created.Token.Id), (principal?.AccountId, principal?.TokenId));
        Assert.Null(store.AuthenticateServiceAccount(created.Secret[..^2] + "zz"));
        Assert.True(store.AuthorizeServiceAccount(account.Id, IamPermissions.ProjectCardsWrite, PermissionTarget.ForProject("alpha")).Allowed);
        Assert.False(store.AuthorizeServiceAccount(account.Id, IamPermissions.ProjectSettingsWrite, PermissionTarget.ForProject("alpha")).Allowed);

        store.RevokeServiceAccountToken(account.Id, created.Token.Id);
        Assert.Null(store.AuthenticateServiceAccount(created.Secret));

        var shortLived = store.CreateServiceAccountToken(account.Id,
            new CreateServiceAccountTokenRequest(null, _clock.GetUtcNow().UtcDateTime.AddHours(1)));
        Assert.NotNull(store.AuthenticateServiceAccount(shortLived.Secret));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(store.AuthenticateServiceAccount(shortLived.Secret));

        var kept = store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null));
        store.DisableServiceAccount(account.Id);
        Assert.Null(store.AuthenticateServiceAccount(kept.Secret));
        Assert.Throws<SecurityOperationException>(() =>
            store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null)));
    }

    [Fact]
    public void Service_account_tokens_must_expire_within_a_year()
    {
        var (store, owner) = Bootstrapped();
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("ci-bot", null), owner);
        var now = _clock.GetUtcNow().UtcDateTime;

        Assert.Throws<SecurityOperationException>(() =>
            store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, now.AddDays(400))));
        Assert.Throws<SecurityOperationException>(() =>
            store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, now.AddMinutes(-1))));
    }

    [Fact]
    public void Invitation_with_explicit_bindings_uses_the_existing_invitation_flow()
    {
        var (store, _) = Bootstrapped();

        var invite = store.InviteUser(new InviteUserRequest("new.developer", "New Developer", "", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")),
             new IamGrantRequest(IamRoles.Approver, IamScope.ForEnvironment("alpha", "production"))]));
        var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "developer pass phrase!"));

        Assert.Equal(StudioRoles.Viewer, accepted.User.Role);
        Assert.Equal(["alpha"], accepted.User.Projects);
        Assert.True(store.AuthorizeUser(accepted.User.Id, IamPermissions.ProjectRunsOperate, PermissionTarget.ForProject("alpha")).Allowed);
        Assert.False(store.AuthorizeUser(accepted.User.Id, IamPermissions.ProjectSettingsWrite, PermissionTarget.ForProject("alpha")).Allowed);
        Assert.True(store.AuthorizeUser(accepted.User.Id, IamPermissions.EnvironmentProductionDeployApprove,
            PermissionTarget.ForProject("alpha", "production")).Allowed);
        Assert.Contains(store.ReadIamSnapshot().Invitations, invitation => invitation.UserId == accepted.User.Id && invitation.UsedAt is not null);
    }

    [Fact]
    public void Legacy_role_update_replaces_direct_bindings_and_keeps_group_bindings()
    {
        var (store, owner) = Bootstrapped();
        var user = store.CreateUser(new CreateUserRequest("legacy.user", "Legacy", StudioRoles.Operator, ["alpha"], Password)).User;
        var group = store.CreateGroup(new CreateGroupRequest("Readers", null, [user.Id]), owner);
        store.AddBinding(new IamBindingRequest(IamPrincipalKind.Group, group.Id, IamRoles.Viewer, IamScope.ForProject("gamma")), owner);

        var updated = store.UpdateUser(user.Id, new UpdateUserRequest(null, StudioRoles.Contributor, ["delta"], null, ["delta"]), owner);

        var bindings = store.ReadIamSnapshot().Bindings;
        Assert.Equal(
            ["approver:delta", "contributor:delta"],
            bindings.Where(binding => binding.PrincipalId == user.Id).Select(binding => $"{binding.Role}:{binding.Scope.Project}").Order());
        Assert.Contains(bindings, binding => binding.PrincipalId == group.Id);
        Assert.Equal(StudioRoles.Contributor, updated.Role);
        Assert.Equal(["delta"], updated.Projects);
        Assert.Equal(["delta"], updated.ProductionApprovalProjects);

        var renamed = store.UpdateUser(user.Id, new UpdateUserRequest("Renamed", null, null, null), owner);
        Assert.Equal("Renamed", renamed.DisplayName);
        Assert.Equal(3, store.ReadIamSnapshot().Bindings.Count(binding => binding.PrincipalId == user.Id || binding.PrincipalId == group.Id));
    }

    public static TheoryData<string, IamScope> BindingManagedUsers => new()
    {
        { IamRoles.Developer, IamScope.ForProject("alpha") },
        { IamRoles.Approver, IamScope.ForEnvironment("alpha", "production") },
        { IamRoles.Approver, IamScope.Workspace },
    };

    [Theory]
    [MemberData(nameof(BindingManagedUsers))]
    public void Rename_and_disable_with_the_current_summary_keep_binding_managed_access(string role, IamScope scope)
    {
        var (store, owner) = Bootstrapped();
        var invite = store.InviteUser(new InviteUserRequest("managed.user", "Managed", "", null, [new IamGrantRequest(role, scope)]), owner);
        var before = store.ReadIamSnapshot().Bindings.Where(binding => binding.PrincipalId == invite.User.Id).ToList();
        var listed = store.ListUsers().Single(user => user.Id == invite.User.Id);

        // The develop Access UI always sends role and projects with a rename or a disable.
        store.UpdateUser(invite.User.Id, new UpdateUserRequest("Renamed", listed.Role, listed.Projects, null), owner);
        store.UpdateUser(invite.User.Id, new UpdateUserRequest(null, listed.Role, listed.Projects, true), owner);

        var after = store.ReadIamSnapshot().Bindings.Where(binding => binding.PrincipalId == invite.User.Id).ToList();
        Assert.Equal(before, after);
        var user = store.ListUsers().Single(candidate => candidate.Id == invite.User.Id);
        Assert.Equal(("Renamed", true), (user.DisplayName, user.Disabled));

        var changed = Assert.Throws<SecurityOperationException>(() =>
            store.UpdateUser(invite.User.Id, new UpdateUserRequest(null, StudioRoles.Operator, ["alpha"], null), owner));
        Assert.Equal((409, "bindings-managed"), (changed.Status, changed.Code));
        Assert.Equal(before, store.ReadIamSnapshot().Bindings.Where(binding => binding.PrincipalId == invite.User.Id).ToList());
    }

    [Fact]
    public void A_refused_update_leaves_the_state_and_file_untouched()
    {
        var (store, owner) = Bootstrapped();
        var file = Path.Combine(store.SecurityFolder, "access-state.json");
        var before = File.ReadAllText(file);

        var demote = Assert.Throws<SecurityOperationException>(() =>
            store.UpdateUser(owner, new UpdateUserRequest("Renamed Owner", StudioRoles.Operator, ["*"], null), owner));
        Assert.Equal("last-owner-protection", demote.Code);
        var disable = Assert.Throws<SecurityOperationException>(() =>
            store.UpdateUser(owner, new UpdateUserRequest("Renamed Owner", null, null, true), owner));
        Assert.Equal("last-owner-protection", disable.Code);

        var second = store.CreateUser(new CreateUserRequest("second.admin", "Second", StudioRoles.Operator, ["*"], Password), owner).User;
        var unchanged = File.ReadAllText(file);
        var last = Assert.Throws<SecurityOperationException>(() =>
            store.UpdateUser(owner, new UpdateUserRequest("Renamed Owner", StudioRoles.Viewer, ["*"], false), second.Id));
        Assert.Equal("last-owner-protection", last.Code);

        Assert.Equal(unchanged, File.ReadAllText(file));
        Assert.NotEqual(before, unchanged);
        var current = store.ListUsers().Single(user => user.Id == owner);
        Assert.Equal(("first.owner", StudioRoles.Owner, false), (current.DisplayName, current.Role, current.Disabled));
        Assert.Contains(store.ReadIamSnapshot().Bindings, binding => binding.PrincipalId == owner && binding.Role == IamRoles.Owner);
    }

    [Fact]
    public void A_user_without_effective_bindings_is_summarized_as_a_viewer_of_no_project()
    {
        var (store, owner) = Bootstrapped();
        var admin = store.CreateUser(new CreateUserRequest("all.admin", "All", StudioRoles.Operator, ["*"], Password), owner).User;
        Assert.Equal(StudioRoles.Operator, admin.Role);
        Assert.Equal(["*"], admin.Projects);

        store.RemoveBinding(store.ReadIamSnapshot().Bindings.Single(binding => binding.PrincipalId == admin.Id).Id, owner);

        var summary = store.ListUsers().Single(user => user.Id == admin.Id);
        Assert.Equal(StudioRoles.Viewer, summary.Role);
        Assert.Empty(summary.Projects);
        Assert.Empty(summary.ProductionApprovalProjects);
    }

    [Fact]
    public void Iam_changes_are_audited_append_only_with_the_acting_user_and_the_injected_clock()
    {
        var (store, owner) = Bootstrapped();
        var created = store.CreateUser(new CreateUserRequest("audited.user", "Audited", StudioRoles.Viewer, ["alpha"], Password), owner).User;
        var group = store.CreateGroup(new CreateGroupRequest("Audited", null, [created.Id]), owner);
        store.UpdateGroup(group.Id, new UpdateGroupRequest(null, null, []), owner);
        var binding = store.AddBinding(new IamBindingRequest(IamPrincipalKind.Group, group.Id, IamRoles.Viewer, IamScope.Workspace), owner);
        store.RemoveBinding(binding.Id, owner);
        store.DeleteGroup(group.Id, owner);
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("audited-bot", null, null), owner);
        var token = store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(null, null), owner);
        store.RevokeServiceAccountToken(account.Id, token.Token.Id, owner);
        store.DisableServiceAccount(account.Id, owner);
        var apiToken = store.CreateApiToken(new CreateApiTokenRequest("audited api", StudioRoles.Viewer, ["alpha"],
            _clock.GetUtcNow().UtcDateTime.AddDays(1)), owner);
        store.RevokeApiToken(apiToken.Token.Id, owner);
        _clock.Advance(TimeSpan.FromMinutes(5));
        store.UpdateUser(created.Id, new UpdateUserRequest(null, StudioRoles.Operator, ["alpha"], null), owner);

        var file = Path.Combine(store.SecurityFolder, AccessSecurityStore.IamAuditFileName);
        var rows = File.ReadAllLines(file).Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement).ToList();
        var actions = rows.Select(row => row.GetProperty("action").GetString()).ToList();
        foreach (var expected in new[]
        {
            "binding.add", "group.create", "group.update", "binding.remove", "group.delete", "service-account.create",
            "token.issue", "token.revoke", "service-account.disable", "user.legacy-role",
        })
            Assert.Contains(expected, actions);
        Assert.All(rows.Skip(1), row => Assert.Equal(owner, row.GetProperty("actor").GetString()));
        var update = rows.Single(row => row.GetProperty("action").GetString() == "group.update");
        Assert.Equal(created.Id, Assert.Single(update.GetProperty("details").GetProperty("membersRemoved").EnumerateArray()).GetString());
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, rows.Last().GetProperty("timestamp").GetDateTime());
        Assert.Equal(owner, store.ReadIamSnapshot().Bindings.Single(binding => binding.PrincipalId == created.Id).CreatedBy);
    }

    [Fact]
    public void Created_and_invited_users_bindings_name_their_creator()
    {
        var (store, owner) = Bootstrapped();

        var created = store.CreateUser(new CreateUserRequest("created.user", "Created", StudioRoles.Viewer, ["alpha"], Password), owner).User;
        var invited = store.InviteUser(new InviteUserRequest("invited.user", "Invited", "", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))]), owner).User;

        var bindings = store.ReadIamSnapshot().Bindings;
        Assert.Equal(owner, bindings.Single(binding => binding.PrincipalId == created.Id).CreatedBy);
        Assert.Equal(owner, bindings.Single(binding => binding.PrincipalId == invited.Id).CreatedBy);
    }

    [Fact]
    public void Disabled_service_accounts_are_reported_as_denied()
    {
        var (store, owner) = Bootstrapped();
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("checked-bot", null,
            [new IamGrantRequest(IamRoles.Viewer, IamScope.Workspace)]), owner);
        Assert.True(store.CheckPermission(IamPrincipalKind.ServiceAccount, account.Id, IamPermissions.ProjectRead,
            PermissionTarget.ForProject("alpha")).Allowed);

        store.DisableServiceAccount(account.Id, owner);

        var decision = store.CheckPermission(IamPrincipalKind.ServiceAccount, account.Id, IamPermissions.ProjectRead,
            PermissionTarget.ForProject("alpha"));
        Assert.False(decision.Allowed);
        Assert.Contains("disabled", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Names_descriptions_and_token_names_are_length_limited()
    {
        var (store, owner) = Bootstrapped();

        Assert.Equal("invalid-name", Assert.Throws<SecurityOperationException>(() =>
            store.CreateGroup(new CreateGroupRequest(new string('g', 65), null, null), owner)).Code);
        Assert.Equal("invalid-description", Assert.Throws<SecurityOperationException>(() =>
            store.CreateGroup(new CreateGroupRequest("Group", new string('d', 501), null), owner)).Code);
        Assert.Equal("invalid-name", Assert.Throws<SecurityOperationException>(() =>
            store.CreateServiceAccount(new CreateServiceAccountRequest(new string('s', 65), null, null), owner)).Code);
        Assert.Equal("invalid-description", Assert.Throws<SecurityOperationException>(() =>
            store.CreateServiceAccount(new CreateServiceAccountRequest("bot", new string('d', 501), null), owner)).Code);
        var account = store.CreateServiceAccount(new CreateServiceAccountRequest("bot", null, null), owner);
        Assert.Equal("invalid-name", Assert.Throws<SecurityOperationException>(() =>
            store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest(new string('t', 101), null), owner)).Code);
        var group = store.CreateGroup(new CreateGroupRequest("Group", null, null), owner);
        Assert.Equal("invalid-description", Assert.Throws<SecurityOperationException>(() =>
            store.UpdateGroup(group.Id, new UpdateGroupRequest(null, new string('d', 501), null), owner)).Code);
    }

    private (AccessSecurityStore Store, string OwnerId) Bootstrapped()
    {
        var store = new AccessSecurityStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build(), NullLogger<AccessSecurityStore>.Instance, _clock);
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        return (store, owner.User.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
