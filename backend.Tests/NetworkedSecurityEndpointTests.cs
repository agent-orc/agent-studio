extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using RClient = Runner::AgentRunner.TaskServerClient;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;

namespace AgentStudio.Tests;

public sealed class NetworkedSecurityEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-networked-endpoints-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Real_endpoints_bootstrap_session_csrf_and_runner_enrollment_fail_closed()
    {
        using var factory = BuildFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tasks")).StatusCode);
        var anonymousManagement = await anonymous.GetAsync("/api/v1/management/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousManagement.StatusCode);
        var anonymousManagementBody = await anonymousManagement.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("authentication-required", anonymousManagementBody.GetProperty("error").GetString());
        Assert.Equal("/api/auth/login", anonymousManagementBody.GetProperty("loginUrl").GetString());
        Assert.Contains("Sign in", anonymousManagementBody.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/clients/register", new { displayName = "open-runner", kind = "service" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/jobs/negotiate", null)).StatusCode);

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        var bootstrap = await browser.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "first.owner",
            password = "correct horse battery staple!",
            displayName = "First Owner",
            bootstrapCode = "website-setup-code"
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.NotNull(auth?.CsrfToken);
        Assert.Contains(bootstrap.Headers.GetValues("Set-Cookie"), value =>
            value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)
            && value.Contains("Secure", StringComparison.OrdinalIgnoreCase)
            && value.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase));
        (await browser.GetAsync("/api/v1/management/status")).EnsureSuccessStatusCode();

        var missingCsrf = await browser.PostAsJsonAsync("/api/auth/runner-enrollments", new { name = "runner-01" });
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);

        using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/runner-enrollments")
        {
            Content = JsonContent.Create(new { name = "runner-01" })
        };
        enrollmentRequest.Headers.Add("X-CSRF-Token", auth!.CsrfToken);
        var enrollmentResponse = await browser.SendAsync(enrollmentRequest);
        enrollmentResponse.EnsureSuccessStatusCode();
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>();
        Assert.StartsWith("enr.", enrollment!.EnrollmentCode);

        var enrolledResponse = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        enrolledResponse.EnsureSuccessStatusCode();
        var enrolled = await enrolledResponse.Content.ReadFromJsonAsync<OneTimeSecretResponse>();
        Assert.StartsWith("rnr.", enrolled!.Secret);

        using var selfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/runner");
        selfRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.Secret);
        var selfResponse = await anonymous.SendAsync(selfRequest);
        selfResponse.EnsureSuccessStatusCode();
        var self = await selfResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(enrolled.RunnerId, self.GetProperty("id").GetString());

        var replay = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var runner = new RClient(anonymous, enrolled.RunnerId, authToken: enrolled.Secret);
        Assert.Equal(enrolled.RunnerId, await runner.RegisterAsync(enrolled.RunnerName, "service", CancellationToken.None));
        var claim = await runner.ClaimAsync(new RClaim(
            enrolled.RunnerId, enrolled.RunnerName, "runner-host", 42, "remote-runner", AvailableSlots: 1),
            CancellationToken.None);
        Assert.NotEqual(Runner::AgentRunner.RunnerClaimStatus.Invalid, claim.Status);

        using var drainRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/management/commands")
        {
            Content = JsonContent.Create(new
            {
                kind = "runner-drain", dryRun = false, confirmation = "runner-drain",
                idempotencyKey = "networked-runner-drain", runnerId = enrolled.RunnerId,
            })
        };
        drainRequest.Headers.Add("X-CSRF-Token", auth.CsrfToken);
        drainRequest.Headers.Add("Idempotency-Key", "networked-runner-drain");
        var drained = await browser.SendAsync(drainRequest);
        drained.EnsureSuccessStatusCode();
        var managementStatus = await browser.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/management/status");
        Assert.Equal("draining", managementStatus.GetProperty("runners")[0].GetProperty("state").GetString());
        Assert.True(File.Exists(Path.Combine(_workspace, ".audit", "management.jsonl")));

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("X-CSRF-Token", auth.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(logoutRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/tasks")).StatusCode);
    }

    [Fact]
    public async Task Untrusted_forwarded_https_is_ignored_but_named_proxy_is_trusted()
    {
        using var untrusted = BuildFactory("203.0.113.15");
        using var untrustedClient = untrusted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://studio.test") });
        using var spoof = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        spoof.Headers.Add("X-Forwarded-Proto", "https");
        spoof.Headers.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await untrustedClient.SendAsync(spoof)).StatusCode);

        using var trusted = BuildFactory("172.30.83.3");
        using var trustedClient = trusted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://studio.test") });
        using var forwarded = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        forwarded.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.OK, (await trustedClient.SendAsync(forwarded)).StatusCode);
    }

    [Fact]
    public async Task First_remote_owner_requires_the_installer_code()
    {
        using var factory = BuildFactory("203.0.113.15");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test") });
        var status = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");
        Assert.True(status!.BootstrapCodeRequired);
        var owner = new { username = "first.owner", password = "correct horse battery staple!", displayName = "First Owner" };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/bootstrap", owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            owner.username, owner.password, owner.displayName, bootstrapCode = "wrong"
        })).StatusCode);
        (await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            owner.username, owner.password, owner.displayName, bootstrapCode = "website-setup-code"
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Loopback_bootstrap_and_named_host_health_remain_available()
    {
        using var factory = BuildFactory("127.0.0.1");
        using var namedHost = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://studio.test") });
        Assert.Equal(HttpStatusCode.OK, (await namedHost.GetAsync("/healthz")).StatusCode);
        using var wrongHost = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://127.0.0.1") });
        Assert.Equal(HttpStatusCode.BadRequest, (await wrongHost.GetAsync("/healthz")).StatusCode);
        using var loopback = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test") });
        (await loopback.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "loopback.owner", password = "correct horse battery staple!", displayName = "Loopback Owner"
        })).EnsureSuccessStatusCode();
    }

    private WebApplicationFactory<Program> BuildFactory(string? remoteIp = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        if (remoteIp is not null)
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse(remoteIp))));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = "networked",
            ["Security:BootstrapCode"] = "website-setup-code",
            ["Security:TrustedProxyAddresses"] = "172.30.83.3",
            ["AllowedHosts"] = "studio.test"
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

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
