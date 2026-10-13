using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class RepeatedReviewBlockStoreTests
{
    [Fact]
    public async Task Same_requirement_fit_block_degrades_to_one_linked_follow_up()
    {
        using var temp = new TempDirectory();
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions { DataDirectory = temp.Path }),
            TimeProvider.System);
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
        var secondReady = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
            new UpdateTaskRequest(null, null, "4-auto-review", secondReady.Version), "test", default);
        var third = await SettleAsync(store, project.ProjectId, task.TaskId, payload, 3);
        Assert.Equal("completed", third.Status);
        Assert.Equal(2, third.ReissueAttempts);
        Assert.Equal("5-human-review", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
        var followUp = await store.GetTaskAsync(project.ProjectId, "review_budget_" + task.TaskId, default);
        Assert.NotNull(followUp);
        Assert.Equal("2-ready", followUp!.State);
        Assert.Contains("Required S1 slice left undelivered", followUp.Body);
        var settlementAudit = (await store.ListAuditAsync(0, default))
            .Last(record => record.Action == "orchestration.stage-completed");
        Assert.Contains("SettlementReason", settlementAudit.DetailJson);
        Assert.Contains("Required S1 slice left undelivered", settlementAudit.DetailJson);
    }

    private static async Task<OrchestrationRunDto> SettleAsync(
        TaskServerStore store,
        string projectId,
        string taskId,
        string payload,
        int round)
    {
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
