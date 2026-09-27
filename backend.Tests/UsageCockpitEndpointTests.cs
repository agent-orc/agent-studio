using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class UsageCockpitEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usage-cockpit-http-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Empty_and_bus_only_ledgers_are_valid_and_bus_failure_is_isolated()
    {
        Directory.CreateDirectory(_root);
        using var factory = BuildFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test")
        });
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var emptyPath = Path.Combine(_root, "empty-project");
        var busPath = Path.Combine(_root, "bus-project");
        Directory.CreateDirectory(emptyPath);
        Directory.CreateDirectory(busPath);
        var empty = registry.EnsureProjectForStorage(emptyPath, "Empty", DefaultWorkspace.Id);
        var busOnly = registry.EnsureProjectForStorage(busPath, "Bus Only", DefaultWorkspace.Id);
        var now = DateTime.UtcNow;
        var usage = new OrchestratorTokenUsage
        {
            Model = "gpt-5.6-sol", InputTokens = 600, CacheReadTokens = 400,
            InputIncludesCached = true, UsageNormalization = "openai-input-includes-cached-v1"
        };
        var bridge = new AgentMessageBusBridge(factory.Services.GetRequiredService<AgentMessageBusStore>(),
            factory.Services.GetRequiredService<IConfiguration>(), NullLogger<AgentMessageBusBridge>.Instance);
        await bridge.EmitTokenUsageAsync(busOnly.DisplayName, "active",
            AgentMessageBusBridge.ParticipantForCli("codex"), "task-token-receipt", usage, now);

        var response = await client.GetAsync("/api/usage/cockpit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DefaultWorkspace.Id, body.GetProperty("workspaceId").GetString());
        Assert.Equal("complete", body.GetProperty("sources").GetProperty("cost").GetProperty("status").GetString());
        var cost = body.GetProperty("cost");
        var children = cost.GetProperty("projects").EnumerateArray().ToArray();
        var emptyChild = children.Single(child => child.GetProperty("projectId").GetString() == empty.Id);
        var busChild = children.Single(child => child.GetProperty("projectId").GetString() == busOnly.Id);
        Assert.Equal("complete", emptyChild.GetProperty("coverage").GetProperty("status").GetString());
        Assert.Equal(0m, emptyChild.GetProperty("todayUsd").GetDecimal());
        Assert.Equal(0m, emptyChild.GetProperty("weekUsd").GetDecimal());
        Assert.Equal(JsonValueKind.Null, emptyChild.GetProperty("latestReceiptAt").ValueKind);
        Assert.Equal("complete", busChild.GetProperty("coverage").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, busChild.GetProperty("latestReceiptAt").ValueKind);
        var expected = TokenPricing.Estimate("gpt-5.6-sol", 600, 0, 400, 0, now).Total;
        Assert.Equal(expected, busChild.GetProperty("todayUsd").GetDecimal());
        Assert.Equal(expected, cost.GetProperty("todayUsd").GetDecimal());
        Assert.Equal(children.Sum(child => child.GetProperty("weekUsd").GetDecimal()),
            cost.GetProperty("weekUsd").GetDecimal());

        // The registry retains its workspace; the bus dependency now has no
        // configured root. Its failure must leave quota, runs, and slots readable.
        factory.Services.GetRequiredService<IConfiguration>()["TaskRepository"] = "";
        var degradedResponse = await client.GetAsync("/api/usage/cockpit");
        Assert.Equal(HttpStatusCode.OK, degradedResponse.StatusCode);
        var degraded = await degradedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("partial", degraded.GetProperty("sources").GetProperty("cost").GetProperty("status").GetString());
        Assert.Equal("complete", degraded.GetProperty("sources").GetProperty("quota").GetProperty("status").GetString());
        Assert.Equal("complete", degraded.GetProperty("sources").GetProperty("runs").GetProperty("status").GetString());
        Assert.Equal("unavailable", degraded.GetProperty("cost").GetProperty("projects")
            .EnumerateArray().Single(child => child.GetProperty("projectId").ValueKind == JsonValueKind.Null)
            .GetProperty("coverage").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Array, degraded.GetProperty("clis").ValueKind);
        Assert.Equal(JsonValueKind.Array, degraded.GetProperty("runs").ValueKind);
        Assert.Equal(3, degraded.GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task Networked_route_requires_a_session_and_denies_a_workspace_without_visible_projects()
    {
        Directory.CreateDirectory(_root);
        using var factory = BuildFactory(networked: true);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"), HandleCookies = false
        });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/usage/cockpit")).StatusCode);

        var access = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = access.Bootstrap(new BootstrapRequest("cockpit.owner", "correct horse battery staple!", null));
        var user = access.CreateUser(new CreateUserRequest("cockpit.scoped", "Scoped",
            StudioRoles.Operator, ["PROJ-999"], "temporary scoped phrase!"));
        var login = access.Login(user.User.Username, user.TemporaryPassword, "cockpit|127.0.0.1");
        access.ChangePassword(new HumanPrincipal(login.User,
            access.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(user.TemporaryPassword, "new scoped password phrase!"));
        var projectPath = Path.Combine(_root, "visible-only-to-owner");
        Directory.CreateDirectory(projectPath);
        factory.Services.GetRequiredService<ProjectRegistry>()
            .EnsureProjectForStorage(projectPath, "Owner Project", DefaultWorkspace.Id);
        using var ownerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/usage/cockpit");
        ownerRequest.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={owner.SessionToken}");
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(ownerRequest)).StatusCode);
        using var scopedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/usage/cockpit");
        scopedRequest.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={login.SessionToken}");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await anonymous.SendAsync(scopedRequest)).StatusCode);
    }

    private WebApplicationFactory<Program> BuildFactory(bool networked = false)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Security:Profile"] = networked ? "networked" : "local",
                    ["AllowedHosts"] = "studio.test",
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs")
                }));
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
