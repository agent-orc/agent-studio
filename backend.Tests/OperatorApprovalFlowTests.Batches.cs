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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Batch_item_preserves_move_result_when_audit_append_fails(bool executed)
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, _, _) = StartSession(factory, installationScope: true);
        var login = store.Login("first.owner", Password, "batch audit failure", CurrentCode());
        var human = store.AuthenticateSession(login.SessionToken)!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;
        var start = store.StartOperatorSignIn(new(new("EC", "P-256", OperatorDevicePublicKey.Base64Url(point.X!),
            OperatorDevicePublicKey.Base64Url(point.Y!)), "batch console", agentId, ["cards.archive-override"]), "127.0.0.1", "test");
        store.VerifyStepUp(human, CurrentCode());
        store.ConfirmOperatorSignIn(new(start.UserCode, agentId, ["cards.archive-override"], 8), human, "127.0.0.1");
        var principal = store.AuthenticateOperatorSession(store.PollOperatorSignIn(start.PollHandle).Token)!;
        var item = new OperatorBatchItemRequest("alpha-done", TaskStates.Archive, "Duplicate without delivery", "alpha");
        var batch = store.SubmitOperatorBatch(principal, new("cards.archive-override", [item], "owner-chat"),
            [new(0, item, "Completed card", "revision", null, null)]);
        store.VerifyStepUp(human, CurrentCode());
        store.DecideOperatorBatch(batch.Id, new([0]), human);

        // A directory at the audit path forces an append failure on every platform.
        var auditPath = store.ControlPlaneAudit.FilePath;
        var backupPath = auditPath + ".backup";
        File.Move(auditPath, backupPath);
        Directory.CreateDirectory(auditPath);
        const string reason = "The guarded move returned its result.";
        try
        {
            var recorded = store.RecordBatchItem(batch.Id, 0, executed, reason);
            Assert.Equal(executed ? "executed" : "skipped", Assert.Single(recorded.Items).State);
            var finished = store.FinishOperatorBatch(batch.Id);
            Assert.Equal(executed ? "executed" : "skipped", Assert.Single(finished.Items).State);
            Assert.Equal(reason, Assert.Single(finished.Items).ResultReason);
            Assert.Equal(finished, Assert.Single(store.ListOperatorBatches()));
        }
        finally
        {
            Directory.Delete(auditPath);
            File.Move(backupPath, auditPath);
        }
    }

    [Fact]
    public async Task Batch_confirmation_runs_once_and_skips_deselected_changed_and_nonqualifying_items()
    {
        using var factory = BuildFactory(("alpha-done", TaskStates.Completed), ("alpha-skip", TaskStates.Completed), ("alpha-stale", TaskStates.Completed));
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var (agentId, _, unboundToken) = StartSession(factory, installationScope: true);
        using (var unbound = BearerClient(factory, unboundToken))
            await AssertRefused(unbound.PostAsJsonAsync(OperatorBatchEndpoints.SessionPath,
                new SubmitOperatorBatchRequest("cards.archive-override", [], "owner-chat")), "batch-class-required");
        var login = store.Login("first.owner", Password, "batch confirmation", CurrentCode());
        var human = store.AuthenticateSession(login.SessionToken)!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;
        var start = store.StartOperatorSignIn(new(new("EC", "P-256", OperatorDevicePublicKey.Base64Url(point.X!),
            OperatorDevicePublicKey.Base64Url(point.Y!)), "batch console", agentId, ["cards.archive-override", "cards.accept-override"]), "127.0.0.1", "test");
        store.VerifyStepUp(human, CurrentCode());
        store.ConfirmOperatorSignIn(new(start.UserCode, agentId, ["cards.archive-override", "cards.accept-override"], 8), human, "127.0.0.1");
        var issued = store.PollOperatorSignIn(start.PollHandle);
        using var agent = BearerClient(factory, issued.Token!);
        using var owner = await OwnerClientAsync(factory);
        async Task<HttpResponseMessage> SendBatch(string classId, params OperatorBatchItemRequest[] items)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OperatorBatchEndpoints.SessionPath)
                { Content = JsonContent.Create(new SubmitOperatorBatchRequest(classId, items, "owner-chat")) };
            request.Headers.Add("DPoP", OperatorDpopTests.Proof(key, issued.Token!, "POST",
                new Uri(agent.BaseAddress!, OperatorBatchEndpoints.SessionPath).GetLeftPart(UriPartial.Path), _clock.GetUtcNow()));
            return await agent.SendAsync(request);
        }
        Task<JsonElement> Submit(string classId, params OperatorBatchItemRequest[] items) => ReadOk(SendBatch(classId, items));
        OperatorBatchItemRequest Archive(string card) => new(card, TaskStates.Archive, "Duplicate without delivery", "alpha");
        using (var oversized = await SendBatch("cards.archive-override", Enumerable.Repeat(Archive("alpha-done"), 26).ToArray()))
            Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        var batch = await Submit("cards.archive-override", Archive("alpha-done"), Archive("alpha-skip"), Archive("alpha-stale"), Archive("missing"));
        var id = batch.GetProperty("id").GetString()!;
        Assert.NotEqual(JsonValueKind.Null, batch.GetProperty("items")[3].GetProperty("refusal").ValueKind);
        Assert.Single(new OperatorApprovalFactSource(store).Collect(_clock.GetUtcNow().UtcDateTime));
        await AssertRefused(owner.PostAsJsonAsync($"/api/auth/iam/operator-batches/{id}/decide", new { selected = new[] { 0 } }), "step-up-required");
        await ReadOk(owner.PutAsJsonAsync("/api/tasks/alpha-stale/title?project=alpha", new { title = "Changed by Owner" }));
        using (var invalid = await owner.PostAsJsonAsync($"/api/auth/iam/operator-batches/{id}/decide", new { selected = new[] { 3 } }))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await StepUpAsync(factory, owner);
        var result = await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/operator-batches/{id}/decide", new { selected = new[] { 0, 2 } }));
        Assert.Equal("confirmed", result.GetProperty("state").GetString());
        var resultPath = OperatorBatchEndpoints.SessionPath + "/" + id;
        using (var read = new HttpRequestMessage(HttpMethod.Get, resultPath))
        {
            read.Headers.Add("DPoP", OperatorDpopTests.Proof(key, issued.Token!, "GET",
                new Uri(agent.BaseAddress!, resultPath).GetLeftPart(UriPartial.Path), _clock.GetUtcNow()));
            Assert.Equal("confirmed", (await ReadOk(agent.SendAsync(read))).GetProperty("state").GetString());
        }
        using (var otherSession = BearerClient(factory, unboundToken))
        using (var hidden = await otherSession.GetAsync(resultPath))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("executed", result.GetProperty("items")[0].GetProperty("state").GetString());
        Assert.Contains("Deselected", result.GetProperty("items")[1].GetProperty("resultReason").GetString());
        Assert.Contains("changed since submission", result.GetProperty("items")[2].GetProperty("resultReason").GetString());
        Assert.Equal(TaskStates.Archive, CardState(factory, "alpha-done"));
        Assert.Equal(TaskStates.Completed, CardState(factory, "alpha-skip"));
        Assert.Empty(new OperatorApprovalFactSource(store).Collect(_clock.GetUtcNow().UtcDateTime));
        using var replay = await owner.PostAsJsonAsync($"/api/auth/iam/operator-batches/{id}/decide", new { selected = new[] { 0 } });
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("alpha-done", ProjectPath("alpha"))!;
        var entry = Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(card.FolderPath), evt => evt.Kind == "archive_override");
        Assert.Equal(id, entry.Details!["batchId"]);
        Assert.Equal("human:" + human.User.Id, entry.Details["approvedBy"]);
        Assert.StartsWith("operator-agent:", entry.Actor);
        var actionAudit = Assert.Single(store.ControlPlaneAudit.ReadNewest(audit => audit.Operation == OperatorOperationIds.CardArchiveOverride
            && audit.Outcome == "executed", 20));
        Assert.Equal(id, actionAudit.Details!.Value.GetProperty("batchId").GetString());
        Assert.Equal("cards.archive-override", actionAudit.Details.Value.GetProperty("elevationClass").GetString());
        var rejected = await Submit("cards.archive-override", Archive("alpha-skip"));
        var rejectedId = rejected.GetProperty("id").GetString()!;
        await StepUpAsync(factory, owner);
        await ReadOk(owner.PostAsJsonAsync($"/api/auth/iam/operator-batches/{rejectedId}/decide", new { selected = new[] { 0 }, approve = false }));
        Assert.Equal(TaskStates.Completed, CardState(factory, "alpha-skip"));
        var expired = await Submit("cards.archive-override", Archive("alpha-skip"));
        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal("expired", store.ListOperatorBatches().Single(item => item.Id == expired.GetProperty("id").GetString()).State);
        Assert.Empty(new OperatorApprovalFactSource(store).Collect(_clock.GetUtcNow().UtcDateTime));
        using var late = await owner.PostAsJsonAsync($"/api/auth/iam/operator-batches/{expired.GetProperty("id").GetString()}/decide", new { selected = new[] { 0 } });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        var nonqualifying = await Submit("cards.accept-override", new OperatorBatchItemRequest("alpha-card", TaskStates.Completed, "Accept with reservation", "alpha", "Naming concern"));
        Assert.NotEqual(JsonValueKind.Null, nonqualifying.GetProperty("items")[0].GetProperty("refusal").ValueKind);
    }
}
