using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// W34 §8 S4 "Public read-only edge" (AGT-2669), depending on the S2 route
/// inventory (AGT-2667, <see cref="PublicDemoExecutionProfileTests"/>). S2
/// denies a catalogued set of execution routes by identity; this edge is the
/// broader net behind it: every unsafe method is denied outright and every
/// safe method must match an explicit read allowlist.
/// </summary>
public sealed class PublicDemoReadOnlyEdgeTests(PublicDemoHostFixture shared) : IClassFixture<PublicDemoHostFixture>, IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "studio-public-demo-edge-" + Guid.NewGuid().ToString("N"));
    private readonly string _secondWorkspace = Path.Combine(
        Path.GetTempPath(),
        "studio-public-demo-edge-second-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("local", "GET", true, true)]
    [InlineData("networked", "POST", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "GET", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "HEAD", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "OPTIONS", true, true)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "GET", false, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "POST", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "PUT", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "PATCH", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "DELETE", true, false)]
    [InlineData(ExecutionAdmissionPolicy.PublicDemoProfile, "DELETE", false, false)]
    public void Edge_policy_matrix(string profile, string method, bool pathAllowlisted, bool expectedAllowed)
    {
        var decision = PublicDemoEdgePolicy.Decide(profile, method, pathAllowlisted);

        Assert.Equal(expectedAllowed, decision.Allowed);
        if (!expectedAllowed)
            Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, decision.Code);
    }

    [Theory]
    [InlineData("/api/environment", true)]
    [InlineData("/api/tasks", true)]
    [InlineData("/api/tasks/DEMO-1", true)]
    [InlineData("/api/tasks-malicious", false)]
    [InlineData("/api/projects", true)]
    [InlineData("/api/projects/demo-app/wiki/home", true)]
    [InlineData("/api/projects/demo-app/wiki-malicious", false)]
    [InlineData("/api/projects/demo-app/workbenches", true)]
    [InlineData("/api/projects/demo-app/workbenches/DEMO-W1/references", true)]
    [InlineData("/api/workbenches/DEMO-W1", true)]
    [InlineData("/hubs/jobs", true)]
    [InlineData("/hubs/jobs/negotiate", true)]
    [InlineData("/hubs/jobs/unknown", false)]
    [InlineData("/hubs/jobs/negotiate/unknown", false)]
    [InlineData("/api/projects/demo-app/security", false)]
    [InlineData("/api/projects/demo-app/security/reviews", false)]
    [InlineData("/api/projects/demo-app/security/files/report.json", false)]
    [InlineData("/api/projects/demo-app/token-usage", false)]
    [InlineData("/api/projects/demo-app/proposals", false)]
    [InlineData("/api/projects/demo-app/settings", false)]
    [InlineData("/api/projects/demo-app", false)]
    [InlineData("/api/audits/run-1", false)]
    [InlineData("/api/pipeline/health", false)]
    [InlineData("/api/workspaces", false)]
    [InlineData("/api/git/status", false)]
    [InlineData("/api/definitely-not-a-real-endpoint", false)]
    public void Read_allowlist_matches_only_the_declared_browse_surface(string path, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, PublicDemoReadAllowlist.Allows(path));
    }

    [Fact]
    public void Viewer_session_store_issues_touches_and_expires_sliding_sessions()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-20T09:00:00Z"));
        var store = new PublicDemoViewerSessionStore(clock) { SessionLifetime = TimeSpan.FromMinutes(30) };

        var id = store.Issue();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.True(store.Touch(id));

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(store.Touch(id), "still inside the sliding window after the refresh above");

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(store.Touch(id), "31 minutes without activity exceeds the 30-minute sliding window");

        Assert.False(store.Touch(null));
        Assert.False(store.Touch("unknown-session-id"));
    }

    [Fact]
    public async Task Allowlisted_read_succeeds_and_issues_a_hardened_ephemeral_viewer_cookie()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
            HandleCookies = false,
        });

        var response = await client.GetAsync("/api/environment");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("publicDemo").GetProperty("active").GetBoolean());
        Assert.Equal(
            ExecutionAdmissionPolicy.PublicDemoProfile,
            body.GetProperty("publicDemo").GetProperty("profile").GetString());

        Assert.Equal(
            "default-src 'self'",
            response.Headers.GetValues("Content-Security-Policy").Single().Split(';')[0].Trim());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());

        var setCookie = response.Headers.GetValues("Set-Cookie").Single(v =>
            v.StartsWith(PublicDemoViewerSessionStore.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_allowlisted_read_is_denied_even_though_the_route_exists()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });

        var response = await client.GetAsync("/api/audits/run-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, body.GetProperty("error").GetString());
        Assert.Equal(ExecutionAdmissionPolicy.PublicDemoProfile, body.GetProperty("profile").GetString());
    }

    [Fact]
    public async Task Unsafe_method_on_a_non_execution_tagged_mutation_still_gets_the_typed_edge_denial()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });

        // /api/tags carries no ExecutionRouteMetadata (it is not part of
        // the S2 catalogue), so this proves the edge's blanket net
        // catches mutations S2 was never told about, rather than S2
        // masking the gap.
        var response = await client.PostAsJsonAsync("/api/tags", new { id = "x", label = "X", color = "#fff" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/", "POST")]
    [InlineData("/not-an-api-route", "DELETE")]
    [InlineData("/hubs/jobs", "DELETE")]
    [InlineData("/hubs/jobs/unknown", "POST")]
    public async Task Raw_unsafe_requests_outside_the_replay_and_negotiate_exceptions_get_typed_denial(
        string path,
        string method)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Replay_ingest_requires_the_exclusive_replay_credential_in_public_demo_mode()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });
        var requestBody = new DemoReplayEventRequest(
            "demo-trace",
            new string('0', 64),
            1,
            "invalid-signature",
            new DemoReplayFrame(
                1,
                0,
                "DEMO-1",
                DemoReplayFrameKinds.TurnStarted,
                "Simulated frame"),
            DateTime.UtcNow);

        var anonymous = await client.PostAsJsonAsync("/api/runner/replay/events", requestBody);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("runner-authentication-required", await ErrorCodeAsync(anonymous));

        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (_, enrollmentCode) = store.CreateEnrollment(new RunnerEnrollmentRequest(
            "demo-runner-replay",
            [RunnerScopes.DemoReplay],
            null,
            null));
        var (_, _, secret) = store.EnrollRunner(enrollmentCode);
        using var authenticatedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/runner/replay/events")
        {
            Content = JsonContent.Create(requestBody),
        };
        authenticatedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var authenticated = await client.SendAsync(authenticatedRequest);

        Assert.Equal(HttpStatusCode.Forbidden, authenticated.StatusCode);
        Assert.Equal(DemoReplayDenialCodes.Disabled, await ErrorCodeAsync(authenticated));
    }

    [Fact]
    public async Task Plain_http_is_rejected_before_any_allowlist_check()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://demo.test"),
        });

        var response = await client.GetAsync("/api/environment");

        Assert.Equal((HttpStatusCode)426, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https-required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cross_origin_request_is_denied()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/environment");
        request.Headers.Add("Origin", "https://not-the-demo.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://demo.test")]
    [InlineData("https://demo.test:444")]
    [InlineData("https://demo.test/path")]
    public async Task Origin_must_match_the_complete_https_origin(string origin)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/environment");
        request.Headers.Add("Origin", origin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PublicDemoEdgePolicy.ReadOnlyDeniedCode, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Https_allowed_and_denied_responses_carry_hsts()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });

        var allowed = await client.GetAsync("/api/environment");
        var denied = await client.PostAsJsonAsync("/api/tags", new { name = "blocked" });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(allowed.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(denied.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Rate_limit_returns_a_typed_safe_denial()
    {
        using var factory = BuildFactory(PermitLimit(2));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/environment")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/environment")).StatusCode);
        var limited = await client.GetAsync("/api/environment");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("public-demo-rate-limited", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("exception", out _));
        Assert.False(body.TryGetProperty("stackTrace", out _));
    }

    [Fact]
    public async Task Rate_limit_of_one_host_leaves_a_concurrent_default_host_unlimited()
    {
        using var limited = BuildFactory(PermitLimit(2));
        using var standard = BuildFactory(new Dictionary<string, string?> { ["TaskRepository"] = _secondWorkspace });
        var clients = await BootTogetherAsync(limited, standard);
        using var limitedClient = clients[0];
        using var standardClient = clients[1];

        var limitedStatuses = new List<HttpStatusCode>();
        var standardStatuses = new List<HttpStatusCode>();
        for (var request = 0; request < 3; request++)
        {
            var responses = await Task.WhenAll(
                limitedClient.GetAsync("/api/environment"),
                standardClient.GetAsync("/api/environment"));
            limitedStatuses.Add(responses[0].StatusCode);
            standardStatuses.Add(responses[1].StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], limitedStatuses);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK], standardStatuses);
    }

    [Fact]
    public void Public_demo_kestrel_body_limit_is_tightened()
    {
        var factory = shared.Factory;

        Assert.Equal(16 * 1024, KestrelLimits(factory).MaxRequestBodySize);
    }

    [Fact]
    public async Task Concurrent_hosts_each_keep_their_own_security_profile_and_body_cap()
    {
        // Program.cs reads the profile, and from it the Kestrel body cap and
        // the execution admission, before Build(). These tests used to reach
        // that read through the process environment, so a second host booting
        // at the same moment saw the first one's profile (AGT-3106).
        using var demo = BuildFactory();
        using var local = BuildFactory(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _secondWorkspace,
            ["Security:Profile"] = SecurityProfiles.Local,
        });
        var clients = await BootTogetherAsync(demo, local);
        using var demoClient = clients[0];
        using var localClient = clients[1];

        var demoProfile = await PublicDemoAsync(demoClient);
        var localProfile = await PublicDemoAsync(localClient);

        Assert.True(demoProfile.GetProperty("active").GetBoolean());
        Assert.Equal(SecurityProfiles.PublicDemo, demoProfile.GetProperty("profile").GetString());
        Assert.False(localProfile.GetProperty("active").GetBoolean());
        Assert.Equal(SecurityProfiles.Local, localProfile.GetProperty("profile").GetString());
        Assert.Equal(16 * 1024, KestrelLimits(demo).MaxRequestBodySize);
        Assert.Equal(25 * 1024 * 1024, KestrelLimits(local).MaxRequestBodySize);
        Assert.True(demo.Services.GetRequiredService<StartupExecutionAdmission>().IsPublicDemo);
        Assert.False(local.Services.GetRequiredService<StartupExecutionAdmission>().IsPublicDemo);
    }

    [Fact]
    public async Task Hub_negotiate_post_is_not_caught_by_the_blanket_unsafe_method_denial()
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://demo.test"),
        });

        var negotiate = await client.PostAsync("/hubs/jobs/negotiate?negotiateVersion=1", content: null);

        Assert.NotEqual(HttpStatusCode.Forbidden, negotiate.StatusCode);
    }

    private WebApplicationFactory<Program> BuildFactory(
        IReadOnlyDictionary<string, string?>? overrides = null)
        => PublicDemoHostFixture.CreateFactory(_workspace, overrides);

    private static Dictionary<string, string?> PermitLimit(int permits) => new()
    {
        ["Security:PublicDemoEdge:RateLimit:PermitLimit"] = permits.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Starts both hosts at the same time and returns a client of each.</summary>
    private static Task<HttpClient[]> BootTogetherAsync(
        WebApplicationFactory<Program> first,
        WebApplicationFactory<Program> second)
    {
        var options = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://demo.test") };
        return Task.WhenAll(
            Task.Run(() => first.CreateClient(options)),
            Task.Run(() => second.CreateClient(options)));
    }

    private static async Task<JsonElement> PublicDemoAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/environment");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("publicDemo");
    }

    private static KestrelServerLimits KestrelLimits(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    public void Dispose()
    {
        TestTempRoot.TryDelete(_workspace);
        TestTempRoot.TryDelete(_secondWorkspace);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
