using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class OperatorApprovalFlowTests
{
    [Fact]
    public async Task Confirmed_testing_class_admits_changes_audits_and_feeds_owner_undo_and_refuses_agent_rearm()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, _, _) = StartSession(factory, installationScope: true);
        using var owner = await OwnerClientAsync(factory);
        var humanLogin = store.Login("first.owner", Password, "elevation confirmation", CurrentCode());
        var human = store.AuthenticateSession(humanLogin.SessionToken)!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;
        var start = store.StartOperatorSignIn(new(new("EC", "P-256", OperatorDevicePublicKey.Base64Url(point.X!),
            OperatorDevicePublicKey.Base64Url(point.Y!)), "test console", agentId, ["project.testing"]), "127.0.0.1", "test");
        store.VerifyStepUp(human, CurrentCode());
        var confirmed = store.ConfirmOperatorSignIn(new(start.UserCode, agentId, ["project.testing"], 24), human, "127.0.0.1");
        var issued = store.PollOperatorSignIn(start.PollHandle);
        using var agent = BearerClient(factory, issued.Token!);
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, object body)
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("DPoP", OperatorDpopTests.Proof(key, issued.Token!, method.Method,
                new Uri(agent.BaseAddress!, path).GetLeftPart(UriPartial.Path), _clock.GetUtcNow()));
            request.Headers.Add(ReasonHeader, "Owner requested strict testing");
            request.Headers.Add(SourceHeader, "owner-chat");
            return await agent.SendAsync(request);
        }
        var result = await ReadOk(Send(HttpMethod.Put, "/api/projects/alpha/testing", new { strategy = "strict" }));
        Assert.Equal("strict", result.GetProperty("strategy").GetString());
        Assert.Empty(store.ListOperatorApprovals());
        var audit = Assert.Single(store.ControlPlaneAudit.ReadNewest(e => e.Operation == "project.testing.set", 20));
        Assert.Equal("project.testing", audit.Details!.Value.GetProperty("elevationClass").GetString());
        Assert.NotEmpty(audit.Details.Value.GetProperty("armingId").GetString()!);
        Assert.Equal(start.SignIn.KeyThumbprint, audit.Details.Value.GetProperty("deviceThumbprint").GetString());
        Assert.Equal(human.User.Id, audit.Details.Value.GetProperty("confirmingOwner").GetString());
        Assert.Equal("owner-chat", audit.Details.Value.GetProperty("instructionSource").GetString());
        var activity = await owner.GetFromJsonAsync<JsonElement>($"/api/auth/audit/operator-activity?operatorAgentId={agentId}&sessionId={confirmed.SessionId}&elevatedOnly=true");
        Assert.NotEmpty(activity.GetProperty("entries").EnumerateArray());
        Assert.All(activity.GetProperty("entries").EnumerateArray(), entry =>
        {
            Assert.Equal(confirmed.SessionId, entry.GetProperty("session").GetString());
            Assert.Equal("project.testing", entry.GetProperty("details").GetProperty("elevationClass").GetString());
        });
        var feed = await owner.GetFromJsonAsync<JsonElement>("/api/runner/orchestrator-feed");
        Assert.Contains(feed.GetProperty("entries").EnumerateArray(), entry => entry.TryGetProperty("testingUndoSequence", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt64() == audit.Sequence);
        await AssertRefused(Send(HttpMethod.Post, $"/api/auth/iam/operator-agents/{agentId}/sessions/{confirmed.SessionId}/rearm", new { classId = "project.testing" }), "token-route-denied");
        var rearmRoute = $"/api/auth/iam/operator-agents/{agentId}/sessions/{confirmed.SessionId}/rearm";
        await AssertRefused(owner.PostAsJsonAsync(rearmRoute, new { classId = "project.testing" }), "step-up-required");
        await StepUpAsync(factory, owner);
        await ReadOk(owner.PostAsJsonAsync(rearmRoute, new { classId = "project.testing" }));
        await AssertRefused(Send(HttpMethod.Post, $"/api/projects/alpha/testing/undo/{audit.Sequence}", new { }), "operator-route-denied");
        var restored = await ReadOk(owner.PostAsJsonAsync($"/api/projects/alpha/testing/undo/{audit.Sequence}", new { }));
        Assert.Equal("fast", restored.GetProperty("strategy").GetString());
        var undo = store.ControlPlaneAudit.ReadNewest(e => e.Operation == "project.testing.set", 1).Single();
        Assert.False(undo.Actor.StartsWith("operator-agent:", StringComparison.Ordinal));
        Assert.Equal(audit.Sequence, undo.Details!.Value.GetProperty("undoSequence").GetInt64());
        using var replay = await owner.PostAsJsonAsync($"/api/projects/alpha/testing/undo/{audit.Sequence}", new { });
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        for (var i = 0; i < 5; i++) await ReadOk(Send(HttpMethod.Put, "/api/projects/alpha/testing", new { strategy = "balanced" }));
        for (var i = 0; i < 3; i++) await AssertRefused(Send(HttpMethod.Put, "/api/projects/alpha/testing", new { strategy = "strict" }), "elevation-rate-exceeded");
        await AssertRefused(Send(HttpMethod.Put, "/api/projects/alpha/testing", new { strategy = "strict" }), "approval-required");
        Assert.Single(new OperatorElevationFactSource(store).Collect(_clock.GetUtcNow().UtcDateTime));
    }
}
