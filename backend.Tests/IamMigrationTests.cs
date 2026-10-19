using System.Text.Json;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Migration from the current access state file (one role per user with a
/// project list) to role bindings (IP-8): nobody loses access, the legacy
/// summary fields stay, the legacy file is kept as a copy, and a single-user
/// installation needs no setup.
/// </summary>
public sealed class IamMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iam-migration-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

    public static TheoryData<string, string[], string[]> LegacyRows => new()
    {
        { StudioRoles.Owner, [], [] },
        { StudioRoles.Operator, ["*"], [] },
        { StudioRoles.Operator, ["alpha", "beta"], [] },
        { StudioRoles.Viewer, ["*"], [] },
        { StudioRoles.Viewer, ["alpha"], [] },
        { StudioRoles.Contributor, ["alpha", "beta"], ["alpha"] },
        { StudioRoles.Contributor, ["alpha"], [] },
    };

    [Theory]
    [MemberData(nameof(LegacyRows))]
    public void Legacy_role_round_trips_through_bindings(string role, string[] projects, string[] approvals)
    {
        var bindings = LegacyRoleMapping.Grants(role, projects, approvals).Select((grant, index) => new RoleBinding
        {
            Id = "rb_" + index, PrincipalKind = IamPrincipalKind.User, PrincipalId = "usr_1",
            Role = grant.Role, Scope = grant.Scope, Source = IamBindingSources.Migration,
        });

        var summary = LegacyRoleMapping.Summarize(bindings);

        Assert.Equal(role, summary.Role);
        Assert.Equal(role == StudioRoles.Owner ? Array.Empty<string>() : projects, summary.Projects);
        Assert.Equal(approvals, summary.ProductionApprovalProjects);
    }

    [Fact]
    public void Legacy_roles_map_to_the_dossier_roles_per_scope()
    {
        Assert.Equal([new IamGrantRequest(IamRoles.Owner, IamScope.Workspace)],
            LegacyRoleMapping.Grants(StudioRoles.Owner, ["alpha"], null));
        Assert.Equal([new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)],
            LegacyRoleMapping.Grants(StudioRoles.Operator, ["*"], null));
        Assert.Equal([new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha"))],
            LegacyRoleMapping.Grants(StudioRoles.Operator, ["alpha", " ALPHA "], null));
        Assert.Equal([new IamGrantRequest(IamRoles.Viewer, IamScope.Workspace)],
            LegacyRoleMapping.Grants(StudioRoles.Viewer, ["*"], null));
        Assert.Equal(
            [
                new IamGrantRequest(IamRoles.Contributor, IamScope.ForProject("alpha")),
                new IamGrantRequest(IamRoles.Approver, IamScope.ForEnvironment("alpha", "production")),
            ],
            LegacyRoleMapping.Grants(StudioRoles.Contributor, ["alpha"], ["alpha"]));
        Assert.Empty(LegacyRoleMapping.Grants(StudioRoles.Operator, [], null));
        Assert.Empty(LegacyRoleMapping.Grants("retired-role", ["alpha"], null));
    }

    [Fact]
    public void Legacy_state_file_migrates_every_user_without_losing_access()
    {
        var store = StoreWithLegacyState(
            Legacy("usr_owner", "first.owner", StudioRoles.Owner, []),
            Legacy("usr_ops_scoped", "ops.scoped", StudioRoles.Operator, ["alpha"]),
            Legacy("usr_ops_all", "ops.all", StudioRoles.Operator, ["*"]),
            Legacy("usr_viewer", "viewer.alpha", StudioRoles.Viewer, ["alpha"]),
            Legacy("usr_product", "product.user", StudioRoles.Contributor, ["alpha", "beta"], ["alpha"]),
            Legacy("usr_empty", "ops.empty", StudioRoles.Operator, []));
        var original = File.ReadAllText(StatePath);

        var snapshot = store.ReadIamSnapshot();

        Assert.Equal(AccessSecurityStore.CurrentIamVersion, snapshot.IamVersion);
        Assert.All(snapshot.Bindings, binding => Assert.Equal(IamBindingSources.Migration, binding.Source));
        Assert.Equal(["owner@workspace"], Grants(snapshot, "usr_owner"));
        Assert.Equal(["admin@project:alpha"], Grants(snapshot, "usr_ops_scoped"));
        Assert.Equal(["admin@workspace"], Grants(snapshot, "usr_ops_all"));
        Assert.Equal(["viewer@project:alpha"], Grants(snapshot, "usr_viewer"));
        Assert.Equal(["approver@environment:alpha/production", "contributor@project:alpha", "contributor@project:beta"],
            Grants(snapshot, "usr_product"));
        Assert.Empty(Grants(snapshot, "usr_empty"));
        var product = snapshot.Users.Single(user => user.Id == "usr_product");
        Assert.Equal(StudioRoles.Contributor, product.Role);
        Assert.Equal(["alpha", "beta"], product.Projects);
        Assert.Equal(["alpha"], product.ProductionApprovalProjects);

        Assert.True(Allowed(store, "usr_owner", IamPermissions.AccessManage, PermissionTarget.WorkspaceWide));
        Assert.True(Allowed(store, "usr_ops_scoped", IamPermissions.ProjectSettingsWrite, PermissionTarget.ForProject("alpha")));
        Assert.False(Allowed(store, "usr_ops_scoped", IamPermissions.ProjectSettingsWrite, PermissionTarget.ForProject("beta")));
        Assert.True(Allowed(store, "usr_ops_scoped", IamPermissions.WorkspaceSettingsWrite, PermissionTarget.Unaddressed));
        Assert.True(Allowed(store, "usr_ops_all", IamPermissions.ProjectRunsOperate, PermissionTarget.ForProject("beta")));
        Assert.True(Allowed(store, "usr_ops_all", IamPermissions.EnvironmentProductionDeployApprove,
            PermissionTarget.ForProject("beta", "production")));
        Assert.True(Allowed(store, "usr_viewer", IamPermissions.ProjectRead, PermissionTarget.ForProject("alpha")));
        Assert.False(Allowed(store, "usr_viewer", IamPermissions.ProjectCardsWrite, PermissionTarget.ForProject("alpha")));
        Assert.True(Allowed(store, "usr_product", IamPermissions.ProjectCardsWrite, PermissionTarget.ForProject("beta")));
        Assert.False(Allowed(store, "usr_product", IamPermissions.ProjectRunsOperate, PermissionTarget.ForProject("alpha")));
        Assert.True(Allowed(store, "usr_product", IamPermissions.EnvironmentProductionDeployApprove,
            PermissionTarget.ForProject("alpha", "production")));
        Assert.False(Allowed(store, "usr_product", IamPermissions.EnvironmentProductionDeployApprove,
            PermissionTarget.ForProject("beta", "production")));
        Assert.False(Allowed(store, "usr_empty", IamPermissions.ProjectRead, PermissionTarget.Unaddressed));

        Assert.Equal(original, File.ReadAllText(Path.Combine(store.SecurityFolder, "access-state.v0.json")));
        using var persisted = JsonDocument.Parse(File.ReadAllText(StatePath));
        Assert.Equal(AccessSecurityStore.CurrentIamVersion, persisted.RootElement.GetProperty("IamVersion").GetInt32());
        Assert.Equal(snapshot.Bindings.Count, persisted.RootElement.GetProperty("RoleBindings").GetArrayLength());
    }

    [Fact]
    public void Migration_runs_once_and_keeps_the_first_legacy_copy()
    {
        var first = StoreWithLegacyState(Legacy("usr_owner", "first.owner", StudioRoles.Owner, []),
            Legacy("usr_viewer", "viewer.alpha", StudioRoles.Viewer, ["alpha"]));
        var bindings = first.ReadIamSnapshot().Bindings.Select(binding => binding.Id).ToList();
        var backup = File.ReadAllText(Path.Combine(first.SecurityFolder, "access-state.v0.json"));

        var reopened = NewStore();

        Assert.Equal(bindings, reopened.ReadIamSnapshot().Bindings.Select(binding => binding.Id).ToList());
        Assert.Equal(backup, File.ReadAllText(Path.Combine(reopened.SecurityFolder, "access-state.v0.json")));
    }

    [Fact]
    public void Single_user_installation_keeps_working_without_setup()
    {
        var store = StoreWithLegacyState(Legacy("usr_owner", "only.owner", StudioRoles.Owner, []));

        var login = store.Login("only.owner", "legacy owner pass phrase!", "owner|127.0.0.1");

        Assert.Equal(StudioRoles.Owner, login.User.Role);
        Assert.Single(store.ReadIamSnapshot().Bindings);
        Assert.Empty(store.ReadIamSnapshot().Groups);
        foreach (var permission in new[] { IamPermissions.AccessManage, IamPermissions.WorkspaceAdminister, IamPermissions.SecretsWrite })
            Assert.True(Allowed(store, "usr_owner", permission, PermissionTarget.WorkspaceWide), permission);
    }

    [Fact]
    public void Fresh_bootstrap_binds_the_first_owner_and_writes_no_legacy_copy()
    {
        var store = NewStore();
        Assert.True(store.BootstrapRequired);

        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));

        var binding = Assert.Single(store.ReadIamSnapshot().Bindings);
        Assert.Equal((IamPrincipalKind.User, owner.User.Id, IamRoles.Owner, IamScopeKind.Workspace, IamBindingSources.Bootstrap),
            (binding.PrincipalKind, binding.PrincipalId, binding.Role, binding.Scope.Kind, binding.Source));
        Assert.False(File.Exists(Path.Combine(store.SecurityFolder, "access-state.v0.json")));
    }

    private AccessSecurityStore StoreWithLegacyState(params object[] users)
    {
        var store = NewStore();
        Directory.CreateDirectory(store.SecurityFolder);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new
        {
            Users = users,
            Invitations = Array.Empty<object>(),
            Sessions = Array.Empty<object>(),
            Runners = Array.Empty<object>(),
            Enrollments = Array.Empty<object>(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return NewStore();
    }

    private object Legacy(string id, string username, string role, string[] projects, string[]? approvals = null) => new
    {
        Id = id, Username = username, DisplayName = username, Role = role,
        PasswordHash = PasswordSecretHasher.HashPassword("legacy owner pass phrase!"),
        Projects = projects, ProductionApprovalProjects = approvals ?? [],
        Disabled = false, MustChangePassword = false,
        CreatedAt = _clock.GetUtcNow().UtcDateTime, PasswordChangedAt = _clock.GetUtcNow().UtcDateTime,
    };

    private string StatePath => Path.Combine(_root, ".security", "access-state.json");

    private AccessSecurityStore NewStore()
        => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked",
        }).Build(), NullLogger<AccessSecurityStore>.Instance, _clock);

    private static bool Allowed(AccessSecurityStore store, string userId, string permission, PermissionTarget target)
        => store.AuthorizeUser(userId, permission, target).Allowed;

    private static List<string> Grants(IamSnapshot snapshot, string userId)
        => snapshot.Bindings.Where(binding => binding.PrincipalId == userId)
            .Select(binding => binding.Scope.Kind switch
            {
                IamScopeKind.Workspace => $"{binding.Role}@workspace",
                IamScopeKind.Project => $"{binding.Role}@project:{binding.Scope.Project}",
                _ => $"{binding.Role}@environment:{binding.Scope.Project}/{binding.Scope.Environment}",
            })
            .Order(StringComparer.Ordinal).ToList();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
