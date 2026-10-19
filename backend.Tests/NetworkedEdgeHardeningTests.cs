using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
            Assert.Equal(StudioResponseHeaderPolicy.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
            Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
            Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
            Assert.Equal(StudioResponseHeaderPolicy.PermissionsPolicy, Header(response, "Permissions-Policy"));
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
            Assert.Equal(StudioResponseHeaderPolicy.StrictTransportSecurity, Header(response, "Strict-Transport-Security"));
        }
    }

    [Fact]
    public async Task Preview_proxy_and_task_result_html_keep_their_own_sandbox_policy()
    {
        var resultsPath = Path.Combine(_workspace, "projects", ProjectName, "3-progress", TaskKey, "results");
        Directory.CreateDirectory(resultsPath);
        File.WriteAllText(Path.Combine(resultsPath, "report.html"), "<html><body>report</body></html>");
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
        string? remoteIp = null, bool previewUpstream = false, string? projectPath = null, string? repository = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
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
            }));
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
