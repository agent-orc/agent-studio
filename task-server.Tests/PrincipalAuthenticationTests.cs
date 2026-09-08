using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

public sealed class PrincipalAuthenticationTests
{
    private const string StudioToken = "studio-principal-token-000000000000000000000001";
    private const string EngineToken = "engine-principal-token-000000000000000000000001";

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-secret-000000000000000000000000000000")]
    public async Task Missing_or_wrong_secret_returns_401(string? token)
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var client = Client(factory, token);
        client.DefaultRequestHeaders.Add("X-Client-Id", "identity-hint-only");

        var response = await client.GetAsync("/api/v1/workspaces");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Representative_route_for_every_scope_rejects_a_wrong_scope()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var manager = Client(factory, StudioToken);
        var runnerRead = await CreateAsync(
            manager,
            "runner-read",
            TaskServerPrincipalKinds.Runner,
            [TaskServerScopes.TasksRead],
            "runner-read");
        var runnerWrite = await CreateAsync(
            manager,
            "runner-write",
            TaskServerPrincipalKinds.Runner,
            [TaskServerScopes.RunsWrite],
            "runner-write");
        using var readOnlyRunner = Client(factory, runnerRead.Credential);
        using var writeOnlyRunner = Client(factory, runnerWrite.Credential);
        using var studio = Client(factory, StudioToken);
        using var engine = Client(factory, EngineToken);

        var checks = new[]
        {
            (writeOnlyRunner, HttpMethod.Get, "/api/v1/workspaces", TaskServerScopes.TasksRead),
            (readOnlyRunner, HttpMethod.Post, "/api/v1/workspaces", TaskServerScopes.TasksWrite),
            (studio, HttpMethod.Post, "/api/v1/runners/runner-a/claims", TaskServerScopes.RunsClaim),
            (engine, HttpMethod.Post, "/api/v1/runners/runner-a/claims", TaskServerScopes.RunsClaim),
            (studio, HttpMethod.Put, "/api/v1/runners/runner-a", TaskServerScopes.RunsWrite),
            (studio, HttpMethod.Post, "/api/v1/runners/runner-a/review-claims", TaskServerScopes.ReviewsClaim),
            (studio, HttpMethod.Post, "/api/v1/reviews/attempts/attempt-a/report", TaskServerScopes.ReviewsWrite),
            (studio, HttpMethod.Post, "/api/v1/orchestration/claims", TaskServerScopes.OrchestrationClaim),
            (studio, HttpMethod.Post, "/api/v1/orchestration/runs/run-a/stages/complete", TaskServerScopes.OrchestrationWrite),
            (studio, HttpMethod.Post, "/api/v1/runs/run-a/events", TaskServerScopes.EventsWrite),
            (readOnlyRunner, HttpMethod.Get, "/api/v1/management/status", TaskServerScopes.Management),
        };

        foreach (var (client, method, path, scope) in checks)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            var response = await client.SendAsync(request);
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"Expected {scope} to reject the wrong principal, got {(int)response.StatusCode} for {method} {path}.");
        }
    }

    [Fact]
    public async Task Runner_without_subscription_scope_cannot_connect_to_hub()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync(
                "/hubs/events/negotiate?negotiateVersion=1",
                null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.PostAsync(
                $"/hubs/events/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(StudioToken)}",
                null)).StatusCode);
        using var manager = Client(factory, StudioToken);
        var runner = await CreateAsync(
            manager,
            "runner-hub",
            TaskServerPrincipalKinds.Runner,
            null,
            "runner-hub");
        using var compromised = Client(factory, runner.Credential);

        var response = await compromised.PostAsync(
            "/hubs/events/negotiate?negotiateVersion=1",
            null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Compromised_runner_is_forbidden_from_every_studio_and_management_mutation()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var manager = Client(factory, StudioToken);
        var runner = await CreateAsync(
            manager,
            "runner-compromised",
            TaskServerPrincipalKinds.Runner,
            null,
            "runner-compromised");
        using var compromised = Client(factory, runner.Credential);
        var source = factory.Services.GetRequiredService<EndpointDataSource>();
        var protectedMutations = source.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Endpoint = endpoint,
                Scope = endpoint.Metadata.GetOrderedMetadata<TaskServerScopeMetadata>()
                    .LastOrDefault()?.Scope,
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
            })
            .Where(item => item.Scope is TaskServerScopes.TasksWrite or TaskServerScopes.Management)
            .SelectMany(item => item.Methods
                .Where(method => !HttpMethods.IsGet(method))
                .Select(method => (Method: method, Path: Materialize(item.Endpoint.RoutePattern.RawText!))))
            .ToArray();
        Assert.NotEmpty(protectedMutations);

        foreach (var (method, path) in protectedMutations)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            var response = await compromised.SendAsync(request);
            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"Compromised Runner reached {method} {path}: {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task Runner_principal_is_bound_to_its_runner_id()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var manager = Client(factory, StudioToken);
        var runner = await CreateAsync(
            manager,
            "runner-bound",
            TaskServerPrincipalKinds.Runner,
            null,
            "runner-bound");
        using var bound = Client(factory, runner.Credential);

        var response = await bound.PutAsJsonAsync(
            "/api/v1/runners/someone-else",
            new RegisterRunnerRequest(
                "someone-else", "host", "instance", "1.0.0", TaskServerProtocol.Current));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Rotation_honors_overlap_and_zero_overlap_expires_old_credentials()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var original = Client(factory, StudioToken);

        var firstRotation = await original.PostAsJsonAsync(
            "/api/v1/management/principals/bootstrap-studio/rotate",
            new RotatePrincipalRequest(60));
        firstRotation.EnsureSuccessStatusCode();
        var first = (await firstRotation.Content.ReadFromJsonAsync<IssuedPrincipalCredential>())!;
        Assert.Equal(HttpStatusCode.OK,
            (await original.GetAsync("/api/v1/management/status")).StatusCode);
        using var rotated = Client(factory, first.Credential);
        Assert.Equal(HttpStatusCode.OK,
            (await rotated.GetAsync("/api/v1/management/status")).StatusCode);

        var secondRotation = await rotated.PostAsJsonAsync(
            "/api/v1/management/principals/bootstrap-studio/rotate",
            new RotatePrincipalRequest(0));
        secondRotation.EnsureSuccessStatusCode();
        var second = (await secondRotation.Content.ReadFromJsonAsync<IssuedPrincipalCredential>())!;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await original.GetAsync("/api/v1/management/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await rotated.GetAsync("/api/v1/management/status")).StatusCode);
        using var current = Client(factory, second.Credential);
        Assert.Equal(HttpStatusCode.OK,
            (await current.GetAsync("/api/v1/management/status")).StatusCode);
    }

    [Fact]
    public async Task Revocation_takes_effect_without_restart_and_store_contains_no_plaintext_secret()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        using var manager = Client(factory, StudioToken);
        var issued = await CreateAsync(
            manager,
            "runner-revoked",
            TaskServerPrincipalKinds.Runner,
            null,
            "runner-revoked");
        using var runner = Client(factory, issued.Credential);
        Assert.Equal(HttpStatusCode.OK,
            (await runner.GetAsync("/api/v1/workspaces")).StatusCode);
        var listed = await manager.GetFromJsonAsync<List<PrincipalDto>>(
            "/api/v1/management/principals");
        Assert.NotNull(Assert.Single(listed!, item => item.PrincipalId == "runner-revoked").LastSeenAt);

        var revocation = await manager.PostAsync(
            "/api/v1/management/principals/runner-revoked/revoke",
            null);
        revocation.EnsureSuccessStatusCode();
        Assert.NotNull((await revocation.Content.ReadFromJsonAsync<PrincipalDto>())!.RevokedAt);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await runner.GetAsync("/api/v1/workspaces")).StatusCode);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(temp.Path, "task-server.db"));
        Assert.DoesNotContain(issued.Credential, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(temp.Path, "task-server.db")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret_hash FROM principal_credentials WHERE principal_id = 'runner-revoked';";
        var hash = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public async Task Every_versioned_route_declares_a_scope()
    {
        using var temp = new TempDirectory();
        await using var factory = Factory(temp.Path);
        _ = factory.CreateClient();
        var unscoped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .Where(endpoint => endpoint.RoutePattern.RawText is not "/api/v1/protocol"
                               and not "/api/v1/protocol/compatibility")
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<TaskServerScopeMetadata>().Count == 0)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Empty(unscoped);
        var writeRoutesUsingReadScope = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .Where(endpoint => endpoint.RoutePattern.RawText != "/api/v1/protocol/compatibility")
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Any(method => !HttpMethods.IsGet(method)) == true)
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<TaskServerScopeMetadata>()
                .LastOrDefault()?.Scope == TaskServerScopes.TasksRead)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.Empty(writeRoutesUsingReadScope);
        var hub = Assert.Single(
            factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/hubs/events/negotiate");
        Assert.Equal(
            TaskServerScopes.EventsSubscribe,
            hub.Metadata.GetOrderedMetadata<TaskServerScopeMetadata>().Last().Scope);
    }

    private static async Task<IssuedPrincipalCredential> CreateAsync(
        HttpClient manager,
        string principalId,
        string kind,
        IReadOnlyList<string>? scopes,
        string? runnerId)
    {
        var response = await manager.PostAsJsonAsync(
            "/api/v1/management/principals",
            new CreatePrincipalRequest(principalId, kind, scopes, runnerId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuedPrincipalCredential>())!;
    }

    private static PrincipalFactory Factory(string path)
        => new(path, new Dictionary<string, string?>
        {
            ["AUTH"] = "bearer",
            ["STUDIO_AUTH_TOKEN"] = StudioToken,
            ["ENGINE_AUTH_TOKEN"] = EngineToken,
        });

    private static HttpClient Client(PrincipalFactory factory, string? credential)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        if (credential is not null)
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private static string Materialize(string route)
        => Regex.Replace(route, "\\{[^}]+\\}", "test-id");

    private sealed class PrincipalFactory(
        string dataDirectory,
        IReadOnlyDictionary<string, string?> overrides)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                };
                foreach (var (key, value) in overrides)
                    values[key] = value;
                configuration.AddInMemoryCollection(values);
            });
        }
    }
}
