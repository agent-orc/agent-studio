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
        var summary = store.ListUsers().Single(user => user.Id == member.Id);
        Assert.Equal(StudioRoles.Operator, summary.Role);
        Assert.Equal(["alpha", "beta"], summary.Projects.Order(StringComparer.Ordinal));

        store.UpdateGroup(group.Id, new UpdateGroupRequest(null, null, []), owner);

        Assert.False(store.AuthorizeUser(member.Id, IamPermissions.ProjectCardsWrite, PermissionTarget.ForProject("beta")).Allowed);
        Assert.Equal(StudioRoles.Viewer, store.ListUsers().Single(user => user.Id == member.Id).Role);
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

        Assert.Equal(StudioRoles.Operator, accepted.User.Role);
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
        store.AddBinding(new IamBindingRequest(IamPrincipalKind.User, user.Id, IamRoles.Developer, IamScope.ForProject("beta")), owner);

        var updated = store.UpdateUser(user.Id, new UpdateUserRequest(null, StudioRoles.Contributor, ["delta"], null, ["delta"]), owner);

        var bindings = store.ReadIamSnapshot().Bindings;
        Assert.Equal(
            ["approver:delta", "contributor:delta"],
            bindings.Where(binding => binding.PrincipalId == user.Id).Select(binding => $"{binding.Role}:{binding.Scope.Project}").Order());
        Assert.Contains(bindings, binding => binding.PrincipalId == group.Id);
        Assert.Equal(StudioRoles.Contributor, updated.Role);
        Assert.Equal(["delta", "gamma"], updated.Projects.Order(StringComparer.Ordinal));
        Assert.Equal(["delta"], updated.ProductionApprovalProjects);

        var renamed = store.UpdateUser(user.Id, new UpdateUserRequest("Renamed", null, null, null), owner);
        Assert.Equal("Renamed", renamed.DisplayName);
        Assert.Equal(3, store.ReadIamSnapshot().Bindings.Count(binding => binding.PrincipalId == user.Id || binding.PrincipalId == group.Id));
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
