using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2983 moved the Studio's core-attach calls onto their versioned Task
/// Server routes. OrchestratorApi keeps serving exactly those method and path
/// shapes from its legacy handlers (local monolith and Stable's transitional
/// proxy profile alike) and leaves every other <c>/api/v1</c> route to its
/// v1 owner.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class StudioV1LegacyRouteAliasTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-v1-alias-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("GET", "/api/v1/studio/auth/status", "/api/auth/status", null)]
    [InlineData("POST", "/api/v1/studio/auth/bootstrap", "/api/auth/bootstrap", null)]
    [InlineData("POST", "/api/v1/studio/auth/login", "/api/auth/login", null)]
    [InlineData("POST", "/api/v1/studio/auth/logout", "/api/auth/logout", null)]
    [InlineData("POST", "/api/v1/studio/auth/change-password", "/api/auth/change-password", null)]
    [InlineData("GET", "/api/v1/studio/board", "/api/tasks/grouped", null)]
    [InlineData("GET", "/api/v1/workspaces", "/api/workspaces", null)]
    [InlineData("POST", "/api/v1/workspaces", "/api/workspaces", null)]
    [InlineData("GET", "/api/v1/projects", "/api/projects", null)]
    [InlineData("POST", "/api/v1/projects", "/api/projects", null)]
    [InlineData("GET", "/api/v1/projects/Agent Studio/tasks/AGT-1", "/api/tasks/AGT-1", "Agent Studio")]
    [InlineData("GET", "/api/v1/projects/-/tasks/AGT-1", "/api/tasks/AGT-1", null)]
    [InlineData("DELETE", "/api/v1/projects/-/tasks/AGT-1", "/api/tasks/AGT-1", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/continue", "/api/tasks/AGT-1/continue", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/move", "/api/tasks/AGT-1/move", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/move-to-top", "/api/tasks/AGT-1/move-to-top", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/start", "/api/tasks/AGT-1/start", null)]
    [InlineData("PUT", "/api/v1/projects/-/tasks/AGT-1/state", "/api/tasks/AGT-1/state", null)]
    [InlineData("POST", "/api/v1/projects/-/tasks/AGT-1/stop", "/api/tasks/AGT-1/stop", null)]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/global", "/api/orchestrator/context/global", null)]
    [InlineData("GET", "/api/v1/studio/orchestrator/context/task:PROJ/AGT-1", "/api/orchestrator/context/task:PROJ/AGT-1", null)]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/project:PROJ/refresh", "/api/orchestrator/context/project:PROJ/refresh", null)]
    [InlineData("GET", "/api/v1/studio/orchestrator/sessions", "/api/orchestrator/sessions", null)]
    [InlineData("POST", "/api/v1/studio/orchestrator/sessions/workbench:PROJ/wb-1/turns", "/api/orchestrator/sessions/workbench:PROJ/wb-1/turns", null)]
    [InlineData("GET", "/api/v1/studio/runner/status", "/api/runner/status", null)]
    [InlineData("GET", "/api/v1/studio/runner/PROJ/orchestrator-chat", "/api/runner/PROJ/orchestrator-chat", null)]
    [InlineData("POST", "/api/v1/studio/runner/PROJ/orchestrator-chat", "/api/runner/PROJ/orchestrator-chat", null)]
    [InlineData("GET", "/api/v1/studio/runner/PROJ/orchestrator-chat/attachments/a.png", "/api/runner/PROJ/orchestrator-chat/attachments/a.png", null)]
    [InlineData("GET", "/hubs/v1/studio", "/hubs/jobs", null)]
    [InlineData("POST", "/hubs/v1/studio/negotiate", "/hubs/jobs/negotiate", null)]
    public void Core_attach_shapes_resolve_to_their_legacy_handler(string method, string path, string legacy, string? project)
    {
        var target = StudioV1LegacyRouteAlias.Resolve(method, path);

        Assert.NotNull(target);
        Assert.Equal(legacy, target.Value.Path);
        Assert.Equal(project, target.Value.Project);
    }

    [Theory]
    [InlineData("GET", "/api/v1/management/status")]
    [InlineData("GET", "/api/v1/protocol")]
    [InlineData("PUT", "/api/v1/projects/PROJ/tasks/AGT-1")]
    [InlineData("GET", "/api/v1/projects/PROJ/tasks")]
    [InlineData("POST", "/api/v1/projects/PROJ/tasks")]
    [InlineData("GET", "/api/v1/projects/PROJ/tasks/AGT-1/history")]
    [InlineData("GET", "/api/v1/projects/PROJ/tasks/AGT-1/move")]
    [InlineData("PUT", "/api/v1/projects/PROJ/tasks/AGT-1/move")]
    [InlineData("POST", "/api/v1/projects/PROJ/tasks/AGT-1/state")]
    [InlineData("DELETE", "/api/v1/workspaces")]
    [InlineData("POST", "/api/v1/studio/board")]
    [InlineData("GET", "/api/v1/studio/auth/login")]
    [InlineData("POST", "/api/v1/studio/auth/status")]
    [InlineData("POST", "/api/v1/studio/orchestrator/context/global")]
    [InlineData("POST", "/api/v1/studio/orchestrator/sessions/project:PROJ/turns")]
    [InlineData("PUT", "/api/v1/studio/runner/PROJ/mode")]
    [InlineData("POST", "/api/v1/studio/runner/PROJ/orchestrator-chat/attachments")]
    [InlineData("GET", "/api/v1/studio/search")]
    [InlineData("GET", "/api/tasks/grouped")]
    [InlineData("GET", "/hubs/jobs")]
    [InlineData("GET", "/hubs/events")]
    public void Every_other_route_is_left_to_its_owner(string method, string path)
        => Assert.Null(StudioV1LegacyRouteAlias.Resolve(method, path));

    [Fact]
    public async Task Local_profile_serves_the_versioned_core_attach_paths_from_the_legacy_handlers()
    {
        using var factory = BuildFactory(taskServerBaseUrl: null);
        using var browser = CreateClient(factory);

        await AssertSameBodyAsync(browser, "/api/v1/studio/auth/status", "/api/auth/status");
        var bootstrap = await browser.PostAsJsonAsync("/api/v1/studio/auth/bootstrap", new
        {
            username = "local.owner",
            password = "correct horse battery staple!",
            displayName = "Local Owner",
        });
        bootstrap.EnsureSuccessStatusCode();
        var login = await browser.PostAsJsonAsync(
            "/api/v1/studio/auth/login", new { username = "local.owner", password = "correct horse battery staple!" });
        login.EnsureSuccessStatusCode();
        Assert.True((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authenticated").GetBoolean());

        foreach (var path in new[]
                 {
                     "/api/v1/studio/board",
                     "/api/v1/workspaces",
                     "/api/v1/projects",
                     "/api/v1/studio/runner/status",
                     "/api/v1/studio/orchestrator/sessions",
                 })
        {
            var response = await browser.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}.");
        }

        var negotiate = await browser.PostAsync("/hubs/v1/studio/negotiate?negotiateVersion=1", null);
        negotiate.EnsureSuccessStatusCode();
        Assert.True((await negotiate.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("connectionToken", out _));
    }

    [Fact]
    public async Task Proxy_profile_keeps_core_attach_local_and_forwards_every_other_v1_route()
    {
        var upstream = new RecordingUpstream();
        using var factory = BuildFactory(taskServerBaseUrl: "http://task-server.invalid", upstream);
        using var browser = CreateClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/studio/board")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/studio/auth/status")).StatusCode);
        Assert.DoesNotContain(upstream.Paths, path => path.Contains("/studio/", StringComparison.Ordinal));

        var proxied = await browser.GetAsync("/api/v1/protocol");
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);
        Assert.Contains("/api/v1/protocol", upstream.Paths);
    }

    private static async Task AssertSameBodyAsync(HttpClient client, string versioned, string legacy)
    {
        var fromVersioned = await client.GetStringAsync(versioned);
        var fromLegacy = await client.GetStringAsync(legacy);
        Assert.Equal(fromLegacy, fromVersioned);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private WebApplicationFactory<Program> BuildFactory(string? taskServerBaseUrl, RecordingUpstream? upstream = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            // Program registers the proxy client before these configuration
            // overrides are visible, so the test sets its origin directly.
            if (upstream is not null)
                builder.ConfigureTestServices(services => services
                    .AddHttpClient(TaskServerPlaneProxy.ClientName)
                    .ConfigureHttpClient(client => client.BaseAddress = new Uri(taskServerBaseUrl!.TrimEnd('/') + "/"))
                    .ConfigurePrimaryHttpMessageHandler(() => upstream));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "local",
                ["TaskServer:BaseUrl"] = taskServerBaseUrl ?? string.Empty,
            }));
        });

    /// <summary>Stands in for the standalone Task Server behind the transitional proxy.</summary>
    private sealed class RecordingUpstream : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { proxied = true }) });
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
