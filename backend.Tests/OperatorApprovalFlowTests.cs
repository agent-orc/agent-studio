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
    public async Task Preflight_blocker_override_requires_a_redeemed_owner_approval()
    {
        using var factory = BuildFactory();
        var (_, _, token) = StartSession(factory, installationScope: true);
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);
        const string route = "/api/operator/preflight/restart/override";

        var request = await AssertRefused(agent.SendAsync(Request(HttpMethod.Post, route,
            source: "owner-chat", reason: "Restart in the approved window")), "approval-required");
        var approvalId = request.GetProperty("approvalId").GetString()!;
        await StepUpAsync(factory, owner);
        await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve", new { }));

        var allowed = await ReadOk(agent.SendAsync(Request(HttpMethod.Post, route, approvalId)));
        Assert.True(allowed.GetProperty("allowed").GetBoolean());
        Assert.Equal(approvalId, allowed.GetProperty("overrideApprovalId").GetString());
        Assert.Equal("used", (await Approval(owner, approvalId)).GetProperty("state").GetString());
        await AssertRefused(agent.SendAsync(Request(HttpMethod.Post, route, approvalId)), "approval-used");
    }

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

    /// <summary>
    /// ES-2 (ES-D8 A, ES-D9 single-request path): an operator session's
    /// override acceptance is the T2 operation card.accept-override. After the
    /// Owner approves it, the replay carries project.cards.override for that
    /// request and completes the card; the timeline and the audit entry name
    /// the session, the approving Owner and the reason.
    /// </summary>
    [Fact]
    public async Task An_override_acceptance_by_an_operator_session_completes_after_the_owner_approves()
    {
        using var factory = BuildFactory(("alpha-review", TaskStates.HumanReview));
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, sessionId, token) = StartSession(factory, new IamGrantRequest(IamRoles.OperatorAgent, IamScope.ForProject("alpha")));
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var actor = $"operator-agent:{agentId}/session:{sessionId}";
        var auditStart = store.ControlPlaneAudit.HeadSequence + 1;
        const string reason = "Delivered outside Studio and verified by hand on stage.";
        var body = new { targetState = TaskStates.Completed, operatorOverride = true, reason };

        var refused = await AssertRefused(Move(agent, "alpha-review", body, source: "owner-chat", reason: "Accept the verified card"),
            "approval-required");
        Assert.Equal((OperatorOperationIds.CardAcceptOverride, "T2"),
            (refused.GetProperty("operation").GetString(), refused.GetProperty("tier").GetString()));
        var approvalId = refused.GetProperty("approvalId").GetString()!;
        Assert.Equal(TaskStates.HumanReview, CardState(factory, "alpha-review"));

        await StepUpAsync(factory, owner);
        await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve", new { }));
        await ReadOk(Move(agent, "alpha-review", body, approvalId));

        Assert.Equal(TaskStates.Completed, CardState(factory, "alpha-review"));
        var used = await Approval(owner, approvalId);
        Assert.Equal(("used", 200), (used.GetProperty("state").GetString(), used.GetProperty("resultStatus").GetInt32()));
        await AssertRefused(Move(agent, "alpha-review", body, approvalId), "approval-used");

        // The timeline names the session as actor, the approving Owner, and the reason.
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("alpha-review", ProjectPath("alpha"))!;
        var events = factory.Services.GetRequiredService<TimelineLog>().ReadAll(card.FolderPath)
            .Where(evt => evt.Details?.GetValueOrDefault("approvalId") == approvalId).ToList();
        Assert.NotEmpty(events);
        Assert.All(events, evt => Assert.Equal((actor, "human:" + ownerId), (evt.Actor, evt.Details!["approvedBy"])));
        Assert.Contains(events, evt => evt.Details!.Values.Contains(reason));

        var executed = store.ControlPlaneAudit.Read(auditStart, 10_000).Single(entry => entry.Stream == ControlPlaneAuditStreams.OperatorAgent
            && entry.Operation == "POST /api/tasks/alpha-review/move" && entry.Outcome == "200");
        Assert.Equal(actor, executed.Actor);
        Assert.Equal((approvalId, ownerId, reason, OperatorOperationIds.CardAcceptOverride),
            (executed.Details!.Value.GetProperty("approvalId").GetString(), executed.Details.Value.GetProperty("approvedBy").GetString(),
                executed.Details.Value.GetProperty("reason").GetString(), executed.Details.Value.GetProperty("operation").GetString()));
    }

    /// <summary>
    /// ES-2: an archive override by an operator session takes the same
    /// approval path; without a written reason it is refused with the move
    /// routes' reason error and opens no approval request. A plain move into
    /// Archive stays outside the catalogue.
    /// </summary>
    [Fact]
    public async Task An_archive_override_by_an_operator_session_follows_the_approval_path_and_needs_its_reason()
    {
        using var factory = BuildFactory(("alpha-done", TaskStates.Completed));
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, sessionId, token) = StartSession(factory, new IamGrantRequest(IamRoles.OperatorAgent, IamScope.ForProject("alpha")));
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        const string reason = "Duplicate of alpha-card; the delivery is not on a protected branch.";

        using (var missing = await Move(agent, "alpha-done", new { targetState = TaskStates.Archive, archiveOverride = true }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal(TaskCrudEndpoints.OverrideReasonRequired, await Error(missing));
        }
        Assert.Empty(store.ListOperatorApprovals(agentId));
        await AssertRefused(Move(agent, "alpha-done", new { targetState = TaskStates.Archive, reason }), "operation-not-catalogued");

        var body = new { targetState = TaskStates.Archive, archiveOverride = true, reason };
        var refused = await AssertRefused(Move(agent, "alpha-done", body, source: "owner-chat"), "approval-required");
        Assert.Equal(OperatorOperationIds.CardArchiveOverride, refused.GetProperty("operation").GetString());
        var approvalId = refused.GetProperty("approvalId").GetString()!;
        Assert.Equal(TaskStates.Completed, CardState(factory, "alpha-done"));

        await StepUpAsync(factory, owner);
        await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/operator-approvals/{approvalId}/approve", new { }));
        await ReadOk(Move(agent, "alpha-done", body, approvalId));

        Assert.Equal(TaskStates.Archive, CardState(factory, "alpha-done"));
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("alpha-done", ProjectPath("alpha"))!;
        var archived = Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(card.FolderPath),
            evt => evt.Kind == "archive_override");
        Assert.Equal(($"operator-agent:{agentId}/session:{sessionId}", "human:" + ownerId, reason),
            (archived.Actor, archived.Details!["approvedBy"], archived.Details["reason"]));
        var row = JsonDocument.Parse(File.ReadLines(Path.Combine(ProjectPath("alpha"), ".audit", "archive-overrides.jsonl")).Single()).RootElement;
        Assert.Equal((approvalId, "human:" + ownerId, reason),
            (row.GetProperty("approvalId").GetString(), row.GetProperty("approvedBy").GetString(), row.GetProperty("reason").GetString()));
    }

    /// <summary>
    /// ES-2: an operator agent bound on the workspace resolves the card's
    /// project for a card write, so a requeue under its standing grant
    /// succeeds instead of reading as an installation-wide change.
    /// </summary>
    [Fact]
    public async Task A_workspace_bound_operator_session_moves_a_card_into_ready()
    {
        using var factory = BuildFactory();
        var (agentId, _, token) = StartSession(factory, installationScope: true);
        using var owner = await OwnerClientAsync(factory);
        using var agent = BearerClient(factory, token);
        await StepUpAsync(factory, owner);
        await ReadOk(owner.PostAsJsonAsync("/api/auth/iam/standing-grants", new CreateStandingGrantRequest(agentId,
            [OperatorOperationIds.CardRequeue], new StandingGrantScope(OperatorScopeKinds.Project, "alpha"), 10, null, 7,
            "Requeue cards in alpha")));

        await ReadOk(Move(agent, "alpha-card", new { targetState = TaskStates.Ready }));
        Assert.Equal(TaskStates.Ready, CardState(factory, "alpha-card"));
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

    private static Task<HttpResponseMessage> Move(HttpClient client, string card, object body, string? approvalId = null,
        string? source = null, string? reason = null)
    {
        var request = Request(HttpMethod.Post, $"/api/tasks/{card}/move?project=alpha", approvalId, source, reason);
        request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private string? CardState(WebApplicationFactory<Program> factory, string card)
    {
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        return scanner.FindJob(card, ProjectPath("alpha"))?.State;
    }

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

    private string CurrentCode()
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
            username = "first.owner", password = Password, secondFactorCode = CurrentCode(),
        });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-Token", (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!);
        return client;
    }

    private static (string AgentId, string SessionId, string Token) StartSession(WebApplicationFactory<Program> factory,
        bool installationScope = false)
        => StartSession(factory, installationScope
            ? new IamGrantRequest(IamRoles.OperatorAgent, IamScope.Workspace)
            : new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha")));

    private static (string AgentId, string SessionId, string Token) StartSession(WebApplicationFactory<Program> factory,
        IamGrantRequest grant)
    {
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("ops console", null, [grant]), ownerId);
        var started = store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), ownerId);
        return (agent.Id, started.Session.Id, started.Secret);
    }

    private WebApplicationFactory<Program> BuildFactory(params (string Name, string State)[] extraCards)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(ProjectPath("alpha"), state));
        var order = 1;
        foreach (var (name, lane) in new[] { ("alpha-card", TaskStates.Backlog), ("alpha-second", TaskStates.Backlog) }.Concat(extraCards))
        {
            var card = Path.Combine(ProjectPath("alpha"), lane, name);
            Directory.CreateDirectory(card);
            File.WriteAllText(Path.Combine(card, "task.json"), JsonSerializer.Serialize(new
            {
                id = name, title = name, state = lane, order = order++, agent = "codex", kind = "task",
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
                // The notifier is the background reader of the approval notice
                // queue; test hosts start product workers only on request.
                services.AddHostedService(provider => provider.GetRequiredService<OperatorApprovalNotifier>());
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
