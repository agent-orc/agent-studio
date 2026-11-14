using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-1 acceptance on the real networked host: a scripted
/// operator session of mixed requests (reads, card writes that produce
/// timeline events under a standing grant, and refused calls) is attributed to the operator agent
/// session in every audit entry and timeline event, and never to the Owner.
/// The Owner manages the agent and reads the activity view over HTTP; the
/// session token is refused on access administration, audit, and grants.
/// </summary>
public sealed class OperatorSessionScriptedTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-session-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private string _ownerTotpSecret = string.Empty;

    [Fact]
    public async Task A_scripted_operator_session_is_attributed_to_the_session_and_never_to_the_owner()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        using var owner = await OwnerClientAsync(factory);

        var agent = await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/operator-agents", new
        {
            name = "ops console", description = "Operator agent on the owner's machine",
            bindings = new[] { new { role = "developer", scope = new { kind = "project", project = "alpha" } } },
        });
        var agentId = agent.GetProperty("id").GetString()!;
        var started = await SendAsync(owner, HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions",
            new { name = "maintenance", lifetimeHours = 8 });
        var token = started.GetProperty("token").GetString()!;
        var sessionId = started.GetProperty("sessionId").GetString()!;
        var actor = $"operator-agent:{agentId}/session:{sessionId}";
        Assert.Equal(actor, started.GetProperty("actor").GetString());
        Assert.Equal($"human:{ownerId}", started.GetProperty("delegatedBy").GetString());
        // SM-4: requeues are routine (T1) and need the Owner's standing grant.
        using (var stepUp = await SendRawAsync(owner, HttpMethod.Post, "/api/auth/step-up", new { code = CurrentCode(factory) }))
            Assert.Equal(HttpStatusCode.NoContent, stepUp.StatusCode);
        await SendAsync(owner, HttpMethod.Post, "/api/auth/iam/standing-grants", new
        {
            principalId = agentId, operations = new[] { OperatorOperationIds.CardRequeue },
            scope = new { kind = "project", project = "alpha" }, ratePerHour = 20, reason = "Scripted session requeues",
        });

        var audit = store.ControlPlaneAudit;
        var firstSequence = audit.HeadSequence + 1;
        var sessionStart = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddSeconds(-1);
        using var operatorClient = BearerClient(factory, token);
        var statuses = new List<HttpStatusCode>();
        for (var round = 0; round < 10; round++)
        {
            async Task Record(Task<HttpResponseMessage> request)
            {
                using var response = await request;
                statuses.Add(response.StatusCode);
            }
            await Record(operatorClient.GetAsync("/api/tasks/alpha-card"));
            await Record(operatorClient.PostAsJsonAsync("/api/tasks/alpha-card/preview-comments",
                new { previewId = $"preview-{round}", comment = $"Checked preview {round}." }));
            await Record(operatorClient.PostAsJsonAsync("/api/tasks/alpha-card/move",
                new MoveJobRequest { TargetState = round % 2 == 0 ? TaskStates.Preparation : TaskStates.Backlog }));
            await Record(operatorClient.GetAsync("/api/tasks/beta-card"));
            await Record(operatorClient.PostAsJsonAsync("/api/auth/iam/bindings", new
            {
                principalKind = "service-account", principalId = agentId, role = "admin", scope = new { kind = "workspace" },
            }));
            await Record(operatorClient.GetAsync("/api/auth/audit/operator-activity"));
        }

        // Reads and granted requeues succeed; the uncatalogued preview comment,
        // the other project, the binding change (T2), and the audit read are refused.
        Assert.Equal(60, statuses.Count);
        Assert.Equal(40, statuses.Count(status => status == HttpStatusCode.Forbidden));
        Assert.Equal(20, statuses.Count(status => status == HttpStatusCode.OK));

        // Every audit entry the session produced names the session and the
        // delegating Owner; none names the Owner as actor.
        var entries = audit.Read(firstSequence, 10_000);
        Assert.True(entries.Count >= 60, $"Expected one entry per request, found {entries.Count}.");
        Assert.All(entries, entry =>
        {
            Assert.Equal(actor, entry.Actor);
            Assert.Equal(sessionId, entry.Session);
            Assert.Equal($"human:{ownerId}", entry.DelegatedBy);
            Assert.StartsWith("sha256:", entry.ParameterDigest ?? "sha256:");
        });
        Assert.DoesNotContain(entries, entry => entry.Actor.Contains(ownerId, StringComparison.Ordinal)
            || entry.Actor.Contains("first.owner", StringComparison.Ordinal));
        Assert.Equal(60, entries.Count(entry => entry.Stream == ControlPlaneAuditStreams.OperatorAgent));
        Assert.Contains(entries, entry => entry.Operation == "POST /api/auth/iam/bindings" && entry.Outcome == "403");
        Assert.True(audit.VerifyChain().Ok);

        // Every timeline event the session wrote on the card names it as well.
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var card = scanner.FindJob("alpha-card", ProjectPath("alpha"))!;
        var events = factory.Services.GetRequiredService<TimelineLog>().ReadAll(card.FolderPath)
            .Where(evt => evt.Ts >= sessionStart).ToList();
        Assert.True(events.Count >= 10, $"Expected move events, found {events.Count}.");
        Assert.DoesNotContain(events, evt => evt.Kind == "preview-comment");
        Assert.Contains(events, evt => evt.Kind == TimelineEventKinds.LaneChanged);
        Assert.All(events, evt => Assert.Equal((actor, $"human:{ownerId}"), (evt.Actor, evt.DelegatedBy)));
        Assert.DoesNotContain(events, evt => evt.Actor.Contains(ownerId, StringComparison.Ordinal)
            || evt.Actor.Contains("first.owner", StringComparison.Ordinal)
            || (evt.Details?.Values.Any(value => value.Contains("first.owner", StringComparison.Ordinal)
                || value == $"human:{ownerId}") ?? false));

        // The Owner sees the session and its entries in the activity view.
        var activity = await owner.GetFromJsonAsync<JsonElement>($"/api/auth/audit/operator-activity?sessionId={sessionId}&limit=1000");
        Assert.Equal(60, activity.GetProperty("entries").GetArrayLength());
        Assert.All(activity.GetProperty("entries").EnumerateArray(),
            entry => Assert.Equal(actor, entry.GetProperty("actor").GetString()));
        var listed = Assert.Single(activity.GetProperty("operatorAgents").EnumerateArray());
        Assert.Equal("active", Assert.Single(listed.GetProperty("sessions").EnumerateArray()).GetProperty("state").GetString());
        var verified = await SendAsync(owner, HttpMethod.Post, "/api/auth/audit/verify", null);
        Assert.Equal("verified", verified.GetProperty("state").GetString());

        // Only the Owner renews; a revoked session is refused.
        var renewed = await SendAsync(owner, HttpMethod.Post,
            $"/api/auth/iam/operator-agents/{agentId}/sessions/{sessionId}/renew", new { lifetimeHours = 2 });
        Assert.Equal("active", renewed.GetProperty("state").GetString());
        using (var revoke = await SendRawAsync(owner, HttpMethod.Delete, $"/api/auth/iam/operator-agents/{agentId}/sessions/{sessionId}", null))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var afterRevoke = await operatorClient.GetAsync("/api/tasks/alpha-card");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task An_operator_session_token_gets_403_on_access_administration_audit_and_grants()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null,
            [new IamGrantRequest(IamRoles.Admin, IamScope.Workspace)]), ownerId);
        var started = store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), ownerId);
        using var client = BearerClient(factory, started.Secret);

        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/auth/iam", null),
            (HttpMethod.Get, "/api/auth/users", null),
            (HttpMethod.Post, "/api/auth/invitations", new { username = "new.person", displayName = "New", role = "viewer" }),
            (HttpMethod.Post, "/api/auth/iam/bindings", new
            {
                principalKind = "service-account", principalId = agent.Id, role = "admin", scope = new { kind = "workspace" },
            }),
            (HttpMethod.Post, "/api/auth/iam/service-accounts", new { name = "shadow" }),
            (HttpMethod.Post, "/api/auth/iam/operator-agents", new { name = "shadow" }),
            (HttpMethod.Post, $"/api/auth/iam/operator-agents/{agent.Id}/sessions", new { }),
            (HttpMethod.Post, $"/api/auth/iam/operator-agents/{agent.Id}/sessions/{started.Session.Id}/renew", new { }),
            (HttpMethod.Post, "/api/auth/audit/verify", null),
            (HttpMethod.Get, "/api/auth/audit/operator-activity", null),
            (HttpMethod.Post, "/api/auth/api-tokens", new { name = "shadow", role = "viewer", projects = new[] { "*" } }),
        };
        foreach (var (method, path, body) in calls)
        {
            using var request = new HttpRequestMessage(method, path)
            { Content = body is null ? null : JsonContent.Create(body) };
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {(int)response.StatusCode}");
        }
        Assert.Equal(2, store.ReadIamSnapshot().Bindings.Count);
        Assert.Single(store.ListOperatorAgents().Single().Sessions!);
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var project in new[] { "alpha", "beta" })
        {
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(ProjectPath(project), state));
            var card = Path.Combine(ProjectPath(project), TaskStates.Backlog, project + "-card");
            Directory.CreateDirectory(card);
            File.WriteAllText(Path.Combine(card, "task.json"), JsonSerializer.Serialize(new
            {
                id = project + "-card", title = project + " card", state = TaskStates.Backlog, order = 1, agent = "codex", kind = "task",
            }));
            File.WriteAllText(Path.Combine(card, "prompt.md"), "Fixture card.");
        }
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
                ["Secrets:KeyFile"] = Path.Combine(_workspace + "-keys", "secrets.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
                ["WatchPaths:0:Name"] = "alpha",
                ["WatchPaths:0:Path"] = ProjectPath("alpha"),
                ["WatchPaths:0:RootPath"] = ProjectPath("alpha"),
                ["WatchPaths:1:Name"] = "beta",
                ["WatchPaths:1:Path"] = ProjectPath("beta"),
                ["WatchPaths:1:RootPath"] = ProjectPath("beta"),
            }));
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var bootstrap = store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        var owner = store.AuthenticateSession(bootstrap.SessionToken)!;
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
            secondFactorCode = CurrentCode(factory),
        });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!);
        return client;
    }

    private string CurrentCode(WebApplicationFactory<Program> factory)
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        return SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime);
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

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        if (Directory.Exists(_workspace + "-keys")) Directory.Delete(_workspace + "-keys", true);
    }
}
