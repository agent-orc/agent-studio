using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class RepeatedReviewBlockStoreTests
{
    [Fact]
    public async Task Review_run_retry_uses_original_payload_for_idempotency()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Review retry"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Review retry", "RRT"),
            "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId,
            new CreateTaskRequest("Review retry", State: "4-auto-review"),
            "test", default);
        await store.UpsertFlowDefinitionAsync(project.ProjectId,
            new UpsertFlowDefinitionRequest(0, [OrchestrationStage.ReviewDecision], 5),
            "test", default);

        var request = new CreateOrchestrationRunRequest(task.TaskId, BlockingPayload(), "review-retry");
        var first = await store.CreateOrchestrationRunAsync(project.ProjectId, request, "test", default);
        var stored = await store.GetOrchestrationRunAsync(first.RunId, default);
        Assert.NotEqual(request.PayloadJson, stored!.PayloadJson);

        var replay = await store.CreateOrchestrationRunAsync(project.ProjectId, request, "test", default);
        Assert.Equal(first.RunId, replay.RunId);
        var changed = request with { PayloadJson = request.PayloadJson.Replace("subject", "other-subject") };
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CreateOrchestrationRunAsync(project.ProjectId, changed, "test", default));
        Assert.Equal("idempotency-conflict", conflict.Code);
    }

    [Fact]
    public async Task Same_requirement_fit_block_degrades_with_linked_follow_up_when_budget_is_spent()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Review loops"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Review loops", "RLP"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest(
                "Implement all Dossier recommendations",
                "Implement all recommendations from the approved Dossier.",
                "4-auto-review"),
            "test",
            default);
        await store.UpsertFlowDefinitionAsync(
            project.ProjectId,
            new UpsertFlowDefinitionRequest(
                0,
                [OrchestrationStage.ReviewDecision],
                MaxReissueAttempts: 5),
            "test",
            default);

        var payload = BlockingPayload();
        var first = await SettleAsync(store, project.ProjectId, task.TaskId, payload, 1);
        Assert.Equal("reissued", first.Status);
        var ready = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        Assert.Equal("2-ready", ready.State);
        await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "4-auto-review", ready.Version),
            "test",
            default);

        var second = await SettleAsync(store, project.ProjectId, task.TaskId, payload, 2);

        Assert.Equal("reissued", second.Status);
        ready = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
            new UpdateTaskRequest(null, null, "4-auto-review", ready.Version), "test", default);
        var third = await SettleAsync(store, project.ProjectId, task.TaskId, payload, 3);
        Assert.Equal("completed", third.Status);
        Assert.Equal("5-human-review", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
        var followUp = Assert.Single(await store.ListTasksAsync(project.ProjectId, default),
            item => item.TaskId != task.TaskId);
        Assert.Equal(StudioTaskLanes.Ready, followUp.State);
        Assert.Contains(followUp.State, StudioTaskLanes.All);
        Assert.Contains("Required S1 slice left undelivered", followUp.Body);
        Assert.Contains(task.TaskId, followUp.Body);
        Assert.Contains(followUp.TaskKey, (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.Body);
        var settlementAudit = (await store.ListAuditAsync(0, default))
            .Last(record => record.Action == "orchestration.stage-completed");
        Assert.Contains("RequiresLinkedFollowUp", settlementAudit.DetailJson);

    }

    private static async Task<OrchestrationRunDto> SettleAsync(
        TaskServerStore store,
        string projectId,
        string taskId,
        string payload,
        int round)
    {
        var reviewPayload = System.Text.Json.Nodes.JsonNode.Parse(payload)!;
        reviewPayload["reviewAttemptId"] = $"review-{round}";
        payload = reviewPayload.ToJsonString();
        var run = await store.CreateOrchestrationRunAsync(
            projectId,
            new CreateOrchestrationRunRequest(taskId, payload, $"run-{round}"),
            "test",
            default);
        var claim = await store.ClaimOrchestrationAsync(
            new OrchestrationClaimRequest(
                "engine", "instance", [OrchestrationStage.ReviewDecision]),
            "engine",
            default);
        Assert.Equal(run.RunId, claim.Run!.RunId);
        return await store.CompleteOrchestrationStageAsync(
            run.RunId,
            new CompleteOrchestrationStageRequest(
                "engine",
                "instance",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                OrchestrationStage.ReviewDecision,
                OrchestrationAction.Reissue,
                "{}",
                $"settle-{round}"),
            "engine",
            default);
    }

    private static string BlockingPayload()
        => JsonSerializer.Serialize(new
        {
            reviewSubjectId = "subject",
            reviewAttemptId = "review",
            resultSha = new string('a', 40),
            reviewPolicyHash = "policy",
            reviewReportSha256 = new string('b', 64),
            reviewOutcome = "ProductFailure",
            failureClassification = "RequirementFit",
            summary = "The broad card is incomplete even though its build passed.",
            verdicts = new[]
            {
                new ReviewVerdictDto(
                    "build-tests", "pass", "Verified", "Build and tests passed."),
                new ReviewVerdictDto(
                    "requirement-fit",
                    "block",
                    "MissingScope",
                    "Required S1 slice left undelivered despite approval to implement all recommendations."),
            },
            gates = new[]
            {
                new ReviewOrchestrationGateDto(
                    "verify-build", "build-tests", "passed", "Verified"),
            },
        });
}
