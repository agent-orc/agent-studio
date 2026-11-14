extern alias UpdSvc;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using BranchVersion = UpdSvc::AgentTaskboard.UpdateService.BranchVersion;
using CommitInfo = UpdSvc::AgentTaskboard.UpdateService.CommitInfo;
using IGitProbe = UpdSvc::AgentTaskboard.UpdateService.IGitProbe;
using PeriodicProbeService = UpdSvc::AgentTaskboard.UpdateService.PeriodicProbeService;
using TriggerToken = UpdSvc::AgentTaskboard.UpdateService.TriggerToken;
using TriggerTokenRequired = UpdSvc::AgentTaskboard.UpdateService.TriggerTokenRequired;
using UpdateServiceOptions = UpdSvc::AgentTaskboard.UpdateService.UpdateServiceOptions;
using UpdateStatusStore = UpdSvc::AgentTaskboard.UpdateService.UpdateStatusStore;
using InstallationPlacement = UpdSvc::AgentTaskboard.UpdateService.Installation.InstallationPlacement;
using VersionTopology = UpdSvc::AgentTaskboard.UpdateService.VersionTopology;

namespace AgentStudio.Tests;

/// <summary>
/// Hardening of the local Update Service: every state-changing route demands
/// the trigger token, a missing token is generated owner-only on first start,
/// CORS grants only the configured Studio origins without credentials, and a
/// forced trigger is accepted only with the token and logged with its caller.
/// The host runs in-process with an installed placement, so an authorized
/// trigger is answered without touching git or the update scripts. Tokens are
/// generated per test; none is a real credential.
/// </summary>
public sealed class UpdateServiceTriggerTokenTests : IDisposable
{
    private const string StudioOrigin = "http://127.0.0.1:4011";
    private const string ForeignOrigin = "http://198.51.100.7:8080";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "update-token-" + Guid.NewGuid().ToString("N"));

    public UpdateServiceTriggerTokenTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string TokenFile => Path.Combine(_root, "config", ".update-token");

    private static string FixtureToken() => "fixture-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    [Fact]
    public async Task Without_a_configured_token_the_first_start_generates_one_and_requires_it()
    {
        Assert.False(File.Exists(TokenFile));
        using var host = new Host(this, configuredToken: null);
        using var client = host.CreateClient();

        Assert.True(File.Exists(TokenFile));
        var generated = File.ReadAllText(TokenFile).Trim();
        Assert.True(generated.Length >= 40, "the generated token carries at least 32 random bytes");
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenFile));

        using (var missing = await client.PostAsJsonAsync("/update/trigger", new { Reason = "manual", Force = false }))
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var accepted = await Post(client, "/update/trigger", new { Reason = "manual", Force = false }, generated);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task A_restart_reuses_the_stored_token()
    {
        string first;
        using (var host = new Host(this, configuredToken: null))
        {
            using var _ = host.CreateClient();
            first = File.ReadAllText(TokenFile).Trim();
        }

        using var restarted = new Host(this, configuredToken: null);
        using var client = restarted.CreateClient();
        Assert.Equal(first, File.ReadAllText(TokenFile).Trim());
        using var accepted = await Post(client, "/update/trigger", new { Reason = "manual" }, first);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Theory]
    [InlineData("/update/trigger")]
    [InlineData("/update/rollback")]
    public async Task Missing_or_wrong_token_is_refused_on_every_mutating_route(string route)
    {
        var token = FixtureToken();
        using var host = new Host(this, configuredToken: token);
        using var client = host.CreateClient();
        object body = route.EndsWith("rollback", StringComparison.Ordinal) ? new { RunId = "run-1" } : new { Reason = "manual" };

        using (var missing = await Post(client, route, body, token: null))
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using (var wrong = await Post(client, route, body, FixtureToken()))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using (var prefix = await Post(client, route, body, token[..^1]))
            Assert.Equal(HttpStatusCode.Unauthorized, prefix.StatusCode);
        using (var accepted = await Post(client, route, body, token))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task Force_without_the_token_is_refused_and_an_authorized_force_is_logged_with_its_caller()
    {
        var token = FixtureToken();
        using var host = new Host(this, configuredToken: token);
        using var client = host.CreateClient();

        using (var refused = await Post(client, "/update/trigger", new { Reason = "scheduled", Force = true }, token: null))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.DoesNotContain(host.Logs, line => line.Contains("Forced update trigger", StringComparison.Ordinal));
        Assert.Contains(host.Logs, line => line.Contains("Refused POST /update/trigger", StringComparison.Ordinal));

        using var accepted = await Post(client, "/update/trigger", new { Reason = "scheduled", Force = true }, token,
            userAgent: "update-token-test");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var forced = Assert.Single(host.Logs, line => line.Contains("Forced update trigger", StringComparison.Ordinal));
        Assert.Contains("userAgent=update-token-test", forced, StringComparison.Ordinal);
        Assert.Contains("trigger=scheduled", forced, StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join('\n', host.Logs), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Guard")]
    public void Every_state_changing_route_requires_the_trigger_token()
    {
        using var host = new Host(this, configuredToken: FixtureToken());
        using var _ = host.CreateClient();
        var mutating = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Any(method => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)) ?? true)
            .ToList();

        Assert.Contains(mutating, endpoint => endpoint.RoutePattern.RawText == "/update/trigger");
        Assert.Contains(mutating, endpoint => endpoint.RoutePattern.RawText == "/update/rollback");
        var unguarded = mutating.Where(endpoint => endpoint.Metadata.GetMetadata<TriggerTokenRequired>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText).ToList();
        Assert.True(unguarded.Count == 0, "Mutating routes without RequireTriggerToken: " + string.Join(", ", unguarded));
    }

    [Fact]
    public async Task A_foreign_origin_gets_no_cors_grant_and_the_studio_origin_gets_one_without_credentials()
    {
        using var host = new Host(this, configuredToken: FixtureToken());
        using var client = host.CreateClient();

        using (var foreign = await Preflight(client, ForeignOrigin))
        {
            Assert.False(foreign.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(foreign.Headers.Contains("Access-Control-Allow-Credentials"));
        }
        using (var foreignRead = await Get(client, "/update/status", ForeignOrigin))
            Assert.False(foreignRead.Headers.Contains("Access-Control-Allow-Origin"));

        using var studio = await Preflight(client, StudioOrigin);
        Assert.Equal(StudioOrigin, Assert.Single(studio.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(TriggerToken.HeaderName,
            string.Join(',', studio.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
        Assert.False(studio.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task An_empty_origin_setting_grants_no_origin()
    {
        using var host = new Host(this, configuredToken: FixtureToken(), allowedOrigins: "");
        using var client = host.CreateClient();

        using var studio = await Preflight(client, StudioOrigin);
        Assert.False(studio.Headers.Contains("Access-Control-Allow-Origin"));
        using var read = await Get(client, "/update/status", StudioOrigin);
        Assert.False(read.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string route, object body, string? token, string? userAgent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Add(TriggerToken.HeaderName, token);
        if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Preflight(HttpClient client, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/update/trigger");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type," + TriggerToken.HeaderName.ToLowerInvariant());
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string route, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    private sealed class Host(UpdateServiceTriggerTokenTests test, string? configuredToken, string? allowedOrigins = null)
        : WebApplicationFactory<UpdSvc::Program>
    {
        private readonly ConcurrentQueue<string> _logs = new();

        public IReadOnlyList<string> Logs => _logs.ToArray();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
            builder.ConfigureServices(services =>
            {
                var periodicProbe = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PeriodicProbeService));
                if (periodicProbe is not null) services.Remove(periodicProbe);
                // Program binds its options before the factory's configuration
                // applies, so the test options replace the singleton, as in
                // UpdateServiceTestFactory.
                var options = Options();
                services.RemoveAll<UpdateServiceOptions>();
                services.AddSingleton(options);
                services.RemoveAll<IGitProbe>();
                services.AddSingleton<IGitProbe, NoGitProbe>();
                services.RemoveAll<UpdateStatusStore>();
                services.AddSingleton(sp => new UpdateStatusStore(options.HistoryFile, "0000000", () => "unknown",
                    sp.GetRequiredService<ILogger<UpdateStatusStore>>(), options.Mode));
            });
            return base.CreateHost(builder);
        }

        private UpdateServiceOptions Options()
        {
            var options = new UpdateServiceOptions
            {
                ListenUrl = "http://127.0.0.1:0",
                StableCheckoutDir = test._root,
                DevspaceDir = test._root,
                HistoryFile = Path.Combine(test._root, "history.jsonl"),
                RunsDirectory = Path.Combine(test._root, "runs"),
                VersionFile = Path.Combine(test._root, "VERSION"),
                Placement = InstallationPlacement.InstalledCompose,
                TriggerTokenFile = test.TokenFile,
                TriggerToken = configuredToken,
            };
            if (allowedOrigins is not null) options.AllowedOrigins = allowedOrigins;
            return options;
        }
    }

    private sealed class NoGitProbe : IGitProbe
    {
        public string HeadShort() => "0000000";
        public (string Origin, int BehindBy) FetchAndCompare() => ("0000000", 0);
        public IReadOnlyList<CommitInfo> PendingCommits(int max = 50) => [];
        public VersionTopology ReadVersionTopology(string runningCommit)
            => new(new BranchVersion("main", runningCommit, null, 0, 0), new BranchVersion("develop", runningCommit, null, 0, 0));
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capturing(sink);
        public void Dispose() { }

        private sealed class Capturing(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue(formatter(state, exception));
        }
    }
}
