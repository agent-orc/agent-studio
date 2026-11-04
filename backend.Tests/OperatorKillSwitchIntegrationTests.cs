extern alias OperationsServerAssembly;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Security;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using OperationsHost = OperationsServerAssembly::AgentStudio.Operations.Server.OperationsServerHost;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-2 acceptance across the Studio API and the Operations
/// Server, both hosted in process: after the Owner's kill switch every
/// operator request is refused on both within 60 seconds, a per-session kill
/// refuses only that session, and every use lands in the audit trail with its
/// own event type.
/// </summary>
public sealed class OperatorKillSwitchIntegrationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple!";
    private static readonly TimeSpan RefusalBudget = TimeSpan.FromSeconds(60);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-kill-" + Guid.NewGuid().ToString("N"));
    private string _ownerTotpSecret = string.Empty;
    // Credential-shaped values are built at runtime, never written as literals.
    private readonly string _revokerToken = "ops-" + Guid.NewGuid().ToString("N");
    private readonly string _operatorOpsToken = "ops-" + Guid.NewGuid().ToString("N");
    private readonly string _hostAgentToken = "ops-" + Guid.NewGuid().ToString("N");
    private WebApplication _operations = null!;

    private string OperationsDirectory => Path.Combine(_workspace + "-operations");
    private string PrincipalsFile => Path.Combine(OperationsDirectory, "principals.json");
    private string RevokerCredentialFile => Path.Combine(OperationsDirectory, "control-plane.token");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(OperationsDirectory);
        File.WriteAllText(RevokerCredentialFile, _revokerToken);
        SavePrincipals(operatorAgentId: "not-created-yet");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:PrincipalsFile"] = PrincipalsFile,
            ["Operations:DataDirectory"] = OperationsDirectory,
        });
        OperationsHost.AddOperationsServer(builder.Services, builder.Configuration);
        _operations = builder.Build();
        OperationsHost.MapOperationsServer(_operations);
        await _operations.StartAsync();
    }

    [Fact]
    public async Task After_the_kill_switch_every_operator_request_is_refused_on_studio_and_operations_server_within_60_seconds()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        using var owner = await OwnerClientAsync(factory);
        var agentId = (await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/operator-agents", new
        {
            name = "ops console",
            bindings = new[] { new { role = "developer", scope = new { kind = "project", project = "alpha" } } },
        })).GetProperty("id").GetString()!;
        SavePrincipals(agentId);
        var tokens = new List<string>();
        for (var index = 0; index < 2; index++)
            tokens.Add((await SendAsync(owner, HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions",
                new { name = $"session {index}" })).GetProperty("token").GetString()!);
        var studioClients = tokens.Select(token => BearerClient(factory, token)).ToList();
        using var operatorOps = OperationsClient(_operatorOpsToken);
        using var hostAgent = OperationsClient(_hostAgentToken);

        // Before: every session and the operator's Operations Server principal work.
        foreach (var client in studioClients)
        {
            using var before = await client.GetAsync("/api/tasks/alpha-card");
            Assert.True(before.StatusCode == HttpStatusCode.OK, await before.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await operatorOps.GetAsync("/api/operations/v1/catalogue")).StatusCode);
        (await hostAgent.PostAsJsonAsync("/api/operations/v1/agents/register", new AgentCapability(
            "host-a", "boot-a", "diagnostics", [], [], new() { ["host.inspect"] = 1 }, 1, 1, 1))).EnsureSuccessStatusCode();
        var input = JsonSerializer.SerializeToElement(new { });
        using (var submitted = await operatorOps.PostAsJsonAsync("/api/operations/v1/commands", new OperationCommand(
                   "inspect-1", "operator", "host-a", "host.inspect", 1, input, OperationsProtocol.Digest(input),
                   _operations.Services.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(5), "correlation"), OperationsProtocol.Json))
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var clock = Stopwatch.StartNew();
        var killed = await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/operator-kill-switch/end-all",
            new { reason = "Drill of the acceptance test" });
        Assert.Equal("all", killed.GetProperty("kind").GetString());
        Assert.Equal("studio", killed.GetProperty("trigger").GetString());

        var studioRefusedAt = await RefusedWithinAsync(async () =>
        {
            foreach (var client in studioClients)
                if ((await client.GetAsync("/api/tasks/alpha-card")).StatusCode != HttpStatusCode.Unauthorized) return false;
            return true;
        }, clock);
        var operationsRefusedAt = await RefusedWithinAsync(async () =>
            (await operatorOps.GetAsync("/api/operations/v1/catalogue")).StatusCode == HttpStatusCode.Unauthorized, clock);
        output.WriteLine($"Kill switch: Studio refused every session after {studioRefusedAt.TotalMilliseconds:0} ms, "
            + $"the Operations Server refused the operator principal after {operationsRefusedAt.TotalMilliseconds:0} ms.");
        Assert.True(studioRefusedAt < RefusalBudget, $"Studio refused after {studioRefusedAt}.");
        Assert.True(operationsRefusedAt < RefusalBudget, $"Operations Server refused after {operationsRefusedAt}.");

        // The queued operator operation is cancelled, so the host agent receives nothing.
        using (var poll = await hostAgent.PostAsJsonAsync("/api/operations/v1/agents/host-a/poll", new AgentPollRequest("boot-a")))
        {
            poll.EnsureSuccessStatusCode();
            Assert.True(poll.StatusCode == HttpStatusCode.NoContent
                || await poll.Content.ReadFromJsonAsync<OperationAttempt?>(OperationsProtocol.Json) is null);
        }
        // Only the operator principal is on the revocation list; the principal file itself is never written.
        var revocations = JsonDocument.Parse(File.ReadAllText(Path.Combine(OperationsDirectory, "operator-revocations.json"))).RootElement;
        Assert.Equal(["operator-console"], revocations.GetProperty("revokedPrincipalIds").EnumerateArray().Select(id => id.GetString()!).ToArray());
        Assert.Equal([agentId], revocations.GetProperty("revokedOperatorAgentIds").EnumerateArray().Select(id => id.GetString()!).ToArray());
        Assert.DoesNotContain("\"revoked\":true", File.ReadAllText(PrincipalsFile), StringComparison.Ordinal);
        using (var control = OperationsClient(_hostAgentToken))
            Assert.Equal(HttpStatusCode.Forbidden, (await control.GetAsync("/api/operations/v1/catalogue")).StatusCode);

        // The Owner cannot start a new session for the disabled principal.
        using (var restart = await SendRawAsync(owner, HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions", new { }))
            Assert.Equal(HttpStatusCode.NotFound, restart.StatusCode);

        var entries = store.ControlPlaneAudit.ReadNewest(entry => entry.Operation.StartsWith("operator-kill.", StringComparison.Ordinal), 10);
        var kill = Assert.Single(entries, entry => entry.Operation == "operator-kill.all");
        Assert.Equal("ok", kill.Outcome);
        Assert.StartsWith("human:", kill.Actor);
        Assert.Contains(entries, entry => entry.Operation == "operator-kill.operations-server" && entry.Outcome == "ok");
        Assert.True(store.ControlPlaneAudit.VerifyChain().Ok);

        // The Owner notice reached the operator feed as one acute event.
        var notice = Assert.Single(factory.Services.GetRequiredService<AgentStudio.Bus.AgentMessageBusStore>()
            .Recent(_workspace, null, 500), message => message.Topic == "operator_kill_switch");
        Assert.Equal("High", notice.Severity);

        var status = await owner.GetFromJsonAsync<JsonElement>("/api/auth/iam/operator-kill-switch");
        var last = status.GetProperty("kills")[0];
        Assert.Equal("all", last.GetProperty("kind").GetString());
        Assert.Equal("revoked", last.GetProperty("operationsServer").GetProperty("state").GetString());
        Assert.Equal(1, last.GetProperty("operationsServer").GetProperty("cancelledOperations").GetInt32());
        foreach (var client in studioClients) client.Dispose();
    }

    [Fact]
    public async Task A_per_session_kill_refuses_that_session_while_another_session_of_the_principal_keeps_working()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        using var owner = await OwnerClientAsync(factory);
        var agentId = (await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/operator-agents", new
        {
            name = "ops console",
            bindings = new[] { new { role = "developer", scope = new { kind = "project", project = "alpha" } } },
        })).GetProperty("id").GetString()!;
        SavePrincipals(agentId);
        var runaway = await SendAsync(owner, HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions", new { name = "runaway" });
        var healthy = await SendAsync(owner, HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions", new { name = "healthy" });
        using var runawayClient = BearerClient(factory, runaway.GetProperty("token").GetString()!);
        using var healthyClient = BearerClient(factory, healthy.GetProperty("token").GetString()!);
        using var operatorOps = OperationsClient(_operatorOpsToken);
        Assert.Equal(HttpStatusCode.OK, (await runawayClient.GetAsync("/api/tasks/alpha-card")).StatusCode);

        var clock = Stopwatch.StartNew();
        var runawayId = runaway.GetProperty("sessionId").GetString()!;
        var killed = await SendAsync(owner, HttpMethod.Post,
            $"/api/auth/iam/operator-agents/{agentId}/sessions/{runawayId}/kill", new { reason = "Runaway loop" });
        Assert.Equal("session", killed.GetProperty("kind").GetString());

        var refusedAt = await RefusedWithinAsync(async () =>
            (await runawayClient.GetAsync("/api/tasks/alpha-card")).StatusCode == HttpStatusCode.Unauthorized, clock);
        output.WriteLine($"Per-session kill: the killed session was refused after {refusedAt.TotalMilliseconds:0} ms.");
        Assert.True(refusedAt < RefusalBudget, $"The killed session was refused after {refusedAt}.");
        Assert.Equal(HttpStatusCode.OK, (await healthyClient.GetAsync("/api/tasks/alpha-card")).StatusCode);
        // The principal stays enabled, so its Operations Server principal keeps working.
        Assert.Equal(HttpStatusCode.OK, (await operatorOps.GetAsync("/api/operations/v1/catalogue")).StatusCode);

        var entry = Assert.Single(store.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == "operator-kill.session", 10));
        Assert.Equal($"operator-agent:{agentId}", entry.Target);
        Assert.Contains(runawayId, entry.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_out_of_band_command_works_with_studio_stopped_and_writes_the_same_audit_event()
    {
        string agentId;
        string token;
        using (var factory = BuildFactory())
        {
            using var owner = await OwnerClientAsync(factory);
            agentId = (await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/operator-agents", new
            {
                name = "ops console",
                bindings = new[] { new { role = "developer", scope = new { kind = "project", project = "alpha" } } },
            })).GetProperty("id").GetString()!;
            SavePrincipals(agentId);
            token = (await SendAsync(owner, HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions", new { }))
                .GetProperty("token").GetString()!;
            using var before = BearerClient(factory, token);
            Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/api/tasks/alpha-card")).StatusCode);
        }
        using var operatorOps = OperationsClient(_operatorOpsToken);
        Assert.Equal(HttpStatusCode.OK, (await operatorOps.GetAsync("/api/operations/v1/catalogue")).StatusCode);

        // Studio is stopped. The command on the control plane host applies the kill itself. The in-process
        // Operations Server is not reachable from it, so the host revocation stays pending for the API.
        var output = new StringWriter();
        var exit = await OperatorKillSwitchCommand.RunAsync(["--reason", "Studio unreachable"], output, StudioConfiguration());
        Assert.Equal(OperatorKillSwitchCommand.ExitOperationsServerPending, exit);
        Assert.Contains("The Studio API is stopped", output.ToString(), StringComparison.Ordinal);
        var offline = new AccessSecurityStore(StudioConfiguration(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AccessSecurityStore>.Instance);
        var entry = Assert.Single(offline.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == "operator-kill.all", 10));
        Assert.Equal(("system:control-plane-host", "ok", "operator-agents:all"), (entry.Actor, entry.Outcome, entry.Target));
        Assert.Contains("out-of-band", entry.Raw, StringComparison.Ordinal);
        Assert.Contains("Studio unreachable", entry.Raw, StringComparison.Ordinal);
        Assert.Null(offline.AuthenticateServiceAccount(token));

        // Studio starts again: it enforces the latch, finishes the host revocation, and notifies the Owner.
        using var restarted = BuildFactory(bootstrap: false);
        using var after = BearerClient(restarted, token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/api/tasks/alpha-card")).StatusCode);
        var clock = Stopwatch.StartNew();
        var refusedAt = await RefusedWithinAsync(async () =>
            (await operatorOps.GetAsync("/api/operations/v1/catalogue")).StatusCode == HttpStatusCode.Unauthorized, clock);
        output.WriteLine($"Out of band: after the API restart the Operations Server refused after {refusedAt.TotalMilliseconds:0} ms.");
        Assert.True(refusedAt < RefusalBudget, $"Operations Server refused after {refusedAt}.");
        // The Owner's view reads this log; a second sign-in would reuse the TOTP step of the first.
        var log = restarted.Services.GetRequiredService<OperatorKillSwitch>().Log;
        OperatorKillRecord? record = null;
        for (var attempt = 0; attempt < 100 && (record?.NotifiedAt is null || record.OperationsServer.State != "revoked"); attempt++)
        {
            record = log.Read().Kills.Single();
            if (record.NotifiedAt is null || record.OperationsServer.State != "revoked") await Task.Delay(100);
        }
        Assert.Equal(("out-of-band", "revoked"), (record!.Trigger, record.OperationsServer.State));
        Assert.NotNull(record.NotifiedAt);
        var store = restarted.Services.GetRequiredService<AccessSecurityStore>();
        Assert.False(store.OperatorKillRequested);
        Assert.Single(store.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == "operator-kill.all", 10));
        Assert.True(store.ControlPlaneAudit.VerifyChain().Ok);
    }

    [Fact]
    public async Task A_drill_run_produces_a_recorded_duration_and_passes()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        using var owner = await OwnerClientAsync(factory);
        var agentId = (await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/operator-agents", new
        {
            name = "ops console",
            bindings = new[] { new { role = "developer", scope = new { kind = "project", project = "alpha" } } },
        })).GetProperty("id").GetString()!;
        var token = (await SendAsync(owner, HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions", new { }))
            .GetProperty("token").GetString()!;
        using var real = BearerClient(factory, token);

        var drill = await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/operator-kill-switch/drills", null);

        Assert.True(drill.GetProperty("passed").GetBoolean(), drill.ToString());
        Assert.Equal("manual-drill", drill.GetProperty("trigger").GetString());
        Assert.Equal("revoked", drill.GetProperty("operationsServerState").GetString());
        var duration = drill.GetProperty("durationMs").GetDouble();
        output.WriteLine($"Drill: passed in {duration} ms (Studio {drill.GetProperty("studioRefusedMs")} ms, "
            + $"Operations Server {drill.GetProperty("operationsServerMs")} ms).");
        Assert.InRange(duration, 0, RefusalBudget.TotalMilliseconds);
        Assert.True(drill.GetProperty("otherSessionsKept").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await real.GetAsync("/api/tasks/alpha-card")).StatusCode);

        var status = await owner.GetFromJsonAsync<JsonElement>("/api/auth/iam/operator-kill-switch");
        Assert.Equal(drill.GetProperty("id").GetString(), status.GetProperty("drills")[0].GetProperty("id").GetString());
        Assert.Equal(720, status.GetProperty("drillIntervalHours").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("nextDrillAt").ValueKind);
        // The test principal stays out of the operator list; the audit entry carries the measured time.
        var agents = await owner.GetFromJsonAsync<JsonElement>("/api/auth/iam/operator-agents");
        Assert.Equal(agentId, Assert.Single(agents.EnumerateArray()).GetProperty("id").GetString());
        var entry = Assert.Single(store.ControlPlaneAudit.ReadNewest(candidate => candidate.Operation == "operator-kill.drill", 10));
        Assert.Equal("passed", entry.Outcome);
        Assert.Contains("durationMs", entry.Raw, StringComparison.Ordinal);
    }

    private static async Task<TimeSpan> RefusedWithinAsync(Func<Task<bool>> refused, Stopwatch clock)
    {
        while (clock.Elapsed < RefusalBudget)
        {
            if (await refused()) return clock.Elapsed;
            await Task.Delay(100);
        }
        return clock.Elapsed;
    }

    private void SavePrincipals(string operatorAgentId)
    {
        object Principal(string id, string token, string kind, string[] scopes, string[] agentIds, string? operatorAgent = null) => new
        {
            id, audience = OperationsProtocol.Audience, tokenSha256 = OperationsProtocol.Digest(token), kind, scopes, agentIds,
            roots = Array.Empty<string>(), operatorAgentId = operatorAgent,
        };
        var document = new
        {
            principals = new[]
            {
                Principal("control-plane", _revokerToken, "service", ["principals.revoke"], []),
                Principal("operator-console", _operatorOpsToken, "service", ["operations.read", "host.inspect", "maintenance.execute"], ["host-a"], operatorAgentId),
                Principal("host-a", _hostAgentToken, "agent", ["agents.connect", "host.inspect"], ["host-a"]),
            },
        };
        var temp = PrincipalsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, OperationsProtocol.Json));
        File.Move(temp, PrincipalsFile, true);
    }

    private HttpClient OperationsClient(string token)
    {
        var client = _operations.GetTestClient();
        client.BaseAddress = new Uri("https://operations.test/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(OperationsProtocol.Header, OperationsProtocol.Version.ToString());
        return client;
    }

    private IConfiguration StudioConfiguration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["TaskRepository"] = _workspace,
        ["Security:OperatorKillSwitch:OperationsServer:Url"] = "https://operations.test/",
        ["Security:OperatorKillSwitch:OperationsServer:CredentialFile"] = RevokerCredentialFile,
    }).Build();

    private WebApplicationFactory<Program> BuildFactory(bool bootstrap = true)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(ProjectPath("alpha"), state));
        var card = Path.Combine(ProjectPath("alpha"), TaskStates.Backlog, "alpha-card");
        Directory.CreateDirectory(card);
        File.WriteAllText(Path.Combine(card, "task.json"), JsonSerializer.Serialize(new
        {
            id = "alpha-card", title = "alpha card", state = TaskStates.Backlog, order = 1, agent = "codex", kind = "task",
        }));
        File.WriteAllText(Path.Combine(card, "prompt.md"), "Fixture card.");
        var operationsHandler = _operations.GetTestServer().CreateHandler();
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
                ["Secrets:KeyFile"] = Path.Combine(_workspace + "-keys", "secrets.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
                ["Security:OperatorKillSwitch:OperationsServer:Url"] = "https://operations.test/",
                ["Security:OperatorKillSwitch:OperationsServer:CredentialFile"] = RevokerCredentialFile,
                ["WatchPaths:0:Name"] = "alpha",
                ["WatchPaths:0:Path"] = ProjectPath("alpha"),
                ["WatchPaths:0:RootPath"] = ProjectPath("alpha"),
            }));
            builder.ConfigureTestServices(services => services
                .AddHttpClient("operator-kill-switch-operations")
                .ConfigurePrimaryHttpMessageHandler(() => operationsHandler));
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        if (!bootstrap) return factory;
        var first = store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        var owner = store.AuthenticateSession(first.SessionToken)!;
        _ownerTotpSecret = store.BeginTotpEnrollment(owner);
        store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_ownerTotpSecret,
            factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime));
        return factory;
    }

    private string ProjectPath(string project) => Path.Combine(_workspace, "projects", project);

    private async Task<HttpClient> OwnerClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "first.owner", password = Password,
            secondFactorCode = SecondFactorCodes.CurrentTotp(_ownerTotpSecret,
                factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime),
        });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!);
        return client;
    }

    private static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, object? body)
    {
        using var response = await SendRawAsync(client, method, path, body);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, object? body)
        => client.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) });

    public async Task DisposeAsync()
    {
        await _operations.DisposeAsync();
        foreach (var directory in new[] { _workspace, _workspace + "-keys", OperationsDirectory })
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
