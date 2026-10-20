using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Management;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3230: response headers per route class, host-only session cookies,
/// same-origin proof for browser changes and the forwarded client address,
/// all through the real networked pipeline.
/// </summary>
public sealed class NetworkedEdgeHardeningTests : IDisposable
{
    private const string Studio = "https://studio.test";
    private const string Password = "correct horse battery staple!";
    private const string ProjectName = "edge-hardening";
    private const string TaskKey = "AGT-EDGE-HARDENING";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-edge-hardening-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Api_shell_rejection_and_health_responses_carry_the_baseline_headers()
    {
        using var factory = BuildFactory();
        using var client = NewClient(factory);

        foreach (var (method, path, status) in new[]
                 {
                     (HttpMethod.Get, "/api/auth/status", HttpStatusCode.OK),
                     (HttpMethod.Get, "/api/tasks", HttpStatusCode.Unauthorized),
                     (HttpMethod.Post, "/hubs/jobs/negotiate", HttpStatusCode.Unauthorized),
                     (HttpMethod.Get, "/healthz", HttpStatusCode.OK),
                 })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, path));
            Assert.Equal(status, response.StatusCode);
            AssertBaseline(response, StudioResponseHeaderPolicy.ApiContentSecurityPolicy);
        }
    }

    [Fact]
    public async Task Unmatched_routes_and_exception_handler_errors_carry_the_baseline_headers()
    {
        using (var factory = BuildFactory())
        {
            var owner = factory.Services.GetRequiredService<AccessSecurityStore>()
                .Bootstrap(new BootstrapRequest("first.owner", Password, null));
            using var client = NewClient(factory);
            foreach (var path in new[] { "/api/no-such-route", "/no-such-route/below" })
            {
                using var missing = await SendAsync(client, HttpMethod.Get, path, owner.SessionToken);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                AssertBaseline(missing, StudioResponseHeaderPolicy.ApiContentSecurityPolicy);
            }
        }

        // An unreadable security state throws inside the endpoint, so the
        // exception handler writes the 500.
        var broken = Path.Combine(_workspace, "broken");
        Directory.CreateDirectory(Path.Combine(broken, ".security"));
        File.WriteAllText(Path.Combine(broken, ".security", "access-state.json"), "{ not json");
        using (var factory = BuildFactory(repository: broken))
        {
            using var client = NewClient(factory);
            using var failed = await client.GetAsync("/api/auth/status");
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Contains("An unexpected server error occurred.", await failed.Content.ReadAsStringAsync());
            AssertBaseline(failed, StudioResponseHeaderPolicy.ApiContentSecurityPolicy);
        }

        // The standalone Task Server short-circuit answers legacy /api routes
        // before routing; its 404 carries the headers as well.
        using (var proxied = BuildFactory(repository: Path.Combine(_workspace, "proxied"),
                   settings: new Dictionary<string, string?> { ["TaskServer:BaseUrl"] = "http://127.0.0.1:1" }))
        {
            using var client = NewClient(proxied);
            using var legacy = await client.GetAsync("/api/tasks");
            Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
            Assert.Contains("route-not-on-task-server", await legacy.Content.ReadAsStringAsync());
            AssertBaseline(legacy, StudioResponseHeaderPolicy.ApiContentSecurityPolicy);
        }
    }

    [Fact]
    public async Task Recovery_console_keeps_its_own_policy_that_runs_its_inline_script()
    {
        using var factory = BuildFactory();
        using var client = NewClient(factory);

        using var recovery = await client.GetAsync("/recovery");

        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        Assert.Contains("Task Server bootstrap and recovery", await recovery.Content.ReadAsStringAsync());
        var policy = Header(recovery, "Content-Security-Policy");
        Assert.Equal(RecoveryConsole.ContentSecurityPolicy, policy);
        Assert.Contains("script-src 'unsafe-inline'", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.Contains("frame-ancestors 'self'", policy);
        Assert.DoesNotContain("sandbox", policy);
        Assert.Equal("SAMEORIGIN", Header(recovery, "X-Frame-Options"));
    }

    [Fact]
    public async Task Preview_proxy_and_task_result_html_keep_their_own_sandbox_policy()
    {
        var resultsPath = Path.Combine(_workspace, "projects", ProjectName, "3-progress", TaskKey, "results");
        Directory.CreateDirectory(resultsPath);
        File.WriteAllText(Path.Combine(resultsPath, "report.html"), "<html><body>report</body></html>");
        File.WriteAllText(Path.Combine(resultsPath, "..", "x.html"), "<html><body><script>alert(document.cookie)</script></body></html>");
        Directory.CreateDirectory(Path.Combine(_workspace, "projects", ProjectName, "docs"));
        File.WriteAllText(Path.Combine(_workspace, "projects", ProjectName, "docs", "diagram.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        File.WriteAllText(Path.Combine(resultsPath, "..", "task.json"), JsonSerializer.Serialize(new
        {
            id = TaskKey, title = "Edge hardening", state = "3-progress", order = 1, agent = "codex", kind = "task",
        }));
        Directory.CreateDirectory(Path.Combine(_workspace, "projects", ProjectName, "2-ready"));

        using var factory = BuildFactory(previewUpstream: true, projectPath: Path.Combine(_workspace, "projects", ProjectName));
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var previewRoot = Path.Combine(_workspace, "preview-site");
        Directory.CreateDirectory(previewRoot);
        var project = registry.EnsureProjectForStorage(previewRoot, "Preview", "ws-1", new FakeTimeProvider());
        var url = registry.AddUrl(project.Id, "Site", "http://127.0.0.1:4200/app").Urls.Single();
        using var client = NewClient(factory);

        using var preview = await SendAsync(client, HttpMethod.Get,
            $"/api/projects/{project.Id}/urls/{url.Id}/preview/", owner.SessionToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("preview body", await preview.Content.ReadAsStringAsync());
        Assert.Equal("sandbox allow-scripts; form-action 'none'", Header(preview, "Content-Security-Policy"));
        Assert.False(preview.Headers.Contains("X-Frame-Options"));
        Assert.Equal("nosniff", Header(preview, "X-Content-Type-Options"));
        Assert.Equal(StudioResponseHeaderPolicy.StrictTransportSecurity, Header(preview, "Strict-Transport-Security"));

        using var report = await SendAsync(client, HttpMethod.Get,
            $"/api/tasks/{TaskKey}/results/report.html", owner.SessionToken);
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.StartsWith("sandbox allow-scripts; default-src 'none'", Header(report, "Content-Security-Policy"));
        Assert.False(report.Headers.Contains("X-Frame-Options"));
        Assert.Equal(StudioResponseHeaderPolicy.PermissionsPolicy, Header(report, "Permissions-Policy"));

        // Wiki SVG has no policy of its own: it gets the sandboxed document
        // policy, never one that runs script.
        using (var svg = await SendAsync(client, HttpMethod.Get,
                   $"/api/projects/{ProjectName}/wiki/assets/diagram.svg", owner.SessionToken))
        {
            Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
            Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
            AssertSandboxedDocument(svg);
        }

        // The networked boundary keeps task files Runner-only (prompt.md under
        // a live lease), so the session gets no HTML at all from this route.
        using var taskFile = await SendAsync(client, HttpMethod.Get, $"/api/tasks/{TaskKey}/files/x.html", owner.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, taskFile.StatusCode);
        Assert.DoesNotContain("<script>", await taskFile.Content.ReadAsStringAsync());
        Assert.Equal(StudioResponseHeaderPolicy.ApiContentSecurityPolicy, Header(taskFile, "Content-Security-Policy"));
    }

    [Fact]
    public async Task Agent_written_task_html_served_by_the_files_route_is_sandboxed_without_script()
    {
        // The files route answers with the stored text and its media type and
        // no policy of its own; the header middleware decides the policy when
        // the response starts.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseStudioResponseHeaders();
        app.MapGet("/api/tasks/{jobId}/files/{**path}", (string jobId, string path) =>
            Results.Text("<html><body><script>alert(document.cookie)</script></body></html>", "text/html", System.Text.Encoding.UTF8));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri(Studio);

        using var html = await client.GetAsync($"/api/tasks/{TaskKey}/files/x.html");

        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
        AssertSandboxedDocument(html);
        Assert.Equal(StudioResponseHeaderPolicy.StrictTransportSecurity, Header(html, "Strict-Transport-Security"));
        await app.StopAsync();
    }

    private static void AssertSandboxedDocument(HttpResponseMessage response)
    {
        var policy = Header(response, "Content-Security-Policy");
        Assert.Equal(StudioResponseHeaderPolicy.DocumentContentSecurityPolicy, policy);
        Assert.StartsWith("sandbox;", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.DoesNotContain("script-src", policy);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Session_and_csrf_cookies_are_host_only_secure_and_strict()
    {
        using var factory = BuildFactory();
        using var client = NewClient(factory);

        using var bootstrap = await SendAsync(client, HttpMethod.Post, "/api/auth/bootstrap", origin: Studio,
            body: new { username = "first.owner", password = Password, displayName = "First Owner", bootstrapCode = "website-setup-code" });
        bootstrap.EnsureSuccessStatusCode();
        var cookies = bootstrap.Headers.GetValues("Set-Cookie").ToList();
        var session = Assert.Single(cookies, value => value.StartsWith(AccessSecurityStore.SessionCookieName + "=", StringComparison.Ordinal));
        var csrf = Assert.Single(cookies, value => value.StartsWith("__Host-agentstudio-csrf=", StringComparison.Ordinal));
        AssertHostOnly(session, httpOnly: true);
        AssertHostOnly(csrf, httpOnly: false);

        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        var token = session[(AccessSecurityStore.SessionCookieName.Length + 1)..].Split(';')[0];
        using var logout = await SendAsync(client, HttpMethod.Post, "/api/auth/logout", token, Studio, csrf: auth!.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        foreach (var deletion in logout.Headers.GetValues("Set-Cookie"))
            Assert.DoesNotContain("domain=", deletion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Browser_changes_without_same_origin_proof_are_rejected()
    {
        using var factory = BuildFactory();
        using var client = NewClient(factory);
        var owner = new { username = "first.owner", password = Password, displayName = "First Owner", bootstrapCode = "website-setup-code" };

        Assert.Equal("origin-mismatch", await RejectionAsync(SendAsync(client, HttpMethod.Post, "/api/auth/bootstrap", origin: "https://other.studio.test", body: owner)));
        Assert.Equal("cross-site-request", await RejectionAsync(SendAsync(client, HttpMethod.Post, "/api/auth/bootstrap", fetchSite: "same-site", body: owner)));
        Assert.Equal("origin-required", await RejectionAsync(SendAsync(client, HttpMethod.Post, "/api/auth/bootstrap", body: owner)));
        using var bootstrap = await SendAsync(client, HttpMethod.Post, "/api/auth/bootstrap", fetchSite: "same-origin", body: owner);
        bootstrap.EnsureSuccessStatusCode();
        var login = factory.Services.GetRequiredService<AccessSecurityStore>().Login(owner.username, Password, "edge-test");
        var enrollment = new { name = "runner-01" };

        Assert.Equal("origin-mismatch", await RejectionAsync(SendAsync(client, HttpMethod.Post, "/api/auth/runner-enrollments", login.SessionToken, "https://other.studio.test", csrf: login.CsrfToken, body: enrollment)));
        Assert.Equal("cross-site-request", await RejectionAsync(SendAsync(client, HttpMethod.Post, "/api/auth/runner-enrollments", login.SessionToken, fetchSite: "cross-site", csrf: login.CsrfToken, body: enrollment)));
        Assert.Equal("origin-required", await RejectionAsync(SendAsync(client, HttpMethod.Post, "/api/auth/runner-enrollments", login.SessionToken, csrf: login.CsrfToken, body: enrollment)));
        using var read = await SendAsync(client, HttpMethod.Get, "/api/auth/session", login.SessionToken, "https://other.studio.test", fetchSite: "cross-site");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var created = await SendAsync(client, HttpMethod.Post, "/api/auth/runner-enrollments", login.SessionToken, Studio, csrf: login.CsrfToken, body: enrollment);
        created.EnsureSuccessStatusCode();
        var code = (await created.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>())!.EnrollmentCode;

        // Runner enrollment and Runner calls carry no browser credential, so
        // they stay outside the Origin rule; CSRF still guards the session.
        using var enroll = await SendAsync(client, HttpMethod.Post, "/api/auth/runner-enroll", body: new { code });
        enroll.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Websocket_upgrade_to_the_job_hub_needs_the_studio_origin()
    {
        using var factory = BuildFactory();
        var owner = factory.Services.GetRequiredService<AccessSecurityStore>()
            .Bootstrap(new BootstrapRequest("first.owner", Password, null));
        using var client = NewClient(factory);

        Assert.Equal("origin-mismatch", await RejectionAsync(HubUpgradeAsync(client, owner.SessionToken, "https://other.studio.test")));
        Assert.Equal("cross-site-request", await RejectionAsync(HubUpgradeAsync(client, owner.SessionToken, fetchSite: "same-site")));
        Assert.Equal("opaque-origin-denied", await RejectionAsync(HubUpgradeAsync(client, owner.SessionToken, "null")));

        foreach (var (origin, fetchSite) in new[] { (Studio, (string?)null), (null, "same-origin"), (null, null) })
        {
            using var admitted = await HubUpgradeAsync(client, owner.SessionToken, origin, fetchSite);
            Assert.NotEqual(HttpStatusCode.Forbidden, admitted.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, admitted.StatusCode);
        }

        // The same check holds through a real WebSocket handshake.
        var sockets = factory.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request =>
        {
            request.Scheme = "https";
            request.Host = new HostString("studio.test");
            request.Headers.Cookie = $"{AccessSecurityStore.SessionCookieName}={owner.SessionToken}";
            request.Headers.Origin = "https://other.studio.test";
        };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => sockets.ConnectAsync(new Uri("wss://studio.test/hubs/jobs"), CancellationToken.None));
    }

    [Fact]
    public async Task Forwarded_loopback_does_not_skip_the_installer_code()
    {
        using var factory = BuildFactory(remoteIp: "172.30.83.3");
        using var client = NewClient(factory);

        Assert.True((await StatusAsync(client, "127.0.0.1")).BootstrapCodeRequired);
        using var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/auth/bootstrap")
        {
            Content = JsonContent.Create(new { username = "first.owner", password = Password, displayName = "First Owner" }),
        };
        bootstrap.Headers.Add("X-Forwarded-For", "127.0.0.1");
        bootstrap.Headers.Add("Origin", Studio);
        using var response = await client.SendAsync(bootstrap);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("bootstrap-code-required",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.True(factory.Services.GetRequiredService<AccessSecurityStore>().BootstrapRequired);
    }

    [Fact]
    public async Task Trusted_frontend_forwards_the_client_address_and_untrusted_peers_cannot()
    {
        using (var trusted = BuildFactory(remoteIp: "172.30.83.3"))
        {
            using var client = NewClient(trusted);
            Assert.True((await StatusAsync(client, "198.51.100.7")).BootstrapCodeRequired);
            trusted.Services.GetRequiredService<AccessSecurityStore>().Bootstrap(new BootstrapRequest("first.owner", Password, null));

            for (var attempt = 0; attempt < 5; attempt++)
                Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "198.51.100.7", "wrong password phrase"));
            Assert.Equal((HttpStatusCode)429, await LoginAsync(client, "198.51.100.7", Password));
            Assert.Equal(HttpStatusCode.OK, await LoginAsync(client, "198.51.100.8", Password));
        }

        using (var rotating = BuildFactory(remoteIp: "172.30.83.3", repository: Path.Combine(_workspace, "rotating")))
        {
            using var client = NewClient(rotating);
            rotating.Services.GetRequiredService<AccessSecurityStore>().Bootstrap(new BootstrapRequest("first.owner", Password, null));

            // Four failures per address stay under the per-client limit, but
            // the account budget of 20 failures spans every address.
            for (var attempt = 0; attempt < 20; attempt++)
                Assert.Equal(HttpStatusCode.Unauthorized,
                    await LoginAsync(client, $"198.51.100.{attempt / 4 + 30}", "wrong password phrase"));
            Assert.Equal((HttpStatusCode)429, await LoginAsync(client, "198.51.100.99", Password));
            Assert.Equal((HttpStatusCode)429, await LoginAsync(client, "2001:db8:7::1", Password));

            _clock.Advance(TimeSpan.FromMinutes(15));
            Assert.Equal(HttpStatusCode.OK, await LoginAsync(client, "198.51.100.99", Password));
        }

        using (var ipv6 = BuildFactory(remoteIp: "172.30.83.3", repository: Path.Combine(_workspace, "ipv6")))
        {
            using var client = NewClient(ipv6);
            ipv6.Services.GetRequiredService<AccessSecurityStore>().Bootstrap(new BootstrapRequest("first.owner", Password, null));

            // One IPv6 host owns its whole /64; rotating within it shares one key.
            for (var attempt = 0; attempt < 5; attempt++)
                Assert.Equal(HttpStatusCode.Unauthorized,
                    await LoginAsync(client, $"2001:db8:1:2::{attempt + 1:x}", "wrong password phrase"));
            Assert.Equal((HttpStatusCode)429, await LoginAsync(client, "2001:db8:1:2:ffff::9", Password));
            Assert.Equal(HttpStatusCode.OK, await LoginAsync(client, "2001:db8:1:3::1", Password));
        }

        using var untrusted = BuildFactory(remoteIp: "203.0.113.15", repository: Path.Combine(_workspace, "untrusted"));
        using var spoofing = NewClient(untrusted);
        Assert.True((await StatusAsync(spoofing, "127.0.0.1")).BootstrapCodeRequired);
        untrusted.Services.GetRequiredService<AccessSecurityStore>().Bootstrap(new BootstrapRequest("first.owner", Password, null));
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(spoofing, $"198.51.100.{attempt + 20}", "wrong password phrase"));
        Assert.Equal((HttpStatusCode)429, await LoginAsync(spoofing, "198.51.100.99", Password));
    }

    private static async Task<AuthStatusResponse> StatusAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!;
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string forwardedFor, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "first.owner", password }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("Origin", Studio);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static Task<HttpResponseMessage> HubUpgradeAsync(
        HttpClient client, string session, string? origin = null, string? fetchSite = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/hubs/jobs");
        request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        return client.SendAsync(request);
    }

    private static void AssertBaseline(HttpResponseMessage response, string contentSecurityPolicy)
    {
        Assert.Equal(contentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal(StudioResponseHeaderPolicy.PermissionsPolicy, Header(response, "Permissions-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
        Assert.Equal(StudioResponseHeaderPolicy.StrictTransportSecurity, Header(response, "Strict-Transport-Security"));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? session = null, string? origin = null,
        string? fetchSite = null, string? csrf = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (session is not null) request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<string?> RejectionAsync(Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("error").GetString();
    }

    private static void AssertHostOnly(string setCookie, bool httpOnly)
    {
        var attributes = setCookie.Split(';', StringSplitOptions.TrimEntries).Skip(1)
            .Select(attribute => attribute.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain", StringComparison.Ordinal));
        Assert.Contains("path=/", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Equal(httpOnly, attributes.Contains("httponly"));
    }

    private static string Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : string.Empty;

    private static HttpClient NewClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(Studio), HandleCookies = false });

    private WebApplicationFactory<Program> BuildFactory(
        string? remoteIp = null, bool previewUpstream = false, string? projectPath = null, string? repository = null,
        IReadOnlyDictionary<string, string?>? settings = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock));
            if (remoteIp is not null)
                builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(remoteIp))));
            if (previewUpstream)
                builder.ConfigureTestServices(services => services.AddHttpClient("project-url-preview")
                    .ConfigurePrimaryHttpMessageHandler(() => new PreviewUpstream()));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = repository ?? _workspace,
                ["Security:Profile"] = "networked",
                ["Security:BootstrapCode"] = "website-setup-code",
                ["Security:TrustedProxyAddresses"] = "172.30.83.3",
                ["AllowedHosts"] = "studio.test",
                ["WatchPaths:0:Name"] = projectPath is null ? null : ProjectName,
                ["WatchPaths:0:Path"] = projectPath,
                ["WatchPaths:0:RootPath"] = projectPath,
                ["WatchPaths:0:RepositoryPath"] = projectPath,
            }.Concat(settings ?? new Dictionary<string, string?>())));
        });

    private sealed class RemoteIpStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use((context, proceed) =>
                {
                    context.Connection.RemoteIpAddress = address;
                    return proceed();
                });
                next(app);
            };
    }

    private sealed class PreviewUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><head></head><body>preview body</body></html>"),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            response.Headers.TryAddWithoutValidation("X-Frame-Options", "DENY");
            return Task.FromResult(response);
        }
    }

    public void Dispose() => TestTempRoot.TryDelete(_workspace);
}
