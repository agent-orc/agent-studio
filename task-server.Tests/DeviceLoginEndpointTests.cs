using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-3225 follow-up: a provider sign-in authorizes the host's provider credentials, so only an
/// Owner may start, read or cancel one, and a target runner must be an active runner of the host.
/// </summary>
public sealed class DeviceLoginEndpointTests
{
    private const string Sessions = "/api/v1/management/device-logins";

    [Fact]
    public async Task Only_an_owner_starts_reads_or_cancels_a_device_sign_in()
    {
        using var temp = new TempDirectory();
        var studioToken = "studio-" + Guid.NewGuid().ToString("N");
        await using var factory = new Factory(temp.Path, studioToken);
        await RegisterAsync(factory, "coding-a", "host-a");
        using var operatorEdge = Client(factory, studioToken, StudioUserRoles.Operator);
        using var owner = Client(factory, studioToken, StudioUserRoles.Owner);

        Assert.Equal("owner-role-required", await ErrorCodeAsync(await operatorEdge.PostAsJsonAsync(Sessions,
            new StartDeviceLoginRequest("host-a", "codex")), HttpStatusCode.Forbidden));

        var started = await owner.PostAsJsonAsync(Sessions, new StartDeviceLoginRequest("host-a", "codex"));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var session = (await started.Content.ReadFromJsonAsync<DeviceLoginSessionDto>())!;
        operatorEdge.DefaultRequestHeaders.Add("X-Device-Login-Session", session.SessionKey);
        owner.DefaultRequestHeaders.Add("X-Device-Login-Session", session.SessionKey);

        Assert.Equal("owner-role-required", await ErrorCodeAsync(
            await operatorEdge.GetAsync($"{Sessions}/{session.Handle}"), HttpStatusCode.Forbidden));
        Assert.Equal("owner-role-required", await ErrorCodeAsync(
            await operatorEdge.PostAsJsonAsync($"{Sessions}/{session.Handle}/cancel", new { }),
            HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{Sessions}/{session.Handle}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await owner.PostAsJsonAsync($"{Sessions}/{session.Handle}/cancel", new { })).StatusCode);
    }

    [Fact]
    public async Task A_target_runner_must_be_an_active_runner_of_the_host()
    {
        using var temp = new TempDirectory();
        var studioToken = "studio-" + Guid.NewGuid().ToString("N");
        await using var factory = new Factory(temp.Path, studioToken);
        await RegisterAsync(factory, "coding-a", "host-a");
        await RegisterAsync(factory, "review-a", "host-a");
        await RegisterAsync(factory, "coding-b", "host-b");
        using var owner = Client(factory, studioToken, StudioUserRoles.Owner);

        Assert.Equal("invalid-login", await ErrorCodeAsync(await owner.PostAsJsonAsync(Sessions,
            new StartDeviceLoginRequest("host-a", "codex", "coding-b")), HttpStatusCode.BadRequest));
        Assert.Equal("invalid-login", await ErrorCodeAsync(await owner.PostAsJsonAsync(Sessions,
            new StartDeviceLoginRequest("host-a", "codex", "unknown-runner")), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(Sessions,
            new StartDeviceLoginRequest("host-a", "codex", "review-a"))).StatusCode);
        Assert.Equal("login-active", await ErrorCodeAsync(await owner.PostAsJsonAsync(Sessions,
            new StartDeviceLoginRequest("host-a", "codex", "coding-a")), HttpStatusCode.Conflict));

        var sessions = factory.Services.GetRequiredService<DeviceLoginSessions>();
        Assert.Null(sessions.Claim("host-a", "coding-a"));
        Assert.NotNull(sessions.Claim("host-a", "review-a"));
    }

    private static async Task RegisterAsync(Factory factory, string runner, string host)
        => await factory.Services.GetRequiredService<TaskServerStore>().RegisterRunnerAsync(runner,
            new RegisterRunnerRequest(runner, host, runner + "-instance", "1.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]), runner, default);

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiError>())!.Code;
    }

    private static HttpClient Client(Factory factory, string studioToken, string actingRole)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", studioToken);
        client.DefaultRequestHeaders.Add(CredentialViewProtocol.ActingRoleHeader, actingRole);
        return client;
    }

    private sealed class Factory(string dataDirectory, string studioToken) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                    ["AUTH"] = "bearer",
                    ["STUDIO_AUTH_TOKEN"] = studioToken,
                }));
        }
    }
}
