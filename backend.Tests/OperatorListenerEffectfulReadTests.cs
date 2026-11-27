using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperatorListenerEffectfulReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-effects-" + Guid.NewGuid().ToString("N"));
    private readonly FakeUpdateProcess _process = new();

    [Theory]
    [InlineData("POST", "/api/devtools/update-stable", null, "operator-listener-required")]
    [InlineData("POST", "/api/devtools/update-stable", "https://foreign.test", "cross-origin-denied")]
    [InlineData("GET", "/api/cli/_probe/invalid", null, "operator-listener-required")]
    [InlineData("GET", "/api/cli/_probe/invalid", "https://foreign.test", "cross-origin-denied")]
    [InlineData("GET", "/api/cli/_probe/claude", null, "operator-listener-required")]
    [InlineData("GET", "/api/cli/_probe/claude", "https://foreign.test", "cross-origin-denied")]
    [InlineData("GET", "/api/devtools/fixtures/scan?apply=true", null, "operator-listener-required")]
    [InlineData("GET", "/api/devtools/fixtures/scan?apply=true", "https://foreign.test", "cross-origin-denied")]
    [InlineData("GET", "/api/cli/claude/models", null, "operator-listener-required")]
    [InlineData("GET", "/api/cli/claude/models", "https://foreign.test", "cross-origin-denied")]
    [InlineData("GET", "/api/cli/model-routing/recommendation?taskType=coding&cliType=claude", null, "operator-listener-required")]
    [InlineData("GET", "/api/cli/model-routing/recommendation?taskType=coding&cliType=claude", "https://foreign.test", "cross-origin-denied")]
    [InlineData("GET", "/api/cli/quota", null, "operator-listener-required")]
    [InlineData("GET", "/api/cli/quota", "https://foreign.test", "cross-origin-denied")]
    public async Task Tunnel_cannot_start_local_processes(string method, string path, string? origin, string error)
    {
        using var factory = Factory();
        using var client = Client(factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (origin is not null) request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _process.Starts);
    }

    [Theory]
    [InlineData("POST", "/api/devtools/update-stable")]
    [InlineData("GET", "/api/cli/_probe/claude")]
    [InlineData("GET", "/api/devtools/fixtures/scan?apply=true")]
    public async Task Operator_port_still_refuses_foreign_origins(string method, string path)
    {
        using var factory = Factory();
        using var client = Client(factory, operatorPort: true);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", "https://foreign.test");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("cross-origin-denied", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _process.Starts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://foreign.test")]
    public async Task Get_without_a_previous_operation_never_starts_an_update(string? origin)
    {
        using var factory = Factory();
        using var client = Client(factory, registered: false);
        if (origin is not null) client.DefaultRequestHeaders.Add("Origin", origin);
        using var response = await client.GetAsync("/api/devtools/update-stable/stream");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("update-stable-not-started", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _process.Starts);
    }

    [Fact]
    public async Task Authorized_post_starts_once_and_gets_only_subscribe_even_after_disconnect()
    {
        using var factory = Factory();
        using var owner = Client(factory, operatorPort: true);
        owner.DefaultRequestHeaders.Add("Origin", "http://localhost:4010");
        using var reader = Client(factory, registered: false);
        using var started = await owner.PostAsync("/api/devtools/update-stable", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var streamUrl = Assert.IsType<Uri>(started.Headers.Location).ToString();
        Assert.Equal(1, _process.Starts);

        using var duplicate = await owner.PostAsync("/api/devtools/update-stable", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, _process.Starts);

        using (var cancelled = new CancellationTokenSource())
        using (var subscription = await reader.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, cancelled.Token))
        {
            Assert.Equal(HttpStatusCode.OK, subscription.StatusCode);
            using var lines = new StreamReader(await subscription.Content.ReadAsStreamAsync(cancelled.Token));
            Assert.Equal("event: log", await lines.ReadLineAsync(cancelled.Token));
            cancelled.Cancel();
        }
        Assert.False(_process.HasExited);
        _process.Finish();
        var replay = await reader.GetStringAsync(streamUrl);
        Assert.Contains("event: stdout\ndata: fake update completed", replay);
        Assert.Contains("event: done\ndata: exit code 0", replay);
        Assert.Equal(replay, await reader.GetStringAsync(streamUrl));
        // The legacy stream URL subscribes to the latest authorized operation.
        Assert.Equal(replay, await reader.GetStringAsync("/api/devtools/update-stable/stream"));
        Assert.Equal(1, _process.Starts);
        using var unknown = await reader.GetAsync("/api/devtools/update-stable/stream?operationId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Trigger_requires_registered_client_attribution()
    {
        using var factory = Factory();
        using var client = Client(factory, operatorPort: true, registered: false);
        using var response = await client.PostAsync("/api/devtools/update-stable", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("client-unknown", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _process.Starts);
    }

    [Theory]
    [InlineData("POST", "/api/devtools/update-stable")]
    [InlineData("GET", "/api/devtools/update-stable/stream")]
    public async Task Feature_flag_still_gates_trigger_and_stream(string method, string path)
    {
        using var factory = Factory(enabled: false);
        using var client = Client(factory, operatorPort: true);
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("DevTools:UpdateStableEnabled is false", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _process.Starts);
    }

    [Theory]
    [InlineData("/api/cli/types")]
    [InlineData("/api/cli/quota/history?cli=claude")]
    public async Task Read_only_cli_routes_remain_available_on_the_tunnel(string path)
    {
        using var factory = Factory();
        using var client = Client(factory, registered: false);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _process.Starts);
    }

    [Theory]
    [InlineData("GET", IamPermissions.WorkspaceRead)]
    [InlineData("POST", IamPermissions.WorkspaceRead)]
    [InlineData("GET", IamPermissions.RunnerService)]
    public void Effect_metadata_overrides_read_and_host_write_exemptions(string method, string permission)
    {
        Assert.Equal("operator-listener-required", OperatorListener.Decide(method, "/api/example", true,
            permission, 5000, 5999, null, localEffect: true)?.Code);
        Assert.Equal("cross-origin-denied", OperatorListener.Decide(method, "/api/example", true,
            permission, 5999, null, "https://foreign.test", localEffect: true)?.Code);
        Assert.Null(OperatorListener.Decide(method, "/api/example", true,
            permission, 5999, 5999, "http://localhost:4010", localEffect: true));
    }

    private WebApplicationFactory<Program> Factory(bool enabled = true)
    {
        Directory.CreateDirectory(_root);
        var script = Path.Combine(_root, "fake-update.sh");
        File.WriteAllText(script, "# Fake runner boundary; this file is never executed.\n");
        return new TestHostFactory().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Test");
            host.UseTestSettings(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = SecurityProfiles.Local,
                [OperatorListener.PortSetting] = "5999",
                ["DevTools:UpdateStableEnabled"] = enabled ? "true" : "false",
                ["DevTools:UpdateStableScriptPath"] = script,
            });
            host.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.AddSingleton<IUpdateStableProcessRunner>(_process);
                services.AddSingleton<IStartupFilter>(new TestPortFilter());
            });
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, bool operatorPort = false, bool registered = true)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Local-Port", operatorPort ? "5999" : "5000");
        if (registered) client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    // TestServer has no socket. Model the Kestrel connection port before the
    // product middleware, without adding any header trust to production code.
    private sealed class TestPortFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.LocalPort = context.Request.Headers["X-Test-Local-Port"] == "5999" ? 5999 : 5000;
                return continuation(context);
            });
            next(app);
        };
    }

    private sealed class FakeUpdateProcess : IUpdateStableProcessRunner
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }
        public bool HasExited { get; private set; }
        public void Finish() => _exit.TrySetResult();

        public async Task<int> RunAsync(string scriptPath, Action<string, string> output)
        {
            Starts++;
            output("log", "$ fake update-stable.sh");
            await _exit.Task;
            output("stdout", "fake update completed");
            HasExited = true;
            return 0;
        }
    }

    public void Dispose()
    {
        _process.Finish();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
