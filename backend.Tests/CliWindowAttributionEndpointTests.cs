using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CliWindowAttributionEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cli-window-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Route_uses_reset_interval_and_hides_unattributed_rows_from_project_scoped_user()
    {
        Directory.CreateDirectory(_root);
        var aggregator = DispatchProxy.Create<ITokenAggregator, AttributionProxy>();
        var capture = (AttributionProxy)(object)aggregator;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<ITokenAggregator>();
                services.AddSingleton(aggregator);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"), HandleCookies = false,
        });
        var access = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = access.Bootstrap(new BootstrapRequest("window.owner", "correct horse battery staple!", null));
        var projectPath = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectPath);
        var project = factory.Services.GetRequiredService<ProjectRegistry>()
            .EnsureProjectForStorage(projectPath, "Window Project", DefaultWorkspace.Id);
        var user = access.CreateUser(new CreateUserRequest("window.scoped", "Scoped",
            StudioRoles.Operator, [project.Id], "temporary scoped phrase!"), owner.User.Id);
        var login = access.Login(user.User.Username, user.TemporaryPassword, "window|127.0.0.1");
        access.ChangePassword(new HumanPrincipal(login.User,
                access.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(user.TemporaryPassword, "new scoped password phrase!"));
        var bridge = new AgentMessageBusBridge(factory.Services.GetRequiredService<AgentMessageBusStore>(),
            factory.Services.GetRequiredService<IConfiguration>(), NullLogger<AgentMessageBusBridge>.Instance);
        var now = _clock.GetUtcNow().UtcDateTime;
        await bridge.EmitTokenUsageAsync(null, null, "agent:codex", "token-receipt",
            new OrchestratorTokenUsage { CliType = "codex", Model = "gpt", InputTokens = 99 }, now.AddMinutes(-2));

        async Task<JsonElement> Get(string query, string session)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/workspace/tokens/cli-window?cliType=codex&label=5-hour" + query);
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var futureReset = now.AddHours(2);
        var future = await Get("&resetAt=" + Uri.EscapeDataString(futureReset.ToString("O")), owner.SessionToken);
        Assert.Equal(77, future.GetProperty("totalTokens").GetInt64());
        Assert.NotEmpty(capture.Usage!.Unattributed);
        Assert.InRange((capture.From - futureReset.AddHours(-5)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(now, capture.To);

        var pastReset = now.AddMinutes(-30);
        await Get("&resetAt=" + Uri.EscapeDataString(pastReset.ToString("O")), owner.SessionToken);
        Assert.InRange((capture.From - pastReset.AddHours(-5)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.InRange((capture.To - pastReset).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await Get("", owner.SessionToken);
        Assert.Equal(now, capture.To);
        Assert.Equal(now.AddHours(-5), capture.From);

        await Get("", login.SessionToken);
        Assert.Equal(["Window Project"], capture.Projects);
        Assert.Empty(capture.Usage!.Unattributed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    public class AttributionProxy : DispatchProxy
    {
        public DateTime From { get; private set; }
        public DateTime To { get; private set; }
        public string[] Projects { get; private set; } = [];
        public WorkspaceBusUsage? Usage { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ITokenAggregator.CliWindowAttribution))
                throw new NotSupportedException(targetMethod?.Name);
            Projects = ((IEnumerable<(string Name, string WatchPath)>)args![0]!)
                .Select(project => project.Name).ToArray();
            From = (DateTime)args[2]!;
            To = (DateTime)args[3]!;
            Usage = (WorkspaceBusUsage?)args[4];
            return new CliWindowAttribution(1, 77, 0, 0, 0);
        }
    }
}
