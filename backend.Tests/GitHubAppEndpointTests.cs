using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.GitHubApp;
using AgentStudio.Secrets;
using AgentStudio.Security;

using AgentStudio.TaskServer.Contracts.GitHubApp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Who gets a repository token over HTTP. The local API has no sign-in, so the
/// token route must fail closed on its own, in every profile.
/// Every test shares one local and one tunnel host (<see cref="SharedHostFixture"/>):
/// key files carry a fresh runner id, the vault keeps its connection file in the
/// test's own folder, and the platform's fixed secret names are written before
/// they are read.
/// </summary>
[TestCaseOrderer(SharedHostTestOrder.TypeName, SharedHostTestOrder.AssemblyName)]
public sealed class GitHubAppEndpointTests(GitHubAppEndpointTests.LocalHost local, GitHubAppEndpointTests.TunnelHost tunnel)
    : IClassFixture<GitHubAppEndpointTests.LocalHost>, IClassFixture<GitHubAppEndpointTests.TunnelHost>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "github-app-endpoints-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The local profile, with its own key-encryption key outside the workspace.</summary>
    public class LocalHost : SharedHostFixture
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings() =>
        [
            new("Secrets:KeyFile", Path.Combine(Root, "keys", "secrets.kek")),
            new("Secrets:KeyEnvironmentVariable", "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N")),
        ];
    }

    /// <summary>The local profile with a tunnel listener beside the operator's port.</summary>
    public sealed class TunnelHost : LocalHost
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings()
            => [.. base.Settings(), new(OperatorListener.PortSetting, "5999")];
    }

    /// <summary>A runner id of this test alone: the host keeps its key files beside those of every other test.</summary>
    private static string NewRunnerId() => "runner-" + Guid.NewGuid().ToString("N");

    /// <summary>A security folder of this test alone, for a vault whose connection file would be the shared host's.</summary>
    private string SecurityFolder => Path.Combine(_root, "security");

    private static HttpRequestMessage Ask(string access, string? bearer, string runnerId, string? clientId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/runners/{runnerId}/repository-credentials")
        {
            Content = JsonContent.Create(new { access }),
        };
        request.Headers.TryAddWithoutValidation("X-Client-Id", clientId ?? runnerId);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private string IssueKey(string runnerId, string access)
    {
        var key = "key-" + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(local.Workspace, ".security", "github-app", "host-keys");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"{runnerId}.{access}.sha256"),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        return key;
    }

    [Fact]
    public async Task Without_a_key_the_open_local_api_hands_out_nothing()
    {
        using var client = local.CreateClient();

        using var response = await client.SendAsync(Ask(RepositoryAccess.Read, null, NewRunnerId()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_runner_credential_means_nothing_in_the_local_profile()
    {
        // The open local API can mint one, so it must not open the token route.
        using var client = local.CreateClient();
        var security = local.Services.GetRequiredService<AccessSecurityStore>();
        var created = security.CreateEnrollment(new RunnerEnrollmentRequest("runner-1", [RunnerScopes.RepositoryWrite], null, null));
        var enrolled = security.EnrollRunner(created.Code);

        using var response = await client.SendAsync(Ask(RepositoryAccess.Write, enrolled.Secret, enrolled.Runner.Id, "runner-1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_gets_nothing()
    {
        using var client = local.CreateClient();
        var runner = NewRunnerId();
        IssueKey(runner, RepositoryAccess.Write);

        using var response = await client.SendAsync(Ask(RepositoryAccess.Read, "key-" + Guid.NewGuid().ToString("N"), runner));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_of_another_host_gets_nothing()
    {
        using var client = local.CreateClient();
        var key = IssueKey(NewRunnerId(), RepositoryAccess.Write);

        using var response = await client.SendAsync(Ask(RepositoryAccess.Read, key, NewRunnerId()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_read_key_cannot_ask_for_write_access()
    {
        using var client = local.CreateClient();
        var runner = NewRunnerId();
        var key = IssueKey(runner, RepositoryAccess.Read);

        using var denied = await client.SendAsync(Ask(RepositoryAccess.Write, key, runner));
        using var allowed = await client.SendAsync(Ask(RepositoryAccess.Read, key, runner));

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task A_write_key_may_also_read_and_no_app_means_no_content()
    {
        using var client = local.CreateClient();
        var runner = NewRunnerId();
        var key = IssueKey(runner, RepositoryAccess.Write);

        using var read = await client.SendAsync(Ask(RepositoryAccess.Read, key, runner));
        using var write = await client.SendAsync(Ask(RepositoryAccess.Write, key, runner));

        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);
    }

    [Fact]
    public async Task An_unknown_access_level_is_refused()
    {
        using var client = local.CreateClient();
        var runner = NewRunnerId();
        var key = IssueKey(runner, RepositoryAccess.Write);

        using var response = await client.SendAsync(Ask("admin", key, runner));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    // The Runner service routes, whatever they are called.
    [InlineData("POST", "/api/runner/claim", IamPermissions.RunnerService, true)]
    [InlineData("POST", "/api/v1/runners/runner-1/repository-credentials", IamPermissions.RunnerService, true)]
    [InlineData("POST", "/api/v1/protocol/compatibility", IamPermissions.WorkspaceRead, true)]
    // Registration and redeeming a one-time code prove themselves.
    [InlineData("POST", "/api/clients/register", IamPermissions.WorkspaceOperate, true)]
    [InlineData("POST", "/API/AUTH/runner-enroll", IamPermissions.Anonymous, true)]
    // What the Task API does for an agent.
    [InlineData("POST", "/api/tasks", IamPermissions.ProjectCardsWrite, true)]
    [InlineData("POST", "/api/tasks/card-1/move", IamPermissions.ProjectCardsWrite, true)]
    [InlineData("PUT", "/api/tasks/card-1/files/result.md", IamPermissions.ProjectCardsWrite, true)]
    [InlineData("POST", "/api/tasks/card-1/continue", IamPermissions.ProjectRunsOperate, true)]
    // Everything else stays with the operator.
    [InlineData("POST", "/api/tasks/card-1/merge", IamPermissions.ProjectRunsOperate, false)]
    [InlineData("POST", "/api/tasks/card-1/open-in-vscode", IamPermissions.ProjectRunsOperate, false)]
    [InlineData("POST", "/api/projects/PROJ-1/urls/url-1/start", IamPermissions.ProjectRunsOperate, false)]
    [InlineData("PUT", "/api/projects/PROJ-1/urls/url-1", IamPermissions.ProjectSettingsWrite, false)]
    [InlineData("PUT", "/api/projects/PROJ-1", IamPermissions.ProjectSettingsWrite, false)]
    [InlineData("POST", "/api/projects", IamPermissions.WorkspaceSettingsWrite, false)]
    [InlineData("POST", "/api/projects/PROJ-1/auto-tag/backfill", IamPermissions.ProjectCardsWrite, false)]
    [InlineData("POST", "/api/auth/bootstrap", IamPermissions.Anonymous, false)]
    [InlineData("POST", "/api/auth/runner-enrollments", IamPermissions.AccessManage, false)]
    [InlineData("POST", "/api/secrets/workspace", IamPermissions.SecretsWrite, false)]
    [InlineData("PUT", "/api/v1/management/github-app/credentials", IamPermissions.WorkspaceAdminister, false)]
    // A route nobody declared has no place on a tunnel.
    [InlineData("POST", "/api/new-route", null, false)]
    public void Hosts_may_write_only_what_they_write_anyway(string method, string path, string? permission, bool allowed)
        => Assert.Equal(allowed, OperatorListener.HostMayWrite(method, path, permission));

    [Fact]
    public void A_tunnel_cannot_reach_an_operator_route_once_the_listener_exists()
    {
        const string route = "/api/v1/management/github-app/credentials";
        const string administer = IamPermissions.WorkspaceAdminister;

        Assert.Equal("operator-listener-required", OperatorListener.Decide("PUT", route, true, administer, 5031, 5032, null)?.Code);
        Assert.Null(OperatorListener.Decide("PUT", route, true, administer, 5032, 5032, "http://localhost:4011"));
        // The App's status is the Owner's as well; ordinary reads stay open to hosts.
        Assert.Equal("operator-listener-required", OperatorListener.Decide("GET", "/api/v1/management/github-app", true, administer, 5031, 5032, null)?.Code);
        Assert.Null(OperatorListener.Decide("GET", "/api/tasks", true, IamPermissions.ProjectRead, 5031, 5032, null));
        // Nothing runs for a route that does not exist.
        Assert.Null(OperatorListener.Decide("POST", "/api/nothing", false, null, 5031, 5032, null));
        // Without tunnels there is no second port, and every port is the operator's.
        Assert.Null(OperatorListener.Decide("PUT", route, true, administer, 5031, null, null));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://evil.example:4011")]
    [InlineData("null")]
    public void A_rebound_website_cannot_write_on_any_port(string origin)
    {
        Assert.Equal("cross-origin-denied",
            OperatorListener.Decide("PUT", "/api/v1/management/github-app/credentials", true, IamPermissions.WorkspaceAdminister, 5032, 5032, origin)?.Code);
        Assert.Equal("cross-origin-denied",
            OperatorListener.Decide("POST", "/api/projects/PROJ-1/urls/url-1/start", true, IamPermissions.ProjectRunsOperate, 5031, null, origin)?.Code);
    }

    [Theory]
    [InlineData("http://localhost:4011")]
    [InlineData("http://127.0.0.1:4011")]
    public void This_studios_own_page_may_write(string origin)
        => Assert.Null(OperatorListener.Decide("PUT", "/api/v1/management/github-app/credentials", true, IamPermissions.WorkspaceAdminister, 5032, 5032, origin));

    /// <summary>What a request through a tunnel gets from the real routes: never the listener's refusal.</summary>
    [Theory]
    [InlineData("POST", "/api/runner/claim")]
    [InlineData("POST", "/api/runner/completion")]
    [InlineData("POST", "/api/runner/events")]
    [InlineData("POST", "/api/runner/logs")]
    [InlineData("POST", "/api/runner/lease/renew")]
    [InlineData("POST", "/api/runner/project-chat/claim")]
    [InlineData("POST", "/api/runner/orchestrator-decisions/claim")]
    [InlineData("POST", "/api/runner/secrets/resolve")]
    [InlineData("POST", "/api/clients/register")]
    [InlineData("POST", "/api/clients/runner-1/runner-git-capability")]
    [InlineData("POST", "/api/auth/runner-enroll")]
    [InlineData("POST", "/api/deploy/jobs/project/job/claim")]
    [InlineData("POST", "/api/v1/protocol/compatibility")]
    [InlineData("PUT", "/api/v1/runners/runner-1")]
    [InlineData("PUT", "/api/v1/runners/runner-1/capabilities")]
    [InlineData("POST", "/api/v1/runners/runner-1/claims")]
    [InlineData("POST", "/api/v1/runners/runner-1/review-claims")]
    [InlineData("POST", "/api/v1/runners/runner-1/repository-credentials")]
    [InlineData("POST", "/api/v1/runs/run-1/completion")]
    [InlineData("POST", "/api/v1/reviews/attempts/attempt-1/reports")]
    [InlineData("POST", "/api/v1/gates/claims")]
    [InlineData("POST", "/api/v1/orchestration/decision-jobs")]
    [InlineData("POST", "/api/tasks/")]
    [InlineData("POST", "/api/tasks/card-1/move")]
    [InlineData("PUT", "/api/tasks/card-1/files/result.md")]
    [InlineData("POST", "/api/tasks/card-1/attachments")]
    [InlineData("GET", "/api/tasks")]
    [InlineData("GET", "/api/projects")]
    [InlineData("GET", "/api/runner/status")]
    public async Task A_host_keeps_every_route_it_uses_behind_the_listener(string method, string path)
    {
        using var client = tunnel.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Id", "local-default");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET") request.Content = JsonContent.Create(new { });

        using var response = await client.SendAsync(request);

        Assert.DoesNotContain("operator-listener-required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>The paths the review found: commands on this machine, repository URLs, identities, spellings, aliases.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/management/github-app")]
    [InlineData("PUT", "/api/v1/management/github-app/credentials")]
    [InlineData("POST", "/api/projects/PROJ-001/urls/url-1/start")]
    [InlineData("PUT", "/api/projects/PROJ-001/urls/url-1")]
    [InlineData("PUT", "/api/projects/PROJ-001/urls/repo")]
    [InlineData("PUT", "/api/projects/PROJ-001")]
    [InlineData("PUT", "/API/PROJECTS/PROJ-001")]
    [InlineData("POST", "/api/projects")]
    [InlineData("POST", "/api/v1/studio/auth/bootstrap")]
    [InlineData("POST", "/api/auth/bootstrap")]
    [InlineData("POST", "/api/auth/runner-enrollments")]
    [InlineData("POST", "/api/secrets/workspace")]
    [InlineData("PUT", "/api/secrets/backends")]
    [InlineData("POST", "/api/first-run/git-setups")]
    public async Task A_tunnel_reaches_none_of_the_operators_routes(string method, string path)
    {
        using var client = tunnel.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Id", "local-default");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET") request.Content = JsonContent.Create(new { });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("operator-listener-required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_platform_secret_cannot_become_a_backends_credential()
    {
        var backends = local.Services.GetRequiredService<SecretBackendSettings>();

        var refused = await Assert.ThrowsAsync<SecretValidationException>(() => backends.SaveAsync(new SecretBackendBinding(
            SecretScope.Workspace.Key, "vault", "https://attacker.example", "kv", null,
            "secret://workspace/" + SecretProviderGitHubAppVault.PrivateKeyName, null), default));

        Assert.Equal("invalid-backend-credential", refused.Code);
    }

    [Theory]
    [InlineData("GITHUB_APP_PRIVATE_KEY", false)]
    [InlineData("GITHUB_APP_ID", false)]
    [InlineData("AGENT_STUDIO_GITHUB_APP_PRIVATE_KEY", true)]
    [InlineData("AGENT_STUDIO_GITHUB_APP_CLIENT_SECRET", true)]
    public void Only_the_platforms_own_names_are_reserved(string name, bool reserved)
        => Assert.Equal(reserved, SecretAccessPolicy.IsPlatformOnly(name));

    [Fact]
    public async Task An_expired_store_stops_answering_when_refreshing_keeps_failing()
    {
        var store = Path.Combine(_root, "studio-host", "git-credentials");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var vault = new FixedConnectionVault();
        using var http = new HttpClient(new TokenOnlyGitHub(clock));
        var broker = new GitHubAppBroker(new GitHubAppClient(http, clock), vault,
            new FixedCatalog("https://github.com/owner/site.git"), clock);
        var service = new GitHubAppHostCredentialService(broker, local.Services.GetRequiredService<IConfiguration>(), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubAppHostCredentialService>.Instance, store);

        await service.RefreshAsync(default);
        Assert.True(File.Exists(store));
        Assert.True(File.Exists(store + ".config"));

        service.DropIfExpired();
        Assert.True(File.Exists(store));

        clock.Advance(TimeSpan.FromMinutes(61));
        service.DropIfExpired();
        Assert.False(File.Exists(store));
        Assert.False(File.Exists(store + ".config"));
    }

    [Fact]
    public async Task Background_start_writes_the_host_tokens_and_signals_readiness()
    {
        var store = Path.Combine(_root, "studio-host-start", "git-credentials");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero));
        using var http = new HttpClient(new TokenOnlyGitHub(clock));
        var readiness = new GitHubAppHostCredentialReadiness();
        var service = HostCredentials(new GitHubAppBroker(new GitHubAppClient(http, clock), new FixedConnectionVault(),
            new FixedCatalog("https://github.com/owner/private.git"), clock), clock, store, readiness);
        Assert.True(readiness.Pending);
        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(await readiness.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.True(File.Exists(store));
            Assert.True(File.Exists(store + ".config"));
            Assert.False(readiness.Pending);
            Assert.True(await readiness.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(61));
            service.DropIfExpired();
        }
    }

    [Fact]
    public async Task A_held_repository_catalog_does_not_hold_host_start()
    {
        var store = Path.Combine(_root, "studio-host-held-catalog", "git-credentials");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero));
        using var http = new HttpClient(new TokenOnlyGitHub(clock));
        var catalog = new BlockingCatalog("https://github.com/owner/private.git");
        var readiness = new GitHubAppHostCredentialReadiness();
        var service = HostCredentials(new GitHubAppBroker(new GitHubAppClient(http, clock), new FixedConnectionVault(),
            catalog, clock), clock, store, readiness);
        try
        {
            await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await catalog.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(readiness.Pending);
            Assert.False(File.Exists(store));

            catalog.Release();
            Assert.True(await readiness.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.True(File.Exists(store));
        }
        finally
        {
            catalog.Release();
            await service.StopAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(61));
            service.DropIfExpired();
        }
    }

    [Fact]
    public async Task A_failed_start_refresh_keeps_integration_waiting_until_a_retry_writes_the_tokens()
    {
        var store = Path.Combine(_root, "studio-host-retry", "git-credentials");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero));
        var github = new SwitchableGitHub(new TokenOnlyGitHub(clock)) { Refuse = true };
        using var http = new HttpClient(github);
        var readiness = new GitHubAppHostCredentialReadiness();
        var service = HostCredentials(new GitHubAppBroker(new GitHubAppClient(http, clock), new FixedConnectionVault(),
            new FixedCatalog("https://github.com/owner/private.git"), clock), clock, store, readiness);
        service.NotReadyRetryInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            // A failed first refresh does not hold the host start.
            await service.StartAsync(CancellationToken.None);

            Assert.False(File.Exists(store));
            Assert.True(readiness.Pending);
            using var failureWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (string.IsNullOrWhiteSpace(readiness.LastFailure))
                await Task.Delay(10, failureWait.Token);
            Assert.False(await readiness.WaitAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));

            github.Refuse = false;

            Assert.True(await readiness.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            Assert.True(File.Exists(store));
            Assert.Null(readiness.LastFailure);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(61));
            service.DropIfExpired();
        }
    }

    [Fact]
    public async Task A_host_without_the_credential_service_never_waits_for_it()
    {
        var readiness = new GitHubAppHostCredentialReadiness();

        Assert.False(readiness.Pending);
        Assert.True(await readiness.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    private static GitHubAppHostCredentialService HostCredentials(GitHubAppBroker broker, TimeProvider clock,
        string store, GitHubAppHostCredentialReadiness readiness)
        => new(broker, new ConfigurationBuilder().Build(), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubAppHostCredentialService>.Instance, store, readiness);

    private sealed class SwitchableGitHub(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool Refuse;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Refuse
                ? throw new HttpRequestException("GitHub is not reachable yet.")
                : base.SendAsync(request, cancellationToken);
    }

    private sealed class FixedConnectionVault : IGitHubAppVault
    {
        private static readonly string Key = CreateKey();

        private static string CreateKey()
        {
            using var rsa = RSA.Create(2048);
            return rsa.ExportRSAPrivateKeyPem();
        }

        public Task<GitHubAppConnection?> LoadConnectionAsync(CancellationToken ct) => Task.FromResult<GitHubAppConnection?>(
            new GitHubAppConnection(1, "slug", "name", "client", "owner", "https://github.com/apps/slug", DateTime.UnixEpoch, "owner",
                ["contents"], [new GitHubAppInstallation(11, "owner", "User", DateTime.UnixEpoch, "owner")]));
        public Task SaveConnectionAsync(GitHubAppConnection connection, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteConnectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<GitHubAppSecrets?> ReadSecretsAsync(CancellationToken ct)
            => Task.FromResult<GitHubAppSecrets?>(new GitHubAppSecrets(Key, "client-secret"));
        public Task WriteSecretsAsync(GitHubAppSecrets secrets, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteSecretsAsync(string actor, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedCatalog(string url) : IRegisteredRepositoryCatalog
    {
        public Task<IReadOnlyList<RegisteredRepository>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RegisteredRepository>>([new("Site", url)]);
    }

    private sealed class BlockingCatalog(string url) : IRegisteredRepositoryCatalog
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Release() => _released.TrySetResult();

        public async Task<IReadOnlyList<RegisteredRepository>> ListAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(ct);
            return [new RegisteredRepository("Site", url)];
        }
    }

    private sealed class TokenOnlyGitHub(TimeProvider clock) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/installation", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { id = 11 })
                : JsonSerializer.Serialize(new
                {
                    token = "token-" + Guid.NewGuid().ToString("N"),
                    expires_at = clock.GetUtcNow().UtcDateTime.AddHours(1).ToString("O"),
                    permissions = new { contents = "write", metadata = "read" },
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Theory]
    [InlineData("../runner-1")]
    [InlineData(".hidden")]
    [InlineData("runner/1")]
    public void A_host_id_never_reaches_a_path(string runnerId)
    {
        var keys = new GitHubAppHostKeys(Path.Combine(_root, "elsewhere"));
        Assert.Null(keys.Granted(runnerId, "any-key"));
    }

    [Fact]
    public async Task The_status_route_answers_without_a_connection_and_stores_nothing_in_the_browser_cache()
    {
        using var client = local.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Id", "local-default");

        using var response = await client.GetAsync("/api/v1/management/github-app");

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var status = await response.Content.ReadFromJsonAsync<GitHubAppStatus>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(status!.Connected);
    }

    [Fact]
    public async Task The_app_key_lands_in_the_secret_provider_and_never_in_the_connection_file()
    {
        var secrets = local.Services.GetRequiredService<ISecretProvider>();
        var vault = new SecretProviderGitHubAppVault(secrets, SecurityFolder);
        var material = new GitHubAppSecrets("pem-" + Guid.NewGuid().ToString("N"), "client-" + Guid.NewGuid().ToString("N"));

        await vault.WriteSecretsAsync(material, "owner", default);
        await vault.SaveConnectionAsync(new GitHubAppConnection(1, "slug", "name", "client-id", "owner", "https://github.com/apps/slug",
            DateTime.UnixEpoch, "owner", ["contents"], []), default);

        var earlier = await secrets.ReadAuditAsync(SecretScope.Workspace, 50, default);
        var read = await vault.ReadSecretsAsync(default);
        Assert.Equal(material.PrivateKeyPem, read!.PrivateKeyPem);
        Assert.Equal(material.ClientSecret, read.ClientSecret);
        var file = await File.ReadAllTextAsync(Path.Combine(SecurityFolder, "github-app", "connection.json"));
        Assert.DoesNotContain(material.PrivateKeyPem, file, StringComparison.Ordinal);
        Assert.DoesNotContain(material.ClientSecret, file, StringComparison.Ordinal);
        Assert.Equal("slug", (await vault.LoadConnectionAsync(default))!.Slug);

        // Every read is audited, under the platform's own name.
        var audit = await secrets.ReadAuditAsync(SecretScope.Workspace, 50, default);
        Assert.Contains(audit.Except(earlier), entry => entry.Action == SecretAuditActions.Resolve
                                        && entry.Reference.EndsWith(SecretProviderGitHubAppVault.PrivateKeyName, StringComparison.Ordinal)
                                        && entry.Purpose == "github-app-token-issue"
                                        && entry.Outcome == "ok");

        await vault.DeleteSecretsAsync("owner", default);
        Assert.Null(await vault.ReadSecretsAsync(default));
        // Removing them twice is fine: the goal is reached.
        await vault.DeleteSecretsAsync("owner", default);
    }

    [Fact]
    public async Task No_workload_ever_receives_the_app_key()
    {
        var secrets = local.Services.GetRequiredService<ISecretProvider>();
        await new SecretProviderGitHubAppVault(secrets, SecurityFolder)
            .WriteSecretsAsync(new GitHubAppSecrets("pem", "client"), "owner", default);
        var workload = new SecretWorkload(SecretWorkload.TaskRun, "run-1", "some-project", null);

        // At the workspace, and through a deeper scope that would inherit it.
        foreach (var reference in new[]
                 {
                     new SecretReference(SecretScope.Workspace, SecretProviderGitHubAppVault.PrivateKeyName),
                     new SecretReference(SecretScope.ForProject("some-project"), SecretProviderGitHubAppVault.PrivateKeyName),
                     new SecretReference(SecretScope.Workspace, SecretProviderGitHubAppVault.ClientSecretName),
                 })
        {
            var denied = await Assert.ThrowsAsync<SecretAccessDeniedException>(() =>
                secrets.GetForWorkloadAsync(reference, workload, "deploy:stage", default));
            Assert.Equal("platform-secret", denied.Code);
        }
    }

    [Fact]
    public async Task The_platform_reads_nothing_but_its_own_secrets()
    {
        var secrets = local.Services.GetRequiredService<ISecretProvider>();

        await Assert.ThrowsAsync<SecretAccessDeniedException>(() => secrets.GetForPlatformAsync(
            new SecretReference(SecretScope.Workspace, "SOME_APP_KEY"), "any", default));
    }

    [Fact]
    public async Task A_revoked_app_key_reads_as_gone()
    {
        var secrets = local.Services.GetRequiredService<ISecretProvider>();
        var vault = new SecretProviderGitHubAppVault(secrets, SecurityFolder);
        await vault.WriteSecretsAsync(new GitHubAppSecrets("pem", "client"), "owner", default);

        await secrets.RevokeAsync(SecretScope.Workspace, SecretProviderGitHubAppVault.PrivateKeyName, null, SecretActor.Human("owner"), default);

        Assert.Null(await vault.ReadSecretsAsync(default));
    }

    [Fact]
    public void The_workspace_repository_is_read_from_its_git_config()
    {
        var repository = Path.Combine(_root, "evidence");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        File.WriteAllText(Path.Combine(repository, ".git", "config"),
            "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/other/wrong\n"
            + "[remote \"origin\"]\n\turl = https://github.com/owner/evidence\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");

        Assert.Equal("https://github.com/owner/evidence", ProjectRegistryRepositoryCatalog.WorkspaceOrigin(repository));
        Assert.Null(ProjectRegistryRepositoryCatalog.WorkspaceOrigin(Path.Combine(_root, "missing")));
        Assert.Null(ProjectRegistryRepositoryCatalog.WorkspaceOrigin(null));
    }

    [Fact]
    public async Task The_platform_reads_workspace_level_secrets_only()
    {
        var secrets = local.Services.GetRequiredService<ISecretProvider>();

        await Assert.ThrowsAsync<SecretAccessDeniedException>(() => secrets.GetForPlatformAsync(
            new SecretReference(SecretScope.ForProject("some-project"), "NAME"), "any", default));
    }
}
