using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Connector;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static AgentStudio.Tests.ConnectorBrowser;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ConnectorProfileTests
{
    [Fact]
    public void Inventory_is_the_approved_108_local_and_325_proxy_surface()
    {
        var inventory = ConnectorRouteInventory.Load();

        Assert.Equal(ConnectorRouteInventory.ExpectedInventorySha256, inventory.SourceChecksum);
        Assert.Equal(433, inventory.Operations.Count);
        Assert.Equal(108, inventory.DevSeatOperations.Count);
        Assert.Equal(325, inventory.TaskServerOperations.Count);
        var hub = Assert.Single(inventory.TaskServerOperations, operation => operation.Method == "WS");
        Assert.Equal("/hubs/v1/studio", hub.Path);
    }

    [Fact]
    public async Task Published_api_and_hub_surface_is_fully_classified_and_hosts_no_authority_worker()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        using var start = await SessionAsync(connector.Client);

        var endpoints = connector.Services.GetServices<EndpointDataSource>()
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
        Assert.Equal(433, metadata.Length);
        Assert.Equal(108, endpoints
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<ConnectorClassifiedEndpointMetadata>())
            .Where(item => item.Classification == ConnectorRouteInventory.DevSeatClassification)
            .Select(item => new ConnectorRouteKey(item.Method, ConnectorRouteKey.NormalizePath(item.Path)))
            .Distinct()
            .Count());
        Assert.DoesNotContain(connector.Services.GetServices<IHostedService>(),
            service => service.GetType().Assembly == typeof(ConnectorProfile).Assembly);
    }

    /// <summary>
    /// Route coverage (gate 4): walks every Task Server-owned operation in
    /// docs/studio-route-ownership/routes.json, sends it through the connector
    /// as the Studio browser would, and asserts it reached the upstream at its
    /// approved target route with the injected Studio credential. A local
    /// handler answering instead, a missing mapping, or a wrong target fails.
    /// </summary>
    [Fact]
    public async Task Every_task_server_owned_inventory_route_is_forwarded_to_its_target()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);
        var inventory = ConnectorRouteInventory.Load();
        var failures = new List<string>();
        var forwarded = 0;

        foreach (var operation in inventory.TaskServerOperations)
        {
            var hub = operation.Method == "WS";
            var method = hub ? HttpMethod.Post : new HttpMethod(operation.Method);
            var requestPath = hub ? operation.Path + "/negotiate" : SamplePath(operation.Path);
            var expected = hub ? operation.TargetRoute + "/negotiate" : ExpectedTarget(operation);
            using var request = new HttpRequestMessage(method, requestPath);
            if (method != HttpMethod.Get)
            {
                request.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
                request.Headers.Add(ConnectorSessionStore.CsrfHeaderName, csrf);
                request.Content = JsonContent.Create(new { });
            }

            var before = transport.ProxiedRequests.Count;
            using var response = await connector.Client.SendAsync(request);
            var observed = transport.ProxiedRequests.Skip(before).ToArray();
            if (response.StatusCode != HttpStatusCode.OK || observed.Length != 1)
            {
                failures.Add($"{operation.Method} {operation.Path}: HTTP {(int)response.StatusCode}, {observed.Length} upstream request(s)");
                continue;
            }
            var upstream = observed[0];
            if (!string.Equals(upstream.Path, expected, StringComparison.Ordinal)
                || upstream.Method != method.Method
                || upstream.Authorization != "Bearer studio-secret")
            {
                failures.Add($"{operation.Method} {operation.Path}: forwarded {upstream.Method} {upstream.Path}, expected {expected}");
                continue;
            }
            forwarded++;
        }

        Assert.True(failures.Count == 0, "Task Server routes not forwarded:\n" + string.Join('\n', failures));
        Assert.Equal(325, forwarded);
    }

    [Fact]
    public void Parameters_embedded_in_a_literal_segment_are_expanded_by_name()
    {
        // Regression found by the coverage walk: "workbench:{project}" used to
        // be forwarded verbatim, so every workbench chat turn lost its project.
        var operation = new ConnectorRouteOperation(
            "fe-test",
            "POST",
            "/api/orchestrator/sessions/workbench:{project}/{workbenchKey}/turns",
            ConnectorRouteInventory.TaskServerClassification,
            "/api/v1/studio/orchestrator/sessions/workbench:{project}/{workbenchKey}/turns");
        var values = new RouteValueDictionary { ["project"] = "AGT", ["workbenchKey"] = "W 1" };

        Assert.Equal(
            "/api/v1/studio/orchestrator/sessions/workbench:AGT/W%201/turns",
            ConnectorProxy.ExpandTarget(operation, values));
    }

    [Fact]
    public async Task Host_origin_session_and_csrf_are_enforced()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var client = connector.Client;

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
        var csrf = await CsrfAsync(client);

        using (var missingCsrf = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, null))
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(missingCsrf)).StatusCode);

        using var accepted = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(accepted)).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:4011", HttpStatusCode.OK, null)]
    [InlineData("http://[::1]:4011", HttpStatusCode.OK, null)]
    [InlineData("http://127.0.0.1:4011", HttpStatusCode.Forbidden, ConnectorRequestPolicy.OriginRejected)]
    [InlineData("http://localhost:4012", HttpStatusCode.Forbidden, ConnectorRequestPolicy.OriginRejected)]
    [InlineData("https://localhost:4011", HttpStatusCode.Forbidden, ConnectorRequestPolicy.OriginRejected)]
    [InlineData("http://evil.example", HttpStatusCode.Forbidden, ConnectorRequestPolicy.OriginRejected)]
    [InlineData("null", HttpStatusCode.Forbidden, ConnectorRequestPolicy.OriginRejected)]
    [InlineData(null, HttpStatusCode.Forbidden, ConnectorRequestPolicy.OriginRequired)]
    public async Task Mutations_are_accepted_only_from_the_configured_studio_origins(
        string? origin,
        HttpStatusCode expected,
        string? expectedCode)
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(HttpMethod.Post, "/api/v1/projects", origin, csrf);
        using var response = await connector.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, await CodeAsync(response));
            Assert.Empty(transport.ProxiedRequests);
        }
    }

    [Fact]
    public async Task Configured_loopback_origin_replaces_the_default_allowlist()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(new ConnectorTestSetup(
            transport,
            new MutableCredentialSource("studio-secret"),
            Settings: new Dictionary<string, string?> { ["Connector:StudioOrigins:0"] = "http://127.0.0.1:4200" }));
        var csrf = await CsrfAsync(connector.Client, "http://127.0.0.1:4200");

        using (var configured = Mutation(HttpMethod.Post, "/api/v1/projects", "http://127.0.0.1:4200", csrf))
            Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(configured)).StatusCode);
        using (var defaultOrigin = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf))
            Assert.Equal(HttpStatusCode.Forbidden, (await connector.Client.SendAsync(defaultOrigin)).StatusCode);
    }

    [Fact]
    public async Task Replayed_csrf_tokens_are_rejected_across_sessions_logout_and_upstream_rotation()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var origin = ConnectorOptions.DefaultStudioOrigin;
        var tokenA = await CsrfAsync(connector.Client);
        using var browserB = connector.NewBrowser();
        using var sessionB = await SessionAsync(browserB);
        var cookieB = ReadCookie(sessionB, ConnectorSessionStore.SessionCookieName);
        var tokenB = ReadCookie(sessionB, ConnectorSessionStore.CsrfCookieName);

        // Session A's token presented by browser B, which holds its own session.
        using (var crossSession = Mutation(HttpMethod.Post, "/api/v1/projects", origin, tokenA))
        {
            using var response = await browserB.SendAsync(crossSession);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(ConnectorRequestPolicy.CsrfRejected, await CodeAsync(response));
        }
        // Header token that does not match the session's cookie token.
        using (var forgedHeader = Mutation(HttpMethod.Post, "/api/v1/projects", origin, new string('0', 64)))
            Assert.Equal(HttpStatusCode.Forbidden, (await browserB.SendAsync(forgedHeader)).StatusCode);

        // After logout, the same cookie and token no longer authorize anything.
        using (var logout = Mutation(HttpMethod.Delete, "/connector/session", origin, tokenB))
            Assert.Equal(HttpStatusCode.NoContent, (await browserB.SendAsync(logout)).StatusCode);
        using (var afterLogout = Mutation(HttpMethod.Post, "/api/v1/projects", origin, tokenB))
        {
            afterLogout.Headers.Add("Cookie", $"{ConnectorSessionStore.SessionCookieName}={cookieB}; {ConnectorSessionStore.CsrfCookieName}={tokenB}");
            using var raw = connector.NewCookielessBrowser();
            using var response = await raw.SendAsync(afterLogout);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(ConnectorRequestPolicy.SessionRequired, await CodeAsync(response));
        }

        // An upstream switch ends every session, so A's still-valid pair dies too.
        connector.Services.GetRequiredService<ConnectorSessionStore>().RotateAll();
        using (var afterRotation = Mutation(HttpMethod.Post, "/api/v1/projects", origin, tokenA))
            Assert.Equal(HttpStatusCode.Unauthorized, (await connector.Client.SendAsync(afterRotation)).StatusCode);

        Assert.Empty(transport.ProxiedRequests);
    }

    [Fact]
    public void Sessions_expire_after_their_absolute_lifetime()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T08:00:00Z"));
        var sessions = new ConnectorSessionStore(TimeSpan.FromHours(12), time);
        var issued = new DefaultHttpContext();
        sessions.Issue(issued.Response);
        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = string.Join("; ", issued.Response.Headers.SetCookie
            .Select(value => value!.Split(';', 2)[0]));

        Assert.True(sessions.ValidateSession(request.Request, out _));
        time.Advance(TimeSpan.FromHours(12));
        Assert.False(sessions.ValidateSession(request.Request, out _));
    }

    [Fact]
    public async Task First_same_origin_read_starts_a_session_without_a_bootstrap_call()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);

        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/v1/studio/board");
        read.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await connector.Client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csrf = ReadCookie(response, ConnectorSessionStore.CsrfCookieName);

        using var mutation = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf);
        Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(mutation)).StatusCode);

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, "/api/v1/studio/board");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var fresh = connector.NewBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.SendAsync(crossSite)).StatusCode);
    }

    [Fact]
    public async Task Hub_negotiate_needs_origin_and_session_but_no_csrf_header()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);

        using (var noSession = new HttpRequestMessage(HttpMethod.Post, "/hubs/v1/studio/negotiate?negotiateVersion=1"))
        {
            noSession.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
            Assert.Equal(HttpStatusCode.Unauthorized, (await connector.Client.SendAsync(noSession)).StatusCode);
        }

        await CsrfAsync(connector.Client);
        using (var foreign = new HttpRequestMessage(HttpMethod.Post, "/hubs/v1/studio/negotiate?negotiateVersion=1"))
        {
            foreign.Headers.Add("Origin", "http://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await connector.Client.SendAsync(foreign)).StatusCode);
        }
        using (var negotiate = new HttpRequestMessage(HttpMethod.Post, "/hubs/v1/studio/negotiate?negotiateVersion=1"))
        {
            negotiate.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
            Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(negotiate)).StatusCode);
        }
        // Any other hub POST (a long-polling send) is a mutation and needs CSRF.
        using (var send = new HttpRequestMessage(HttpMethod.Post, "/hubs/v1/studio?id=connection"))
        {
            send.Headers.Add("Origin", ConnectorOptions.DefaultStudioOrigin);
            send.Content = new StringContent("{}");
            Assert.Equal(HttpStatusCode.Forbidden, (await connector.Client.SendAsync(send)).StatusCode);
        }

        Assert.Equal("/hubs/v1/studio/negotiate", Assert.Single(transport.ProxiedRequests).Path);
    }

    [Fact]
    public async Task Proxy_strips_browser_identity_and_injects_the_studio_credential_and_protocol()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "browser-token");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add(TaskServerProtocol.HeaderName, "999");
        request.Headers.Add("X-Client-Id", "studio-window-1");

        Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(request)).StatusCode);
        var observed = Assert.Single(transport.ProxiedRequests);
        Assert.Equal("Bearer studio-secret", observed.Authorization);
        Assert.Equal(TaskServerProtocol.Current.ToString(), observed.Protocol);
        Assert.Equal("studio-window-1", observed.ClientId);
        Assert.False(observed.HasCookie);
        Assert.False(observed.HasForwardedFor);
        Assert.Equal("/api/v1/projects", observed.Path);
    }

    [Fact]
    public async Task Forwarded_requests_carry_the_negotiated_protocol_version()
    {
        var transport = new RecordingTransport
        {
            Attach = (request, _) => (HttpStatusCode.OK, RecordingTransport.MatchingServer(request) with { ApiProtocol = 1 }),
        };
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf);
        Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(request)).StatusCode);
        Assert.Equal("1", Assert.Single(transport.ProxiedRequests).Protocol);
    }

    [Fact]
    public async Task Core_attach_task_lifecycle_routes_forward_with_the_unscoped_project_token()
    {
        // The Studio sends a task-only lifecycle mutation on its versioned
        // route with the reserved unscoped-project token (AGT-2983); the
        // connector forwards that path to the Task Server unchanged.
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(
            HttpMethod.Post,
            $"/api/v1/projects/{ConnectorProxy.UnscopedProjectToken}/tasks/tsk_core-attach-demo/move",
            ConnectorOptions.DefaultStudioOrigin,
            csrf,
            new { targetState = "2-ready" });

        Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(request)).StatusCode);
        var observed = Assert.Single(transport.ProxiedRequests);
        Assert.Equal(
            $"/api/v1/projects/{ConnectorProxy.UnscopedProjectToken}/tasks/tsk_core-attach-demo/move",
            observed.Path);
    }

    [Theory]
    [InlineData("GET", "/api/v1/studio/board")]
    [InlineData("GET", "/api/v1/studio/auth/status")]
    [InlineData("GET", "/api/v1/projects/Agent%20Studio/tasks/AGT-1")]
    [InlineData("POST", "/api/v1/studio/orchestrator/sessions/workbench:PROJ/wb-1/turns")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/global")]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/global/refresh")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/project:Agent%20Studio")]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/project:Agent%20Studio/refresh")]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/task:Agent%20Studio/AGT-1")]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/task:Agent%20Studio/AGT-1/refresh")]
    [InlineData("POST", "/hubs/v1/studio/negotiate")]
    public async Task Versioned_core_attach_paths_forward_to_the_task_server_unchanged(string method, string path)
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(new HttpMethod(method), path, ConnectorOptions.DefaultStudioOrigin, csrf, new { prompt = "test" });
        if (method == "GET") request.Content = null;

        Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(request)).StatusCode);
        var observed = Assert.Single(transport.ProxiedRequests);
        Assert.Equal(new Uri("http://host" + path).AbsolutePath, observed.Path);
    }

    [Theory]
    [InlineData("GET", "/api/orchestrator/context/workbench:PROJ/AGT-W1")]
    [InlineData("POST", "/api/orchestrator/context/workbench:PROJ/AGT-W1/refresh")]
    public async Task Dossier_context_digest_is_answered_by_the_local_dev_seat_not_the_task_server(string method, string path)
    {
        // The Task Server has no Dossier descriptors; the connector serves the
        // Dossier digest from the local OrchestratorApi handler (AGT-2983).
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(new HttpMethod(method), path, ConnectorOptions.DefaultStudioOrigin, csrf);
        if (method == "GET") request.Content = null;

        using var response = await connector.Client.SendAsync(request);
        Assert.Empty(transport.ProxiedRequests);
        // The local handler answered: an unknown Dossier, not an unmapped route.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown Dossier 'AGT-W1'", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rotated_credential_is_used_without_a_restart()
    {
        var credentials = new MutableCredentialSource("studio-secret-v1");
        var revoked = false;
        var transport = new RecordingTransport
        {
            ForwardStatus = observed => revoked && observed.Authorization == "Bearer studio-secret-v1"
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.OK,
        };
        await using var connector = ConnectorUnderTest.Boot(new ConnectorTestSetup(
            transport,
            credentials,
            Settings: new Dictionary<string, string?> { ["Connector:CredentialRefreshSeconds"] = "300" }));
        var csrf = await CsrfAsync(connector.Client);

        using (var before = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf))
            Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(before)).StatusCode);

        // Overlap-and-prove rotation: the operator stores the new credential,
        // then the server revokes the old one. The refresh interval (300 s)
        // has not elapsed, so only the upstream 401 makes the connector re-read.
        credentials.Rotate("studio-secret-v2");
        revoked = true;
        using (var stale = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf))
            Assert.Equal(HttpStatusCode.Unauthorized, (await connector.Client.SendAsync(stale)).StatusCode);
        using (var after = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf))
            Assert.Equal(HttpStatusCode.OK, (await connector.Client.SendAsync(after)).StatusCode);

        Assert.Equal(
            ["Bearer studio-secret-v1", "Bearer studio-secret-v1", "Bearer studio-secret-v2"],
            transport.ProxiedRequests.Select(request => request.Authorization));
        // The new credential was proven by a fresh attach before it was used.
        Assert.Equal("Bearer studio-secret-v2", transport.AttachAuthorizations[^1]);
    }

    [Theory]
    [InlineData(3, 4, 1, 1, ConnectorAttachFailureCodes.ProtocolIncompatible)]
    [InlineData(1, 2, 2, 3, ConnectorAttachFailureCodes.HubProtocolIncompatible)]
    public async Task Protocol_mismatch_refuses_the_attach_with_an_operator_readable_reason(
        int serverMinimumApi,
        int serverMaximumApi,
        int serverMinimumHub,
        int serverMaximumHub,
        string expectedReason)
    {
        var transport = new RecordingTransport
        {
            Attach = (request, _) =>
            {
                var server = RecordingTransport.ServerRange(serverMinimumApi, serverMaximumApi, serverMinimumHub, serverMaximumHub);
                var api = ProtocolNegotiation.HighestCommon(request.MinimumApiProtocol, request.MaximumApiProtocol, serverMinimumApi, serverMaximumApi);
                var code = api is null ? ProtocolAttachCodes.ApiProtocolIncompatible : ProtocolAttachCodes.HubProtocolIncompatible;
                return (HttpStatusCode.UpgradeRequired, new ProtocolAttachResponse(
                    false, code, server, null, null, null, "studio-robert-windows",
                    $"Task Server test-server speaks {serverMinimumApi}-{serverMaximumApi} and hub {serverMinimumHub}-{serverMaximumHub}."));
            },
        };
        await using var connector = ConnectorUnderTest.Boot(transport);
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf);
        using var response = await connector.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ConnectorProxy.AttachRefusedCode, body.GetProperty("code").GetString());
        Assert.Equal(expectedReason, body.GetProperty("reason").GetString());
        Assert.Contains("Task Server test-server speaks", body.GetProperty("message").GetString());
        Assert.Empty(transport.ProxiedRequests);

        using var readiness = await connector.Client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        var ready = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedReason, ready.GetProperty("failureCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(ready.GetProperty("failureReason").GetString()));
    }

    [Fact]
    public async Task Absent_credential_refuses_the_attach_and_never_reaches_the_upstream()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(new ConnectorTestSetup(transport, new MutableCredentialSource(null)));
        var csrf = await CsrfAsync(connector.Client);

        using var request = Mutation(HttpMethod.Post, "/api/v1/projects", ConnectorOptions.DefaultStudioOrigin, csrf);
        using var response = await connector.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ConnectorCredentialFailureCodes.Unavailable, body.GetProperty("reason").GetString());
        Assert.Empty(transport.ProxiedRequests);
        Assert.Empty(transport.AttachAuthorizations);
    }

    [Fact]
    public async Task Upstream_switch_is_validated_atomic_and_rotates_sessions()
    {
        var options = TestOptions();
        var sessions = new ConnectorSessionStore();
        var transport = new RecordingTransport();
        var credentials = new ConnectorCredentialProvider(options, new MutableCredentialSource("studio-secret"), TimeProvider.System);
        var manager = new ConnectorUpstreamManager(
            options,
            credentials,
            transport,
            sessions,
            ConnectorProtocolRange.Supported,
            ConnectorRouteInventory.Load(),
            TimeProvider.System);
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
    public async Task Rotation_between_capture_and_attach_cannot_skip_the_new_credentials_handshake()
    {
        var options = TestOptions();
        var source = new MutableCredentialSource("studio-secret");
        var credentials = new ConnectorCredentialProvider(options, source, TimeProvider.System);
        var transport = new RecordingTransport
        {
            // The Task Server no longer accepts the rotated value: only a handshake with it can reveal that.
            Attach = (request, authorization) => authorization == "Bearer rotated-secret"
                ? (HttpStatusCode.Unauthorized, new { error = "unauthorized" })
                : (HttpStatusCode.OK, RecordingTransport.MatchingServer(request)),
        };
        var manager = new ConnectorUpstreamManager(
            options,
            credentials,
            transport,
            new ConnectorSessionStore(),
            ConnectorProtocolRange.Supported,
            ConnectorRouteInventory.Load(),
            TimeProvider.System);

        // Request A captures the old credential; another request's refresh rotates it before A attaches.
        var stale = manager.Capture();
        source.Rotate("rotated-secret");
        credentials.Invalidate();
        Assert.Equal("rotated-secret", credentials.Current().Credential!.Bearer);

        var staleAttach = await manager.EnsureAttachedAsync(stale, default);
        Assert.True(staleAttach.Ready);
        Assert.Equal(["Bearer studio-secret"], transport.AttachAuthorizations);

        // The old credential's handshake must not vouch for the rotated credential.
        var rotated = manager.Capture();
        Assert.Equal("rotated-secret", rotated.Credential!.Bearer);
        Assert.NotEqual(stale.CredentialRevision, rotated.CredentialRevision);
        var rotatedAttach = await manager.EnsureAttachedAsync(rotated, default);
        Assert.False(rotatedAttach.Ready);
        Assert.Equal(ConnectorAttachFailureCodes.CredentialRejected, rotatedAttach.FailureCode);
        Assert.Equal(["Bearer studio-secret", "Bearer rotated-secret"], transport.AttachAuthorizations);

        // Once the Task Server accepts the rotated value, its own proven handshake is reused.
        transport.Attach = (request, _) => (HttpStatusCode.OK, RecordingTransport.MatchingServer(request));
        manager.InvalidateAttachment();
        Assert.True((await manager.EnsureAttachedAsync(manager.Capture(), default)).Ready);
        Assert.True((await manager.EnsureAttachedAsync(manager.Capture(), default)).Ready);
        Assert.Equal(
            ["Bearer studio-secret", "Bearer rotated-secret", "Bearer rotated-secret"],
            transport.AttachAuthorizations);
    }

    [Fact]
    public void Credential_state_pairs_each_value_with_the_revision_it_was_read_under()
    {
        var source = new MutableCredentialSource("v1");
        var credentials = new ConnectorCredentialProvider(TestOptions(), source, TimeProvider.System);

        var first = credentials.CurrentState();
        source.Rotate("v2");
        credentials.Invalidate();
        var second = credentials.CurrentState();
        credentials.Invalidate();
        var unchanged = credentials.CurrentState();

        Assert.Equal(("v1", 1L), (first.Result.Credential!.Bearer, first.Revision));
        Assert.Equal(("v2", 2L), (second.Result.Credential!.Bearer, second.Revision));
        Assert.Equal(second.Revision, unchanged.Revision);
    }

    [Fact]
    public async Task Health_separates_liveness_from_redacted_upstream_readiness()
    {
        var transport = new RecordingTransport();
        await using var connector = ConnectorUnderTest.Boot(transport);

        using var liveness = await connector.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);

        using var readiness = await connector.Client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        var json = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", json.GetProperty("status").GetString());
        Assert.Equal("remote-task-server", json.GetProperty("upstream").GetString());
        Assert.Equal(TaskServerHubProtocol.Current, json.GetProperty("hubProtocol").GetInt32());
        Assert.Equal(ConnectorRouteInventory.Load().RouteChecksum,
            json.GetProperty("routeChecksum").GetString());
        var body = await readiness.Content.ReadAsStringAsync();
        Assert.DoesNotContain("studio-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("task-server.invalid", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectorCredentialSource.DockerSecretPath, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS", "http://127.0.0.1:5031")]
    [InlineData("URLS", "http://127.0.0.1:5031")]
    [InlineData("Kestrel__Endpoints__Http__Url", "http://127.0.0.1:5031")]
    public async Task Host_boots_while_the_launching_process_owns_a_listener(string variable, string value)
    {
        // The gate condition: the Studio backend starts `dotnet test`, so the
        // test process inherits the listener configuration of a host that is
        // already serving on the connector's port. Every host-booting test in
        // this class then failed inside the gate with "The connector may listen
        // only on http://[::1]:5031" while passing from an operator shell
        // (AGT-2840). The fixture owns the listener configuration instead of
        // inheriting it, so the suite no longer depends on its launcher.
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var transport = new RecordingTransport();
            await using var connector = ConnectorUnderTest.Boot(transport);

            using var liveness = await connector.Client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Credential_supplied_through_an_environment_variable_stops_the_boot()
    {
        const string variable = "Connector__BearerToken";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "env-studio-secret");
        try
        {
            var exception = Assert.ThrowsAny<Exception>(() =>
                ConnectorUnderTest.Boot(new RecordingTransport()));
            var message = Flatten(exception);
            Assert.Contains("Connector:BearerToken", message, StringComparison.Ordinal);
            Assert.DoesNotContain("env-studio-secret", message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
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

    /// <summary>
    /// Replaces each <c>{name}</c> in an inventory path with a value derived
    /// from its position, so two aliases of one physical route (such as
    /// <c>{id}</c> and <c>{projectId}</c>) produce the same request.
    /// </summary>
    private static string SamplePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segments.Select((segment, index) => SampleSegment(segment, index)));
    }

    private static string SampleSegment(string segment, int index)
    {
        if (!segment.Contains('{')) return segment;
        if (!segment.StartsWith('{') || !segment.EndsWith('}'))
            return System.Text.RegularExpressions.Regex.Replace(segment, @"\{[^{}/]+\}", $"p{index}");
        return segment.Contains('*') ? $"p{index}/deep" : $"p{index}";
    }

    /// <summary>
    /// The approved upstream path for <see cref="SamplePath"/>: a target
    /// parameter takes the value of the same-named source parameter, else the
    /// source parameter at the same position, else the reserved unscoped
    /// project token for a project id the legacy route does not carry.
    /// </summary>
    private static string ExpectedTarget(ConnectorRouteOperation operation)
    {
        var source = operation.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sourceIndexByName = source
            .Select((segment, index) => (segment, index))
            .Where(item => item.segment.StartsWith('{'))
            .ToDictionary(item => ParameterName(item.segment), item => item.index);
        var embeddedIndexByName = source
            .Select((segment, index) => (segment, index))
            .Where(item => item.segment.Contains('{') && !item.segment.StartsWith('{'))
            .SelectMany(item => System.Text.RegularExpressions.Regex.Matches(item.segment, @"\{([^{}/]+)\}")
                .Select(match => (name: match.Groups[1].Value, item.index)))
            .ToDictionary(item => item.name, item => item.index);
        var target = operation.TargetRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var expanded = target.Select((segment, index) =>
        {
            if (!segment.Contains('{')) return segment;
            if (!segment.StartsWith('{'))
            {
                return System.Text.RegularExpressions.Regex.Replace(segment, @"\{([^{}/]+)\}", match =>
                    $"p{embeddedIndexByName[match.Groups[1].Value]}");
            }
            var name = ParameterName(segment);
            if (sourceIndexByName.TryGetValue(name, out var sourceIndex))
                return SampleSegment(source[sourceIndex], sourceIndex);
            if (index < source.Length && source[index].StartsWith('{'))
                return SampleSegment(source[index], index);
            Assert.Equal("projectId", name);
            return ConnectorProxy.UnscopedProjectToken;
        });
        return "/" + string.Join('/', expanded);
    }

    private static string ParameterName(string segment)
    {
        var value = segment[1..^1].Trim('*');
        var constraint = value.IndexOf(':');
        return constraint < 0 ? value : value[..constraint];
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        return string.Join(" | ", messages);
    }

    internal static ConnectorOptions TestOptions() => new(
        ConnectorOptions.NativeMode,
        ConnectorOptions.DefaultAuthority,
        ConnectorOptions.DefaultStudioOrigins,
        "remote",
        new Uri("https://task-server.invalid/"),
        null,
        1,
        "remote-task-server",
        ConnectorOptions.DefaultCredentialTarget,
        null,
        ConnectorOptions.DefaultCredentialRefreshInterval,
        ConnectorOptions.DefaultSessionLifetime);
}
