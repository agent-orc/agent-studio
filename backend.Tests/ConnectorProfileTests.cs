using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AgentStudio.Connector;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ConnectorProfileTests
{
    [Fact]
    public void Inventory_is_the_approved_103_local_and_260_proxy_surface()
    {
        var inventory = ConnectorRouteInventory.Load();

        Assert.Equal(ConnectorRouteInventory.ExpectedInventorySha256, inventory.SourceChecksum);
        Assert.Equal(364, inventory.Operations.Count);
        Assert.Equal(103, inventory.DevSeatOperations.Count);
        Assert.Equal(260, inventory.TaskServerOperations.Count);
        Assert.Equal(1, inventory.Operations.Count(operation => operation.Classification == ConnectorRouteInventory.RetiredClassification));
        Assert.Equal(1, inventory.TaskServerOperations.Count(operation => operation.Method == "WS"));
    }

    [Fact]
    public async Task Published_api_and_hub_surface_is_fully_classified_and_hosts_no_authority_worker()
    {
        var transport = new RecordingTransport();
        await using var factory = BuildFactory(transport);
        using var client = CreateClient(factory);
        using var start = await SessionAsync(client);

        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true
                || endpoint.RoutePattern.RawText?.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) == true)
            .Distinct()
            .ToArray();
        var metadata = endpoints
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<ConnectorClassifiedEndpointMetadata>())
            .Select(item => new ConnectorRouteKey(item.Method, ConnectorRouteKey.NormalizePath(item.Path)))
            .Distinct()
            .ToArray();

        Assert.All(endpoints, endpoint => Assert.NotEmpty(
            endpoint.Metadata.GetOrderedMetadata<ConnectorClassifiedEndpointMetadata>()));
        Assert.Equal(363, metadata.Length); // 364 inventory rows minus 1 retired row, which is never mapped.
        Assert.Equal(103, endpoints
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<ConnectorClassifiedEndpointMetadata>())
            .Where(item => item.Classification == ConnectorRouteInventory.DevSeatClassification)
            .Select(item => new ConnectorRouteKey(item.Method, ConnectorRouteKey.NormalizePath(item.Path)))
            .Distinct()
            .Count());
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(),
            service => service.GetType().Assembly == typeof(ConnectorProfile).Assembly);
    }

    [Fact]
    public async Task Host_origin_session_and_csrf_are_enforced()
    {
        var transport = new RecordingTransport();
        await using var factory = BuildFactory(transport);
        using var client = CreateClient(factory);

        using (var badHost = new HttpRequestMessage(HttpMethod.Get, "/healthz"))
        {
            badHost.Headers.Host = "localhost:5031";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(badHost)).StatusCode);
        }

        using (var badOrigin = new HttpRequestMessage(HttpMethod.Get, "/connector/session"))
        {
            badOrigin.Headers.Add("Origin", "http://evil.test");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(badOrigin)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/environment")).StatusCode);
        using var session = await SessionAsync(client);
        var csrf = ReadCookie(session, ConnectorSessionStore.CsrfCookieName);

        using (var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/projects"))
        {
            missingCsrf.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
            missingCsrf.Content = JsonContent.Create(new { displayName = "test" });
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(missingCsrf)).StatusCode);
        }

        using var accepted = new HttpRequestMessage(HttpMethod.Post, "/api/projects");
        accepted.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
        accepted.Headers.Add(ConnectorSessionStore.CsrfHeaderName, csrf);
        accepted.Content = JsonContent.Create(new { displayName = "test" });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(accepted)).StatusCode);
    }

    [Fact]
    public async Task Proxy_strips_browser_identity_and_injects_the_studio_credential_and_protocol()
    {
        var transport = new RecordingTransport();
        await using var factory = BuildFactory(transport);
        using var client = CreateClient(factory);
        using var session = await SessionAsync(client);
        var csrf = ReadCookie(session, ConnectorSessionStore.CsrfCookieName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects");
        request.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
        request.Headers.Add(ConnectorSessionStore.CsrfHeaderName, csrf);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "browser-token");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add(TaskServerProtocol.HeaderName, "999");
        request.Headers.Add("X-Client-Id", "studio-window-1");
        request.Content = JsonContent.Create(new { displayName = "test" });

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var observed = Assert.Single(transport.ProxiedRequests);
        Assert.Equal("Bearer studio-secret", observed.Authorization);
        Assert.Equal(TaskServerProtocol.Current.ToString(), observed.Protocol);
        Assert.Equal("studio-window-1", observed.ClientId);
        Assert.False(observed.HasCookie);
        Assert.False(observed.HasForwardedFor);
        Assert.Equal("/api/v1/projects", observed.Path);
    }

    [Fact]
    public async Task Core_attach_task_lifecycle_routes_forward_with_the_unscoped_project_token()
    {
        // /api/tasks/{taskId}/move carries only a task id: the frontend does
        // not yet send a project id or "project" query for this core-attach
        // mutation. The connector has no task-to-project lookup of its own
        // (it is a mechanical path translator, not a Task Server client), so
        // it must forward using the reserved unscoped-project token rather
        // than failing the request.
        var transport = new RecordingTransport();
        await using var factory = BuildFactory(transport);
        using var client = CreateClient(factory);
        using var session = await SessionAsync(client);
        var csrf = ReadCookie(session, ConnectorSessionStore.CsrfCookieName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/tsk_core-attach-demo/move");
        request.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
        request.Headers.Add(ConnectorSessionStore.CsrfHeaderName, csrf);
        request.Content = JsonContent.Create(new { targetState = "2-ready" });

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var observed = Assert.Single(transport.ProxiedRequests);
        Assert.Equal(
            $"/api/v1/projects/{ConnectorProxy.UnscopedProjectToken}/tasks/tsk_core-attach-demo/move",
            observed.Path);
    }

    [Fact]
    public async Task Upstream_switch_is_validated_atomic_and_rotates_sessions()
    {
        var options = TestOptions();
        var sessions = new ConnectorSessionStore();
        var transport = new RecordingTransport();
        var manager = new ConnectorUpstreamManager(options, new TestCredentialSource(), transport, sessions);
        var old = manager.Capture();
        var candidate = old with { Generation = 2, BaseUri = new Uri("https://fallback.invalid/") };
        var issued = new DefaultHttpContext();
        sessions.Issue(issued.Response);
        var sessionCookie = issued.Response.Headers.SetCookie
            .Select(value => value?.Split(';', 2)[0])
            .Where(value => value is not null)
            .ToArray();
        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = string.Join("; ", sessionCookie!);
        Assert.True(sessions.ValidateSession(request.Request, out _));

        var rejected = await manager.TrySwitchAsync(
            candidate,
            new ConnectorUpstreamSwitchEvidence(false, true),
            default);
        Assert.False(rejected.Switched);
        Assert.Same(old, manager.Capture());

        var switched = await manager.TrySwitchAsync(
            candidate,
            new ConnectorUpstreamSwitchEvidence(true, true),
            default);
        Assert.True(switched.Switched);
        Assert.Same(candidate, manager.Capture());
        Assert.False(sessions.ValidateSession(request.Request, out _));
    }

    [Fact]
    public async Task Health_separates_liveness_from_redacted_upstream_readiness()
    {
        var transport = new RecordingTransport();
        await using var factory = BuildFactory(transport);
        using var client = CreateClient(factory);

        using var liveness = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);

        using var readiness = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        var json = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", json.GetProperty("status").GetString());
        Assert.Equal("remote-task-server", json.GetProperty("upstream").GetString());
        Assert.Equal(ConnectorRouteInventory.Load().RouteChecksum,
            json.GetProperty("routeChecksum").GetString());
        var body = await readiness.Content.ReadAsStringAsync();
        Assert.DoesNotContain("studio-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("task-server.invalid", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectorCredentialSource.DockerSecretPath, body, StringComparison.Ordinal);
    }

    [Fact]
    public void Docker_secret_requires_owner_read_only_permissions()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"connector-secret-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "docker-studio-secret\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
            Assert.Equal("docker-studio-secret", ConnectorCredentialSource.ReadDockerSecret(path));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Throws<InvalidOperationException>(() => ConnectorCredentialSource.ReadDockerSecret(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Listener_configuration_rejects_every_non_ipv6_loopback_authority()
    {
        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.ServerUrlsKey] = "http://0.0.0.0:5031",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ConnectorHost.ValidateConfiguredListener(invalid));

        var valid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.ServerUrlsKey] = "http://[::1]:5031",
        }).Build();
        ConnectorHost.ValidateConfiguredListener(valid);

        var extraEndpoint = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://[::1]:5031",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ConnectorHost.ValidateConfiguredListener(extraEndpoint));
    }

    private static WebApplicationFactory<Program> BuildFactory(RecordingTransport transport)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting(ConnectorProfile.ConfigurationKey, "connector");
            builder.UseSetting("Connector:Mode", "native");
            builder.UseSetting("Connector:Upstream:Mode", "remote");
            builder.UseSetting("Connector:Upstream:BaseUrl", "https://task-server.invalid");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [ConnectorProfile.ConfigurationKey] = "connector",
                    ["Connector:Mode"] = "native",
                    ["Connector:Upstream:Mode"] = "remote",
                    ["Connector:Upstream:BaseUrl"] = "https://task-server.invalid",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConnectorCredentialSource>();
                services.RemoveAll<IConnectorUpstreamTransport>();
                services.AddSingleton<IConnectorCredentialSource, TestCredentialSource>();
                services.AddSingleton<IConnectorUpstreamTransport>(transport);
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://[::1]:5031"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    private static async Task<HttpResponseMessage> SessionAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connector/session");
        request.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static string ReadCookie(HttpResponseMessage response, string name)
    {
        var prefix = name + "=";
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return cookie[prefix.Length..cookie.IndexOf(';')];
    }

    private static ConnectorOptions TestOptions() => new(
        ConnectorOptions.NativeMode,
        ConnectorOptions.DefaultAuthority,
        ConnectorOptions.DefaultStudioOrigin,
        "remote",
        new Uri("https://task-server.invalid/"),
        null,
        1,
        "remote-task-server");

    private sealed class TestCredentialSource : IConnectorCredentialSource
    {
        public ConnectorCredentialLoadResult Load(ConnectorOptions options)
            => new(new ConnectorCredential("studio-secret"), null);
    }

    private sealed record ObservedRequest(
        string Path,
        string? Authorization,
        string? Protocol,
        string? ClientId,
        bool HasCookie,
        bool HasForwardedFor);

    private sealed class RecordingTransport : IConnectorUpstreamTransport
    {
        public List<ObservedRequest> ProxiedRequests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(
            ConnectorUpstreamSnapshot snapshot,
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/readyz") return Task.FromResult(Json(HttpStatusCode.OK, new { status = "ready" }));
            if (path == "/api/v1/protocol/compatibility")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, new ProtocolCompatibilityResponse(
                    true,
                    new ProtocolRangeDto(2, 1, 2, "test", "task-server", ["studio"]))));
            }

            ProxiedRequests.Add(new ObservedRequest(
                path,
                request.Headers.Authorization?.ToString(),
                request.Headers.GetValues(TaskServerProtocol.HeaderName).SingleOrDefault(),
                request.Headers.TryGetValues("X-Client-Id", out var clientIds) ? clientIds.SingleOrDefault() : null,
                request.Headers.Contains("Cookie"),
                request.Headers.Contains("X-Forwarded-For")));
            return Task.FromResult(Json(HttpStatusCode.OK, new { proxied = true }));
        }

        public Task<ClientWebSocket> ConnectWebSocketAsync(
            ConnectorUpstreamSnapshot snapshot,
            Uri uri,
            IReadOnlyList<string> subProtocols,
            string? clientId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        private static HttpResponseMessage Json(HttpStatusCode status, object body)
            => new(status) { Content = JsonContent.Create(body) };
    }
}
