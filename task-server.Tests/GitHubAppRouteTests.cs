using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.AttemptCredentials;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>The App connection in a Task Server's encrypted root store.</summary>
public sealed class GitHubAppRouteTests
{
    [Fact]
    public async Task The_connection_and_its_secrets_round_trip_through_the_root_store()
    {
        var roots = new MemoryRoots();
        var vault = new AttemptRootGitHubAppVault(roots);
        var secrets = new GitHubAppSecrets("pem-" + Guid.NewGuid().ToString("N"), "client-" + Guid.NewGuid().ToString("N"));
        var connection = new GitHubAppConnection(1, "slug", "name", "client-id", "owner", "https://github.com/apps/slug",
            DateTime.UnixEpoch, "owner", ["contents"],
            [new GitHubAppInstallation(11, "owner", "User", DateTime.UnixEpoch, "owner")]);

        Assert.Null(await vault.LoadConnectionAsync(default));
        Assert.Null(await vault.ReadSecretsAsync(default));

        await vault.WriteSecretsAsync(secrets, "owner", default);
        await vault.SaveConnectionAsync(connection, default);

        Assert.Equal(secrets, await vault.ReadSecretsAsync(default));
        var loaded = await vault.LoadConnectionAsync(default);
        Assert.Equal(11, Assert.Single(loaded!.Installations).Id);
        Assert.DoesNotContain(secrets.PrivateKeyPem, roots.Values["github-app-connection"], StringComparison.Ordinal);

        await vault.DeleteSecretsAsync("owner", default);
        await vault.DeleteConnectionAsync(default);
        Assert.Empty(roots.Values);
    }

    [Fact]
    public async Task An_attempt_without_an_imported_root_and_without_an_app_is_refused()
    {
        using var http = new HttpClient();
        var issuer = new GitHubAppTokenIssuer(http, new MemoryRoots());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            issuer.IssueReadAsync("repository-one", "https://github.com/example/one.git", default));

        Assert.Contains("not provisioned", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pushing_through_a_repository_token_is_never_assumed()
    {
        foreach (var kind in new[] { TaskServerPrincipalKinds.Runner, TaskServerPrincipalKinds.Studio, TaskServerPrincipalKinds.Engine })
            Assert.DoesNotContain(TaskServerScopes.RepositoriesWrite, TaskServerStore.DefaultScopes(kind));
        var coding = TaskServerStore.DefaultScopes(TaskServerPrincipalKinds.Runner, repositoryWrite: true);
        Assert.Contains(TaskServerScopes.RepositoriesWrite, coding);
        Assert.Contains(TaskServerScopes.ReviewsClaim, coding);
    }

    [Fact]
    public async Task Enrolment_decides_write_access_and_enrolling_again_can_change_it()
    {
        using var temp = new TempDirectory();
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero)));
        await store.InitializeAsync();

        async Task<bool> EnrolAsync(bool repositoryWrite)
        {
            var issued = await store.CreateEnrolmentAsync(
                new CreateEnrolmentRequest("runner:host-a", TaskServerPrincipalKinds.Runner, "host-a", RepositoryWrite: repositoryWrite),
                "owner", default);
            await store.ExchangeEnrolmentAsync(new ExchangeEnrolmentRequest(issued.EnrolmentCode, issued.InstallationId), default);
            return (await store.ListPrincipalsAsync(default)).Single(item => item.PrincipalId == "runner:host-a")
                .Scopes.Contains(TaskServerScopes.RepositoriesWrite);
        }

        Assert.False(await EnrolAsync(false));
        Assert.True(await EnrolAsync(true));
        Assert.False(await EnrolAsync(false));
    }

    private sealed class MemoryRoots : IAttemptRootSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task<string?> ReadAsync(string name, CancellationToken ct)
            => Task.FromResult(Values.TryGetValue(name, out var value) ? value : null);
        public Task WriteAsync(string name, string value, CancellationToken ct) { Values[name] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string name, CancellationToken ct) { Values.Remove(name); return Task.CompletedTask; }
    }
}
