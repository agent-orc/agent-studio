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
        // Tightening (review item 7): workspace settings and operation need a
        // workspace binding; a scoped legacy operator keeps its projects only.
        Assert.False(Allowed(store, "usr_ops_scoped", IamPermissions.WorkspaceSettingsWrite, PermissionTarget.Unaddressed));
        Assert.False(Allowed(store, "usr_ops_scoped", IamPermissions.WorkspaceOperate, PermissionTarget.WorkspaceWide));
        Assert.True(Allowed(store, "usr_ops_all", IamPermissions.WorkspaceSettingsWrite, PermissionTarget.WorkspaceWide));
        Assert.True(Allowed(store, "usr_ops_all", IamPermissions.WorkspaceOperate, PermissionTarget.WorkspaceWide));
        Assert.False(Allowed(store, "usr_ops_all", IamPermissions.AccessRead, PermissionTarget.WorkspaceWide));
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

        Assert.Equal(original, File.ReadAllText(Assert.Single(Backups(store))));
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
        var backup = Assert.Single(Backups(first));
        var content = File.ReadAllText(backup);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var reopened = NewStore();

        Assert.Equal(bindings, reopened.ReadIamSnapshot().Bindings.Select(binding => binding.Id).ToList());
        Assert.Equal(backup, Assert.Single(Backups(reopened)));
        Assert.Equal(content, File.ReadAllText(backup));
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
        Assert.Empty(Backups(store));
    }

    [Fact]
    public void Sessions_runners_enrollments_invitations_and_api_tokens_survive_the_migration()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var sessionToken = $"ssn.{Guid.NewGuid():N}.{PasswordSecretHasher.RandomToken()}";
        var runnerSecret = $"rnr.{Guid.NewGuid():N}.{PasswordSecretHasher.RandomToken()}";
        var engineSecret = $"rnr.{Guid.NewGuid():N}.{PasswordSecretHasher.RandomToken()}";
        var apiSecret = $"api.{Guid.NewGuid():N}.{PasswordSecretHasher.RandomToken()}";
        var viewerSecret = $"api.{Guid.NewGuid():N}.{PasswordSecretHasher.RandomToken()}";
        var sessions = new[]
        {
            new StudioSession
            {
                Id = sessionToken.Split('.')[1], UserId = "usr_owner", TokenHash = PasswordSecretHasher.HashSecret(sessionToken),
                CsrfHash = PasswordSecretHasher.HashSecret("csrf"), CreatedAt = now, LastSeenAt = now,
                ExpiresAt = now.AddHours(8), AbsoluteExpiresAt = now.AddDays(7),
            },
        };
        var runners = new[]
        {
            Runner("runner_" + Guid.NewGuid().ToString("N"), "coding-host", runnerSecret, RunnerScopes.Minimum, now),
            Runner("engine_" + Guid.NewGuid().ToString("N"), "engine", engineSecret, [RunnerScopes.Orchestration], now),
        };
        var enrollments = new[]
        {
            new RunnerEnrollment
            {
                Id = Guid.NewGuid().ToString("N"), Name = "pending", CodeHash = PasswordSecretHasher.HashSecret("enrollment"),
                Scopes = [.. RunnerScopes.Minimum], CreatedAt = now, ExpiresAt = now.AddMinutes(15),
            },
        };
        var invitations = new[]
        {
            new StudioInvitation { Id = Guid.NewGuid().ToString("N"), UserId = "usr_viewer", TokenHash = PasswordSecretHasher.HashSecret("invite"), ExpiresAt = now.AddHours(24) },
        };
        var apiTokens = new[]
        {
            ApiToken(apiSecret, "deploy bot", StudioRoles.Operator, ["alpha"], now),
            ApiToken(viewerSecret, "reader", StudioRoles.Viewer, ["*"], now),
        };
        var store = StoreWithState(new
        {
            Users = new object[]
            {
                Legacy("usr_owner", "first.owner", StudioRoles.Owner, []),
                Legacy("usr_viewer", "viewer.alpha", StudioRoles.Viewer, ["alpha"]),
            },
            Invitations = invitations, Sessions = sessions, Runners = runners, Enrollments = enrollments, ApiTokens = apiTokens,
        });

        var snapshot = store.ReadIamSnapshot();

        using var persisted = JsonDocument.Parse(File.ReadAllText(StatePath));
        Assert.Equal(Canonical(sessions), Canonical<StudioSession>(persisted.RootElement.GetProperty("Sessions")));
        Assert.Equal(Canonical(runners), Canonical<RunnerServiceIdentity>(persisted.RootElement.GetProperty("Runners")));
        Assert.Equal(Canonical(enrollments), Canonical<RunnerEnrollment>(persisted.RootElement.GetProperty("Enrollments")));
        Assert.Equal(Canonical(invitations), Canonical<StudioInvitation>(persisted.RootElement.GetProperty("Invitations")));
        Assert.Equal(0, persisted.RootElement.GetProperty("ApiTokens").GetArrayLength());
        Assert.NotNull(store.AuthenticateSession(sessionToken, touch: false));
        Assert.Equal(runners[0].Id, store.AuthenticateRunner(runnerSecret)?.RunnerId);
        var engine = store.AuthenticateRunner(engineSecret);
        Assert.Equal(runners[1].Id, engine?.RunnerId);
        Assert.Equal([RunnerScopes.Orchestration], engine!.Scopes);

        var deploy = store.AuthenticateServiceAccount(apiSecret);
        Assert.Equal(("sa_" + apiTokens[0].Id, apiTokens[0].Id, ServiceAccountOrigins.ApiToken),
            (deploy?.AccountId, deploy?.TokenId, deploy?.Origin));
        Assert.Equal("service-account:sa_" + apiTokens[0].Id, deploy!.ActorId);
        var account = snapshot.ServiceAccounts.Single(candidate => candidate.Id == deploy.AccountId);
        Assert.Equal(apiTokens[0].SecretHash, Assert.Single(account.Tokens).SecretHash);
        Assert.Equal(["admin@project:alpha"], Grants(snapshot, deploy.AccountId));
        Assert.Equal(["viewer@workspace"], Grants(snapshot, "sa_" + apiTokens[1].Id));
        Assert.NotNull(store.AuthenticateServiceAccount(viewerSecret));
        Assert.Equal(["deploy bot", "reader"], store.ListApiTokens().Select(token => token.Name));
        Assert.Equal(StudioRoles.Operator, store.ListApiTokens()[0].Role);
        Assert.Equal(["alpha"], store.ListApiTokens()[0].Projects);
        Assert.False(store.AuthorizeServiceAccount(deploy.AccountId, IamPermissions.EnvironmentProductionDeployApprove,
            PermissionTarget.ForProject("alpha", "production")).Allowed);
    }

    [Fact]
    public void An_existing_backup_is_kept_and_this_migration_writes_its_own()
    {
        var store = NewStore();
        Directory.CreateDirectory(store.SecurityFolder);
        var oldBackup = Path.Combine(store.SecurityFolder, "access-state.v0.json");
        File.WriteAllText(oldBackup, "{\"Users\":[]}");
        var oldStamped = Path.Combine(store.SecurityFolder, "access-state.v0.20260101T000000000Z.json");
        File.WriteAllText(oldStamped, "{\"Users\":[]}");
        store = StoreWithLegacyState(Legacy("usr_owner", "first.owner", StudioRoles.Owner, []));
        var original = File.ReadAllText(StatePath);

        store.ReadIamSnapshot();

        Assert.Equal("{\"Users\":[]}", File.ReadAllText(oldBackup));
        Assert.Equal("{\"Users\":[]}", File.ReadAllText(oldStamped));
        var written = Assert.Single(Backups(store), path => path != oldBackup && path != oldStamped);
        Assert.Contains("20261020T090000000Z", Path.GetFileName(written), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(written));
    }

    [Fact]
    public void A_crash_between_the_copy_and_the_save_leaves_the_file_and_the_store_unmigrated()
    {
        StoreWithLegacyState(Legacy("usr_owner", "first.owner", StudioRoles.Owner, []),
            Legacy("usr_ops", "ops.alpha", StudioRoles.Operator, ["alpha"]));
        var original = File.ReadAllText(StatePath);
        // A directory where the save writes its temp file makes the save fail
        // after the migrated copy and the backup exist.
        Directory.CreateDirectory(StatePath + ".tmp");
        var store = NewStore();

        Assert.ThrowsAny<SystemException>(() => store.ReadIamSnapshot());
        Assert.ThrowsAny<SystemException>(() => store.ReadIamSnapshot());

        Assert.Equal(original, File.ReadAllText(StatePath));
        Directory.Delete(StatePath + ".tmp");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var snapshot = store.ReadIamSnapshot();
        Assert.Equal(["admin@project:alpha"], Grants(snapshot, "usr_ops"));
        Assert.Equal(["owner@workspace"], Grants(snapshot, "usr_owner"));
        Assert.All(Backups(store), backup => Assert.Equal(original, File.ReadAllText(backup)));
        using var persisted = JsonDocument.Parse(File.ReadAllText(StatePath));
        Assert.Equal(AccessSecurityStore.CurrentIamVersion, persisted.RootElement.GetProperty("IamVersion").GetInt32());
    }

    [Fact]
    public void The_persisted_summary_is_downgrade_safe()
    {
        var store = StoreWithLegacyState(Legacy("usr_owner", "first.owner", StudioRoles.Owner, []));
        var owner = "usr_owner";
        var developer = store.InviteUser(new InviteUserRequest("dev.user", "Dev", "", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.Workspace)]), owner).User;
        var approver = store.InviteUser(new InviteUserRequest("approver.user", "Approver", "", null,
            [new IamGrantRequest(IamRoles.Approver, IamScope.ForEnvironment("alpha", "production"))]), owner).User;
        var mixed = store.InviteUser(new InviteUserRequest("mixed.user", "Mixed", "", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.ForProject("alpha")), new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("beta"))]), owner).User;

        using var persisted = JsonDocument.Parse(File.ReadAllText(StatePath));
        var users = persisted.RootElement.GetProperty("Users").EnumerateArray()
            .ToDictionary(user => user.GetProperty("Id").GetString()!, user => user);
        Assert.Equal(StudioRoles.Viewer, users[developer.Id].GetProperty("Role").GetString());
        Assert.Equal(["*"], users[developer.Id].GetProperty("Projects").EnumerateArray().Select(project => project.GetString()));
        Assert.Equal(StudioRoles.Viewer, users[approver.Id].GetProperty("Role").GetString());
        Assert.Equal(StudioRoles.Operator, users[mixed.Id].GetProperty("Role").GetString());
        Assert.Equal(["alpha"], users[mixed.Id].GetProperty("Projects").EnumerateArray().Select(project => project.GetString()));
    }

    [Fact]
    public void A_version_one_state_moves_to_version_two_and_re_derives_the_summary()
    {
        var store = NewStore();
        Directory.CreateDirectory(store.SecurityFolder);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(new
        {
            IamVersion = 1,
            Users = new[]
            {
                Legacy("usr_owner", "first.owner", StudioRoles.Owner, []),
                Legacy("usr_dev", "dev.user", StudioRoles.Operator, ["alpha"]),
            },
            RoleBindings = new[]
            {
                new RoleBinding { Id = "rb_owner", PrincipalKind = IamPrincipalKind.User, PrincipalId = "usr_owner", Role = IamRoles.Owner, Scope = IamScope.Workspace, Source = IamBindingSources.Bootstrap },
                new RoleBinding { Id = "rb_dev", PrincipalKind = IamPrincipalKind.User, PrincipalId = "usr_dev", Role = IamRoles.Developer, Scope = IamScope.ForProject("alpha"), Source = IamBindingSources.Manual },
            },
        }));

        var snapshot = NewStore().ReadIamSnapshot();

        Assert.Equal(AccessSecurityStore.CurrentIamVersion, snapshot.IamVersion);
        Assert.Equal(["rb_dev", "rb_owner"], snapshot.Bindings.Select(binding => binding.Id).Order(StringComparer.Ordinal));
        Assert.Equal(StudioRoles.Viewer, snapshot.Users.Single(user => user.Id == "usr_dev").Role);
        Assert.Contains("access-state.v1.", Path.GetFileName(Assert.Single(Backups(store))), StringComparison.Ordinal);
    }

    private static RunnerServiceIdentity Runner(string id, string name, string secret, IEnumerable<string> scopes, DateTime now) => new()
    {
        Id = id, Name = name, CreatedAt = now,
        Credentials =
        [
            new RunnerCredential
            {
                Id = secret.Split('.')[1], SecretHash = PasswordSecretHasher.HashSecret(secret), Scopes = [.. scopes], CreatedAt = now,
            },
        ],
    };

    private static StudioApiToken ApiToken(string secret, string name, string role, List<string> projects, DateTime now) => new()
    {
        Id = secret.Split('.')[1], Name = name, Role = role, Projects = projects, SecretHash = PasswordSecretHasher.HashSecret(secret),
        CreatedBy = "usr_owner", CreatedAt = now, ExpiresAt = now.AddDays(30),
    };

    private static string Canonical<T>(IEnumerable<T> items) => JsonSerializer.Serialize(items.ToList(), CanonicalJson);

    private static string Canonical<T>(JsonElement element)
        => Canonical(JsonSerializer.Deserialize<List<T>>(element.GetRawText(), CanonicalJson)!);

    private static readonly JsonSerializerOptions CanonicalJson = new() { PropertyNameCaseInsensitive = true };

    private static IReadOnlyList<string> Backups(AccessSecurityStore store)
        => Directory.GetFiles(store.SecurityFolder, "access-state.v*.json").Order(StringComparer.Ordinal).ToList();

    private AccessSecurityStore StoreWithLegacyState(params object[] users)
        => StoreWithState(new
        {
            Users = users,
            Invitations = Array.Empty<object>(),
            Sessions = Array.Empty<object>(),
            Runners = Array.Empty<object>(),
            Enrollments = Array.Empty<object>(),
        });

    private AccessSecurityStore StoreWithState(object state)
    {
        var store = NewStore();
        Directory.CreateDirectory(store.SecurityFolder);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
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
