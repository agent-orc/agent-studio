using System.Text.Json;
using AgentStudio.GitHubApp;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCredentialInventoryTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);

    private static GitHubAppStatus App(bool connected = true, string? problem = null,
        IReadOnlyList<GitHubAppRepositoryCoverage>? repositories = null, IReadOnlyList<GitHubAppHostSync>? hosts = null)
        => new(connected, connected ? 7 : null, connected ? "studio-app" : null, connected ? "Studio App" : null,
            connected ? "owner" : null, null, null, connected ? Now : null, [],
            repositories ?? [], hosts ?? [], problem);

    private static CredentialInventoryDto Project(
        GitHubAppStatus? app = null,
        IReadOnlyList<GitHubAppHostKey>? keys = null,
        SecretListing? secrets = null,
        IReadOnlyList<RunnerServiceIdentity>? runners = null,
        IReadOnlyList<LocalCredentialInventory.HostGitAccess>? git = null)
        => LocalCredentialInventory.Project(new LocalCredentialInventory.Sources(
            app ?? App(), keys ?? [], secrets ?? new SecretListing([], []), runners ?? [], [], git ?? []), Now);

    private static CredentialInventoryItemDto Only(CredentialInventoryDto inventory, string kind)
        => Assert.Single(inventory.Items, item => item.Kind == kind);

    [Fact]
    public void Without_an_App_the_page_says_so_and_repositories_count_as_missing()
    {
        var inventory = Project(App(connected: false, repositories:
        [
            new("Core", "https://github.com/Owner/Core.git", GitHubAppCoverageStates.NotInstalled, null, null),
            new("Local", null, GitHubAppCoverageStates.NoRepositoryUrl, null, "The project registers no repository URL."),
        ]));

        var app = Only(inventory, "github_app");
        Assert.Equal("missing", app.State);
        Assert.True(app.Required);
        Assert.Contains("Settings > GitHub", app.Detail);
        var repository = Only(inventory, "github_app_installation");
        Assert.Equal("missing", repository.State);
        Assert.Equal("Owner/Core", repository.Repository);
    }

    [Fact]
    public void A_connected_App_and_its_covered_repositories_are_fine()
    {
        var inventory = Project(App(repositories:
            [new("Core", "https://github.com/Owner/Core.git", GitHubAppCoverageStates.Covered, 11, null)]));

        Assert.Equal("ok", Only(inventory, "github_app").State);
        Assert.Equal("ok", Only(inventory, "github_app_installation").State);
    }

    [Theory]
    [InlineData(4, "ok")]
    [InlineData(40, "expiring")]
    [InlineData(90, "missing")]
    public void Host_tokens_age_with_the_hosts_last_fetch(int minutes, string state)
    {
        var inventory = Project(App(hosts: [new("runner-1", RepositoryAccess.Write, 3, Now.AddMinutes(-minutes))]));

        var token = Only(inventory, "github_app_token");
        Assert.Equal(state, token.State);
        Assert.Equal("runner-1", token.HostId);
        Assert.Equal(RepositoryAccess.Write, token.Permission);
    }

    [Fact]
    public void A_host_key_nobody_used_since_the_App_connected_shows_as_unused()
    {
        var inventory = Project(
            App(hosts: [new("runner-1", RepositoryAccess.Read, 1, Now)]),
            keys: [new("runner-1", RepositoryAccess.Read), new("runner-2", RepositoryAccess.Read)]);

        var keys = inventory.Items.Where(item => item.Kind == "host_key").ToDictionary(item => item.HostId);
        Assert.Equal("ok", keys["runner-1"].State);
        Assert.Equal(Now, keys["runner-1"].LastUseAt);
        Assert.Equal("unused", keys["runner-2"].State);
    }

    [Fact]
    public void A_service_credential_warns_before_it_expires_and_a_revoked_one_is_gone()
    {
        RunnerCredential Credential(string id, DateTime? expires, DateTime? revoked = null) => new()
        {
            Id = id, SecretHash = "hash-value-never-listed", Scopes = ["runner.claim"], CreatedAt = Now, ExpiresAt = expires, RevokedAt = revoked,
        };
        var inventory = Project(runners:
        [
            new RunnerServiceIdentity
            {
                Id = "engine", Name = "Engine", CreatedAt = Now,
                Credentials = [Credential("a", Now.AddDays(3)), Credential("b", null), Credential("c", Now.AddDays(-1)), Credential("d", null, Now)],
            },
        ]);

        var states = inventory.Items.Where(item => item.Kind == "service_credential").Select(item => item.State).ToArray();
        Assert.Equal(["expiring", "ok", "missing"], states);
        Assert.DoesNotContain("hash-value-never-listed", JsonSerializer.Serialize(inventory));
    }

    [Theory]
    [InlineData("ready", "ok", RepositoryAccess.Write)]
    [InlineData("ready-no-workflow-scope", "expiring", RepositoryAccess.Write)]
    [InlineData("read-only", "missing", RepositoryAccess.Read)]
    public void A_hosts_own_git_probe_is_listed(string status, string state, string permission)
    {
        var item = Only(Project(git: [new("runner-1", status, null, Now)]), "host_git_access");

        Assert.Equal(state, item.State);
        Assert.Equal(permission, item.Permission);
    }
}
