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
/// Security model SM-4 on the real networked host: every operator session
/// request passes the policy step. T0 reads are admitted, a T1 operation only
/// under a live standing grant that covers its operation, scope and rate, every
/// T2 operation is refused with <c>approval-required</c>, and a request outside
/// the operation catalogue is refused, whatever the session's role bindings say.
/// </summary>
public sealed class OperatorPolicyStepTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-policy-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private string _ownerTotpSecret = string.Empty;

    [Fact]
    public async Task An_operator_session_with_developer_bindings_is_refused_outside_the_catalogue_and_on_t2()
    {
        using var factory = BuildFactory();
        var (agentId, token) = StartSession(factory);
        using var client = BearerClient(factory, token);

        using (var read = await client.GetAsync("/api/tasks/alpha-card"))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        await AssertRefused(client.PostAsJsonAsync("/api/tasks/alpha-card/preview-comments",
            new { previewId = "preview-1", comment = "Looks fine." }), OperatorPolicyReasons.NotCatalogued);
        await AssertRefused(client.DeleteAsync("/api/tasks/alpha-card"), OperatorPolicyReasons.ApprovalRequired);
        // A T2 request waits for the Owner's approval (SM-5) only once the bindings admit it:
        // Developer holds no secrets.write, so no approval request opens.
        await AssertRefused(client.PostAsJsonAsync("/api/projects/alpha/secrets", new { name = "DEPLOY_VALUE", value = "x" }),
            "permission-denied");
        await AssertRefused(client.PostAsJsonAsync("/api/tasks/alpha-card/move",
            new MoveJobRequest { TargetState = TaskStates.Completed }), OperatorPolicyReasons.ApprovalRequired);
        // Access changes are T2 as well; the policy step answers before the access floor.
        await AssertRefused(client.PostAsJsonAsync("/api/auth/iam/bindings", new
        {
            principalKind = "service-account", principalId = agentId, role = "admin", scope = new { kind = "workspace" },
        }), OperatorPolicyReasons.ApprovalRequired);

        // A T1 requeue without a standing grant is refused with a typed reason.
        await AssertRefused(client.PostAsJsonAsync("/api/tasks/alpha-card/move",
            new MoveJobRequest { TargetState = TaskStates.Preparation }), OperatorPolicyReasons.GrantRequired);

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        Assert.Equal(TaskStates.Backlog, scanner.FindJob("alpha-card", ProjectPath("alpha"))!.State);
    }

    [Fact]
    public async Task A_standing_grant_admits_t1_within_its_rate_until_the_owner_revokes_it()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, token) = StartSession(factory);
        using var owner = await OwnerClientAsync(factory);
        using var client = BearerClient(factory, token);
        var auditStart = store.ControlPlaneAudit.HeadSequence + 1;

        var catalogue = await owner.GetFromJsonAsync<JsonElement>("/api/auth/iam/operator-catalogue");
        var requeue = catalogue.GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("id").GetString() == OperatorOperationIds.CardRequeue);
        Assert.Equal(("T1", "project"), (requeue.GetProperty("tier").GetString(), requeue.GetProperty("scopeKind").GetString()));
        Assert.Equal(7, catalogue.GetProperty("defaultGrantDays").GetInt32());

        var grantRequest = new
        {
            principalId = agentId, operations = new[] { OperatorOperationIds.CardRequeue },
            scope = new { kind = "project", project = "alpha" }, ratePerHour = 2, reason = "Requeue stalled cards",
        };
        // Issuing needs a fresh step-up, and the step-up is single use.
        using (var unverified = await owner.PostAsJsonAsync("/api/auth/iam/standing-grants", grantRequest))
            Assert.Equal("step-up-required", await Error(unverified));
        await StepUpAsync(factory, owner);
        var grant = await ReadOk(owner.PostAsJsonAsync("/api/auth/iam/standing-grants", grantRequest));
        var grantId = grant.GetProperty("id").GetString()!;
        Assert.Equal("active", grant.GetProperty("state").GetString());
        Assert.Equal(grant.GetProperty("issuedAt").GetDateTime().AddDays(7), grant.GetProperty("expiresAt").GetDateTime());

        // The operator session cannot change grants: that is T2.
        await AssertRefused(client.PostAsJsonAsync("/api/auth/iam/standing-grants", grantRequest), OperatorPolicyReasons.ApprovalRequired);

        await ReadOk(client.PostAsJsonAsync("/api/tasks/alpha-card/move", new MoveJobRequest { TargetState = TaskStates.Preparation }));
        await ReadOk(client.PostAsJsonAsync("/api/tasks/alpha-card/move", new MoveJobRequest { TargetState = TaskStates.Backlog }));
        var exceeded = await AssertRefused(client.PostAsJsonAsync("/api/tasks/alpha-card/move",
            new MoveJobRequest { TargetState = TaskStates.Preparation }), OperatorPolicyReasons.GrantRateExceeded);
        Assert.Equal((OperatorOperationIds.CardRequeue, "T1"),
            (exceeded.GetProperty("operation").GetString(), exceeded.GetProperty("tier").GetString()));

        // A fresh grant admits again; the Owner's revocation stops it on the next request.
        await StepUpAsync(factory, owner);
        var second = await ReadOk(owner.PostAsJsonAsync("/api/auth/iam/standing-grants", grantRequest with { ratePerHour = 5 }));
        await ReadOk(client.PostAsJsonAsync("/api/tasks/alpha-card/move", new MoveJobRequest { TargetState = TaskStates.Preparation }));
        var revoked = await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/standing-grants/{second.GetProperty("id").GetString()}/revoke",
            new { reason = "Overnight run finished" }));
        Assert.Equal("revoked", revoked.GetProperty("state").GetString());
        await AssertRefused(client.PostAsJsonAsync("/api/tasks/alpha-card/move",
            new MoveJobRequest { TargetState = TaskStates.Backlog }), OperatorPolicyReasons.GrantRateExceeded);
        await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/standing-grants/{grantId}/revoke", new { }));
        await AssertRefused(client.PostAsJsonAsync("/api/tasks/alpha-card/move",
            new MoveJobRequest { TargetState = TaskStates.Backlog }), OperatorPolicyReasons.GrantRequired);

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        Assert.Equal(TaskStates.Preparation, scanner.FindJob("alpha-card", ProjectPath("alpha"))!.State);
        var listed = await owner.GetFromJsonAsync<JsonElement>($"/api/auth/iam/standing-grants?principalId={agentId}");
        Assert.All(listed.EnumerateArray(), item => Assert.Equal("revoked", item.GetProperty("state").GetString()));

        // Grant changes are in the IAM audit; admitted requests name their grant, refusals their reason.
        var entries = store.ControlPlaneAudit.Read(auditStart, 10_000);
        Assert.Equal(["standing-grant.create", "standing-grant.create", "standing-grant.revoke", "standing-grant.revoke"],
            entries.Where(entry => entry.Stream == ControlPlaneAuditStreams.Iam && entry.Operation.StartsWith("standing-grant.", StringComparison.Ordinal))
                .Select(entry => entry.Operation).ToList());
        var moves = entries.Where(entry => entry.Stream == ControlPlaneAuditStreams.OperatorAgent
                                           && entry.Operation == "POST /api/tasks/alpha-card/move").ToList();
        Assert.Equal([grantId, grantId, null, second.GetProperty("id").GetString(), null, null],
            moves.Select(entry => entry.Details!.Value.GetProperty("grantId").GetString()).ToList());
        Assert.Equal([null, null, OperatorPolicyReasons.GrantRateExceeded, null, OperatorPolicyReasons.GrantRateExceeded, OperatorPolicyReasons.GrantRequired],
            moves.Select(entry => entry.Details!.Value.GetProperty("policyReason").GetString()).ToList());
        Assert.True(store.ControlPlaneAudit.VerifyChain().Ok);
    }

    private static async Task<JsonElement> AssertRefused(Task<HttpResponseMessage> request, string reason)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode} {body}");
        var root = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(reason, root.GetProperty("error").GetString());
        return root;
    }

    private static async Task<JsonElement> ReadOk(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string?> Error(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    private async Task StepUpAsync(WebApplicationFactory<Program> factory, HttpClient owner)
        => await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode(factory) }));

    private string CurrentCode(WebApplicationFactory<Program> factory)
    {
        _clock.Advance(TimeSpan.FromSeconds(30));
        return SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime);
    }

    private async Task<HttpClient> OwnerClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "first.owner", password = Password, secondFactorCode = CurrentCode(factory),
        });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!);
        return client;
    }

    private (string AgentId, string Token) StartSession(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))]), ownerId);
        return (agent.Id, store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), ownerId).Secret);
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

    private static HttpClient BearerClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public void Dispose()
    {
        DeleteTemporaryDirectory(_workspace);
        DeleteTemporaryDirectory(_workspace + "-keys");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) when (attempt < 9) { Thread.Sleep(100); }
        }
    }
}
