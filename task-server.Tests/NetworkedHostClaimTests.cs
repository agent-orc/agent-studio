using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-3432: a host joined to a networked install claims device sign-ins and Git setups
/// with the credential its join token gave it.
/// </summary>
public sealed class NetworkedHostClaimTests
{
    private const string StudioToken = "studio-edge-token-0000000000000000000000000002";
    private const string OwnerCode = "owner-code-cccccccccccccccccccccccccccccccccccc";

    [Theory]
    [InlineData("coding")]
    [InlineData("review")]
    public async Task A_joined_host_claims_device_sign_ins_and_git_setups(string role)
    {
        using var temp = new TempDirectory();
        await using var factory = new Factory(temp.Path);
        using var edge = Client(factory, StudioToken);
        var owner = await (await edge.PostAsJsonAsync("/api/v1/studio/auth/bootstrap",
            new StudioBootstrapRequest("owner", "owner password long enough", BootstrapCode: OwnerCode)))
            .Content.ReadFromJsonAsync<StudioOwnerBootstrapSessionDto>();
        using var ownerClient = Client(factory, StudioToken, owner!.SessionToken);
        var joined = await ownerClient.PostAsJsonAsync("/api/v1/management/host-join-tokens",
            new CreateHostJoinTokenRequest("https://tasks.example.test", "host-a", role));
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        var join = (await joined.Content.ReadFromJsonAsync<IssuedHostJoinToken>())!;
        var body = join.JoinToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var payload = JsonDocument.Parse(Convert.FromBase64String(
            body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=')));
        using var anonymous = Client(factory, credential: null);
        var exchanged = await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange",
            new ExchangeEnrolmentRequest(payload.RootElement.GetProperty("enrolmentCode").GetString()!,
                payload.RootElement.GetProperty("installationId").GetString()!));
        Assert.Equal(HttpStatusCode.Created, exchanged.StatusCode);
        var credential = (await exchanged.Content.ReadFromJsonAsync<ExchangedEnrolment>())!.Issued.Credential;
        using var host = Client(factory, credential);
        host.DefaultRequestHeaders.TryAddWithoutValidation("X-Principal-Consumer-Id", join.RunnerId);
        host.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Id", join.RunnerId);

        var deviceLogin = await host.PostAsJsonAsync(
            $"/api/v1/runners/{join.RunnerId}/device-login/claim", new { });
        var gitSetup = await host.PostAsJsonAsync(
            $"/api/v1/runners/{join.RunnerId}/git-setups/claim", new { });

        Assert.True(deviceLogin.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"device-login claim answered {(int)deviceLogin.StatusCode}: {await deviceLogin.Content.ReadAsStringAsync()}");
        Assert.True(gitSetup.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"git-setups claim answered {(int)gitSetup.StatusCode}: {await gitSetup.Content.ReadAsStringAsync()}");
    }

    private static HttpClient Client(Factory factory, string? credential, string? session = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        if (credential is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (session is not null)
            client.DefaultRequestHeaders.Add("X-Studio-Session-Token", session);
        return client;
    }

    private sealed class Factory(string dataDirectory) : WebApplicationFactory<Program>
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
                    ["STUDIO_AUTH_TOKEN"] = StudioToken,
                    ["OWNER_BOOTSTRAP_CODE"] = OwnerCode,
                }));
        }
    }
}
