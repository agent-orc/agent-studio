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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Security model SM-5 (decision SM-D8 option A) on the real networked host:
/// a T2 request of an operator session becomes an approval request instead of
/// an effect. The Owner approves it in Studio after a step-up; the approval is
/// single use, bound to the parameter digest and the session, and expires
/// after 15 minutes. Execution re-checks the digest and records its result
/// against the approval. The approving actor is the authenticated session,
/// never a client header.
/// </summary>
public sealed class OperatorApprovalFlowTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private const string ApprovalHeader = "X-Operator-Approval";
    private const string SourceHeader = "X-Operator-Instruction-Source";
    private const string ReasonHeader = "X-Operator-Reason";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "operator-approval-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingApprovalPush _push = new();
    private string _ownerTotpSecret = string.Empty;

    [Fact]
    public async Task A_t2_request_runs_only_after_an_owner_approval_made_with_step_up()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, sessionId, token) = StartSession(factory);
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var auditStart = store.ControlPlaneAudit.HeadSequence + 1;

        // The T2 request is refused and becomes an approval request carrying what the Owner must see.
        var refused = await AssertRefused(Delete(agent, "alpha-card", source: "owner-chat", reason: "Remove the duplicate card"),
            "approval-required");
        var approvalId = refused.GetProperty("approvalId").GetString()!;
        Assert.StartsWith("apr_", approvalId);
        Assert.True(CardExists(factory, "alpha-card"));

        var pending = await Approval(owner, approvalId);
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.Equal((OperatorOperationIds.DataDelete, "T2"),
            (pending.GetProperty("operation").GetString(), pending.GetProperty("tier").GetString()));
        Assert.Equal(("DELETE", "/api/tasks/alpha-card", "?project=alpha"),
            (pending.GetProperty("method").GetString(), pending.GetProperty("path").GetString(), pending.GetProperty("query").GetString()));
        Assert.StartsWith("sha256:", pending.GetProperty("parameterDigest").GetString());
        Assert.Contains("alpha-card", pending.GetProperty("target").GetString());
        Assert.Equal((agentId, sessionId), (pending.GetProperty("principalId").GetString(), pending.GetProperty("sessionId").GetString()));
        Assert.Equal($"operator-agent:{agentId}/session:{sessionId}", pending.GetProperty("actor").GetString());
        Assert.Equal(("owner-chat", "Remove the duplicate card"),
            (pending.GetProperty("instructionSource").GetString(), pending.GetProperty("reason").GetString()));
        Assert.Equal(pending.GetProperty("requestedAt").GetDateTime().AddMinutes(15), pending.GetProperty("expiresAt").GetDateTime());

        // A pending request does not run, and repeating it does not open a second request.
        await AssertRefused(Delete(agent, "alpha-card", approvalId), "approval-pending");
        var repeated = await AssertRefused(Delete(agent, "alpha-card", source: "owner-chat", reason: "Remove the duplicate card"),
            "approval-required");
        Assert.Equal(approvalId, repeated.GetProperty("approvalId").GetString());

        // Without a fresh step-up the approval is refused and nothing changes.
        using (var unverified = await owner.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
            Assert.Equal("step-up-required", await Error(unverified));
        }
        Assert.Equal("pending", (await Approval(owner, approvalId)).GetProperty("state").GetString());
        await AssertRefused(Delete(agent, "alpha-card", approvalId), "approval-pending");
        Assert.True(CardExists(factory, "alpha-card"));

        await StepUpAsync(factory, owner);
        var approved = await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve",
            new { note = "Duplicate confirmed" }));
        Assert.Equal(("approved", ownerId), (approved.GetProperty("state").GetString(), approved.GetProperty("decidedBy").GetString()));
        Assert.Equal(approved.GetProperty("decidedAt").GetDateTime().AddMinutes(15), approved.GetProperty("approvalExpiresAt").GetDateTime());

        // The exact request runs once; the result is recorded against the approval.
        await ReadOk(Delete(agent, "alpha-card", approvalId));
        Assert.False(CardExists(factory, "alpha-card"));
        var used = await Approval(owner, approvalId);
        Assert.Equal(("used", 200), (used.GetProperty("state").GetString(), used.GetProperty("resultStatus").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, used.GetProperty("usedAt").ValueKind);

        // Replay of a used approval is refused.
        await AssertRefused(Delete(agent, "alpha-card", approvalId), "approval-used");

        var entries = store.ControlPlaneAudit.Read(auditStart, 10_000);
        Assert.Equal(["operator-approval.request", "operator-approval.approve", "operator-approval.execute"],
            entries.Where(entry => entry.Stream == ControlPlaneAuditStreams.Iam && entry.Operation.StartsWith("operator-approval.", StringComparison.Ordinal))
                .Select(entry => entry.Operation).ToList());
        var deletes = entries.Where(entry => entry.Stream == ControlPlaneAuditStreams.OperatorAgent
                                             && entry.Operation == "DELETE /api/tasks/alpha-card").ToList();
        // Every attempt names the approval it concerned; only the admitted one ran.
        Assert.Equal(Enumerable.Repeat(approvalId, 6).ToList(),
            deletes.Select(entry => entry.Details!.Value.GetProperty("approvalId").GetString()).ToList());
        Assert.Equal(["approval-required", "approval-pending", "approval-required", "approval-pending", null, "approval-used"],
            deletes.Select(entry => entry.Details!.Value.GetProperty("policyReason").GetString()).ToList());
        Assert.Equal(["403", "403", "403", "403", "200", "403"], deletes.Select(entry => entry.Outcome).ToList());
        Assert.All(deletes, entry => Assert.Equal(used.GetProperty("parameterDigest").GetString(), entry.ParameterDigest));
        Assert.True(store.ControlPlaneAudit.VerifyChain().Ok);
    }

    [Fact]
    public async Task A_changed_parameter_another_session_and_an_expired_approval_are_refused()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, _, token) = StartSession(factory);
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);

        var approvalId = (await AssertRefused(Delete(agent, "alpha-card", source: "card"), "approval-required"))
            .GetProperty("approvalId").GetString()!;
        await StepUpAsync(factory, owner);
        await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve", new { }));

        // Another card, another query, or another session never runs under this approval.
        await AssertRefused(Delete(agent, "alpha-second", approvalId), "approval-digest-mismatch");
        await AssertRefused(agent.SendAsync(Request(HttpMethod.Delete, "/api/tasks/alpha-card?project=alpha&watchPath=alpha", approvalId)),
            "approval-digest-mismatch");
        var otherToken = store.StartOperatorSession(agentId, new StartOperatorSessionRequest("second", null),
            store.ListUsers().Single(user => user.Username == "first.owner").Id).Secret;
        using (var other = BearerClient(factory, otherToken))
            await AssertRefused(Delete(other, "alpha-card", approvalId), "approval-not-found");
        Assert.True(CardExists(factory, "alpha-card"));
        Assert.True(CardExists(factory, "alpha-second"));
        Assert.Equal("approved", (await Approval(owner, approvalId)).GetProperty("state").GetString());

        // Fifteen minutes after the decision the approval no longer runs anything.
        _clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await AssertRefused(Delete(agent, "alpha-card", approvalId), "approval-expired");
        Assert.True(CardExists(factory, "alpha-card"));
        Assert.Equal("expired", (await Approval(owner, approvalId)).GetProperty("state").GetString());

        // A request left pending for 15 minutes can no longer be approved.
        var late = (await AssertRefused(Delete(agent, "alpha-second", source: "log"), "approval-required"))
            .GetProperty("approvalId").GetString()!;
        _clock.Advance(TimeSpan.FromMinutes(16));
        await StepUpAsync(factory, owner);
        using (var expired = await owner.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{late}/approve", new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
            Assert.Equal("approval-expired", await Error(expired));
        }
        await AssertRefused(Delete(agent, "alpha-second", late), "approval-expired");
        Assert.True(CardExists(factory, "alpha-second"));
    }

    [Fact]
    public async Task Approval_actors_come_from_the_authenticated_session_never_from_a_client_header()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (_, _, token) = StartSession(factory);
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var approvalId = (await AssertRefused(Delete(agent, "alpha-card", source: "web-page"), "approval-required"))
            .GetProperty("approvalId").GetString()!;

        // The operator session forges the Owner as the approving actor: refused.
        foreach (var path in new[] { "approve", "reject" })
        {
            using var forged = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/iam/operator-approvals/{approvalId}/{path}")
            { Content = JsonContent.Create(new { }) };
            forged.Headers.Add("X-Client-Id", ownerId);
            forged.Headers.Add("X-Approval-Actor", "human:" + ownerId);
            using var response = await agent.SendAsync(forged);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // A caller without a Studio session cannot approve by naming the Owner either.
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
               { BaseAddress = new Uri("https://studio.test"), HandleCookies = false }))
        {
            anonymous.DefaultRequestHeaders.Add("Origin", "https://studio.test");
            anonymous.DefaultRequestHeaders.Add("X-Client-Id", ownerId);
            using var response = await anonymous.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve", new { });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal("pending", (await Approval(owner, approvalId)).GetProperty("state").GetString());
        await AssertRefused(Delete(agent, "alpha-card", approvalId), "approval-pending");

        // The Owner's own approval records the session's user, whatever header the client adds.
        await StepUpAsync(factory, owner);
        using var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/iam/operator-approvals/{approvalId}/approve")
        { Content = JsonContent.Create(new { decidedBy = "usr_forged" }) };
        approve.Headers.Add("X-Client-Id", "usr_forged");
        approve.Headers.Add("X-Approval-Actor", "human:usr_forged");
        var approved = await ReadOk(owner.SendAsync(approve));
        Assert.Equal(ownerId, approved.GetProperty("decidedBy").GetString());
        var entries = store.ControlPlaneAudit.Read(1, 10_000);
        Assert.Contains(entries, entry => entry.Operation == "operator-approval.approve" && entry.Actor == "human:" + ownerId);
        Assert.DoesNotContain(entries, entry => entry.Actor.Contains("usr_forged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_push_notification_links_to_the_request_and_cannot_approve_it()
    {
        using var factory = BuildFactory();
        var (_, _, token) = StartSession(factory);
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);

        var approvalId = (await AssertRefused(Delete(agent, "alpha-card", source: "repository-file", reason: "Cleanup"),
            "approval-required")).GetProperty("approvalId").GetString()!;
        var sent = await _push.WaitAsync(approvalId);
        Assert.Equal($"https://studio.test/#/workspace/settings/access?approval={approvalId}", sent.Url);
        Assert.Contains(OperatorOperationIds.DataDelete, sent.Message);
        // The notification carries a link, not a credential: no token, digest, or decision route.
        var serialized = JsonSerializer.Serialize(sent);
        Assert.DoesNotContain("sha256:", serialized);
        Assert.DoesNotContain("/approve", serialized);
        Assert.DoesNotContain(token, serialized);

        // Following the link without a Studio session and second factor approves nothing.
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        anonymous.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        using (var response = await anonymous.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve", new { }))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("pending", (await Approval(owner, approvalId)).GetProperty("state").GetString());
    }

    private static Task<HttpResponseMessage> Delete(HttpClient client, string card, string? approvalId = null,
        string? source = null, string? reason = null)
        => client.SendAsync(Request(HttpMethod.Delete, $"/api/tasks/{card}?project=alpha", approvalId, source, reason));

    private static HttpRequestMessage Request(HttpMethod method, string path, string? approvalId = null,
        string? source = null, string? reason = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (approvalId is not null) request.Headers.Add(ApprovalHeader, approvalId);
        if (source is not null) request.Headers.Add(SourceHeader, source);
        if (reason is not null) request.Headers.Add(ReasonHeader, reason);
        return request;
    }

    private static async Task<JsonElement> Approval(HttpClient owner, string approvalId)
    {
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/auth/iam/operator-approvals");
        return list.EnumerateArray().Single(item => item.GetProperty("id").GetString() == approvalId).Clone();
    }

    private bool CardExists(WebApplicationFactory<Program> factory, string card)
        => factory.Services.GetRequiredService<TaskScannerService>().FindJob(card, ProjectPath("alpha")) is not null;

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
        => await ReadOk(owner.PostAsJsonAsync("/api/auth/step-up", new { code = CurrentCode() }));

    private string CurrentCode() => SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime);

    private async Task<HttpClient> OwnerClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "first.owner", password = Password, secondFactorCode = CurrentCode(),
        });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!);
        return client;
    }

    private static (string AgentId, string SessionId, string Token) StartSession(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))]), ownerId);
        var started = store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), ownerId);
        return (agent.Id, started.Session.Id, started.Secret);
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(ProjectPath("alpha"), state));
        var order = 1;
        foreach (var name in new[] { "alpha-card", "alpha-second" })
        {
            var card = Path.Combine(ProjectPath("alpha"), TaskStates.Backlog, name);
            Directory.CreateDirectory(card);
            File.WriteAllText(Path.Combine(card, "task.json"), JsonSerializer.Serialize(new
            {
                id = name, title = name, state = TaskStates.Backlog, order = order++, agent = "codex", kind = "task",
            }));
            File.WriteAllText(Path.Combine(card, "prompt.md"), "Fixture card.");
        }
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
                ["OperatorApprovals:StudioUrl"] = "https://studio.test",
                ["WatchPaths:0:Name"] = "alpha",
                ["WatchPaths:0:Path"] = ProjectPath("alpha"),
                ["WatchPaths:0:RootPath"] = ProjectPath("alpha"),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IOperatorApprovalPush>();
                services.AddSingleton<IOperatorApprovalPush>(_push);
            });
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var bootstrap = store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        var owner = store.AuthenticateSession(bootstrap.SessionToken)!;
        _ownerTotpSecret = store.BeginTotpEnrollment(owner);
        store.ConfirmTotpEnrollment(owner, SecondFactorCodes.CurrentTotp(_ownerTotpSecret, _clock.GetUtcNow().UtcDateTime));
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
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
        if (Directory.Exists(_workspace + "-keys")) Directory.Delete(_workspace + "-keys", true);
    }

    /// <summary>Records what the push channel would deliver.</summary>
    private sealed class RecordingApprovalPush : IOperatorApprovalPush
    {
        private readonly List<OperatorApprovalPushMessage> _sent = [];

        public bool Configured => true;

        public Task SendAsync(OperatorApprovalPushMessage message, CancellationToken ct)
        {
            lock (_sent) _sent.Add(message);
            return Task.CompletedTask;
        }

        public async Task<OperatorApprovalPushMessage> WaitAsync(string approvalId)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                lock (_sent)
                    if (_sent.FirstOrDefault(message => message.ApprovalId == approvalId) is { } found) return found;
                await Task.Delay(50);
            }
            throw new TimeoutException($"No push notification for {approvalId}.");
        }
    }
}
