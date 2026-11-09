extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using RClient = Runner::AgentRunner.TaskServerClient;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3432: the networked monolith mapped no device sign-in or Git setup route,
/// so every host's claim poll failed every two seconds (401, later 404
/// route-not-mapped) and a sign-in from the Credentials page never reached a
/// host. The host's own credential now claims both, or hears that there is no work.
/// </summary>
public sealed class NetworkedHostSetupTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-host-setup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_host_claims_device_sign_in_and_git_setup_with_its_credential()
    {
        await using var factory = BuildFactory();
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var host = store.EnrollRunner(store.CreateEnrollment(new RunnerEnrollmentRequest("host-a-coding", null, null, null)).Code);
        var other = store.EnrollRunner(store.CreateEnrollment(new RunnerEnrollmentRequest("host-b-coding", null, null, null)).Code);
        // The runner client sets its bearer as a default header; raw probes use their own client.
        using var probe = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        using var runnerHttp = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        using var runner = new RClient(runnerHttp, host.Runner.Id, authToken: host.Secret);

        // Nothing queued: both claims answer "no work" instead of failing.
        Assert.Null(await runner.ClaimDeviceLoginAsync(CancellationToken.None));
        Assert.Null(await runner.ClaimGitSetupAsync(host.Runner.Id, CancellationToken.None));

        factory.Services.GetRequiredService<V1ReviewExecutorRegistry>().Register(host.Runner.Id,
            new RegisterRunnerRequest("host-a-coding", "host-a", "instance-1", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]));

        // The Credentials page starts the sign-in through the Owner route.
        using var started = await OwnerPostAsync(http, owner, "/api/v1/management/device-logins",
            new StartDeviceLoginRequest("host-a", "codex"));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<DeviceLoginSessionDto>())!;
        var claim = await runner.ClaimDeviceLoginAsync(CancellationToken.None);
        Assert.Equal((session.Handle, "codex"), (claim!.Handle, claim.Provider));
        Assert.Equal("running", await runner.DeviceLoginStateAsync(claim.Handle, CancellationToken.None));

        using var setup = await OwnerPostAsync(http, owner, "/api/v1/management/runner-git-setups",
            new StartRunnerGitSetupRequest(host.Runner.Id, "https://github.com/acme/widgets", "deploy-key"));
        Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        var work = await runner.ClaimGitSetupAsync(host.Runner.Id, CancellationToken.None);
        Assert.Equal(("https://github.com/acme/widgets", "configure"), (work!.RepositoryUrl, work.Action));

        // Another host's credential cannot claim for this runner; an anonymous caller is refused.
        Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(probe, $"/api/v1/runners/{host.Runner.Id}/git-setups/claim", other.Secret));
        Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(probe, $"/api/v1/runners/{host.Runner.Id}/device-login/claim", other.Secret));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostStatusAsync(probe, $"/api/v1/runners/{host.Runner.Id}/device-login/claim", null));
    }

    [Fact]
    public async Task The_claude_code_the_owner_pastes_reaches_only_the_claiming_host_once()
    {
        await using var factory = BuildFactory();
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var host = store.EnrollRunner(store.CreateEnrollment(new RunnerEnrollmentRequest("host-a-coding", null, null, null)).Code);
        var other = store.EnrollRunner(store.CreateEnrollment(new RunnerEnrollmentRequest("host-b-coding", null, null, null)).Code);
        using var runnerHttp = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        using var runner = new RClient(runnerHttp, host.Runner.Id, authToken: host.Secret);
        using var probe = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        factory.Services.GetRequiredService<V1ReviewExecutorRegistry>().Register(host.Runner.Id,
            new RegisterRunnerRequest("host-a-coding", "host-a", "instance-1", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]));

        using var started = await OwnerPostAsync(http, owner, "/api/v1/management/device-logins",
            new StartDeviceLoginRequest("host-a", "claude"));
        var session = (await started.Content.ReadFromJsonAsync<DeviceLoginSessionDto>())!;
        var claim = (await runner.ClaimDeviceLoginAsync(CancellationToken.None))!;
        await runner.ReportDeviceLoginInstructionsAsync(claim.Handle, new DeviceLoginInstructionsRequest(
            "https://claude.com/cai/oauth/authorize?code=true&client_id=studio", null), CancellationToken.None);
        Assert.Null(await runner.TakeDeviceLoginCodeAsync(claim.Handle, CancellationToken.None));

        var code = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + "#" + Guid.NewGuid().ToString("N");
        using var submitted = await OwnerPostAsync(http, owner, $"/api/v1/management/device-logins/{session.Handle}/code",
            new DeviceLoginCodeRequest(code), session.SessionKey);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(probe,
            $"/api/v1/runners/{host.Runner.Id}/device-login/{claim.Handle}/code", other.Secret));
        Assert.Equal(code, await runner.TakeDeviceLoginCodeAsync(claim.Handle, CancellationToken.None));
        Assert.Null(await runner.TakeDeviceLoginCodeAsync(claim.Handle, CancellationToken.None));
    }

    [Fact]
    public async Task The_monolith_maps_the_task_server_device_login_route_family()
    {
        await using var factory = BuildFactory();
        _ = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => (endpoint.RoutePattern.RawText ?? "").Contains("device-login", StringComparison.Ordinal))
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText!.TrimEnd('/')}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The same set task-server.Tests/DeviceLoginRouteTests pins for the standalone Task Server.
        Assert.Equal(new[]
        {
            "GET /api/v1/management/device-logins/{handle}",
            "GET /api/v1/runners/{runnerId}/device-login/{handle}",
            "POST /api/v1/management/device-logins",
            "POST /api/v1/management/device-logins/{handle}/cancel",
            "POST /api/v1/management/device-logins/{handle}/code",
            "POST /api/v1/runners/{runnerId}/device-login/claim",
            "POST /api/v1/runners/{runnerId}/device-login/{handle}/code",
            "POST /api/v1/runners/{runnerId}/device-login/{handle}/completion",
            "POST /api/v1/runners/{runnerId}/device-login/{handle}/instructions",
        }.Order(StringComparer.Ordinal), routes);
    }

    private static async Task<HttpResponseMessage> OwnerPostAsync<T>(HttpClient http,
        (StudioUser User, string SessionToken, string CsrfToken) owner, string path, T body,
        string? deviceLoginSession = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (deviceLoginSession is not null) request.Headers.Add("X-Device-Login-Session", deviceLoginSession);
        request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={owner.SessionToken}");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("X-CSRF-Token", owner.CsrfToken);
        return await http.SendAsync(request);
    }

    private static async Task<HttpStatusCode> PostStatusAsync(HttpClient http, string path, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    private WebApplicationFactory<Program> BuildFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = "networked",
            ["Security:BootstrapCode"] = "website-setup-code",
            ["Security:TrustedProxyAddresses"] = "172.30.83.3",
            ["Security:PublicCloudflareEdge"] = "false",
            ["AllowedHosts"] = "studio.test",
        }));
    });

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
