extern alias Runner;

using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

using RClient = Runner::AgentRunner.TaskServerClient;
using RTaskServerException = Runner::AgentRunner.TaskServerException;

/// <summary>
/// AGT-3432: every host polls the device-login and Git setup claims. The networked
/// monolith refused the host credential with 401 because the routes had no Runner
/// scope, so hosts retried every two seconds. The monolith offers neither flow and
/// now says so with a typed answer the host's poller stops on.
/// </summary>
public sealed class MonolithRunnerClaimTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "monolith-runner-claims-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(SecurityProfiles.Local)]
    [InlineData(SecurityProfiles.Networked)]
    public async Task Host_credential_claims_answer_route_unavailable(string profile)
    {
        using var factory = Factory(profile);
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (runner, _, secret) = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("host-a", null, null, null)).Code);
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"),
        });
        using var client = new RClient(http, runner.Id, authToken: secret);

        var deviceLogin = await Assert.ThrowsAsync<RTaskServerException>(
            () => client.ClaimDeviceLoginAsync(CancellationToken.None));
        var gitSetup = await Assert.ThrowsAsync<RTaskServerException>(
            () => client.ClaimGitSetupAsync(runner.Id, CancellationToken.None));

        Assert.Equal((404, ApiErrorCodes.RouteUnavailable), (deviceLogin.StatusCode, deviceLogin.ErrorCode));
        Assert.Equal((404, ApiErrorCodes.RouteUnavailable), (gitSetup.StatusCode, gitSetup.ErrorCode));
    }

    [Fact]
    public async Task Networked_host_credential_cannot_claim_for_another_host()
    {
        using var factory = Factory(SecurityProfiles.Networked);
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (_, _, secret) = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("host-a", null, null, null)).Code);
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"),
        });
        using var client = new RClient(http, "host-b", authToken: secret);

        var refused = await Assert.ThrowsAsync<RTaskServerException>(
            () => client.ClaimGitSetupAsync("host-b", CancellationToken.None));

        Assert.Equal(403, refused.StatusCode);
    }

    private WebApplicationFactory<Program> Factory(string profile)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = profile,
                    ["Security:BootstrapCode"] = "monolith-runner-claims-setup-code",
                    ["AllowedHosts"] = "studio.test",
                }));
        });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }
}
