using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed partial class TaskServerStoreTests
{
    [Fact]
    public async Task Operator_move_revokes_claim_and_old_completion_cannot_change_lane()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        Assert.Equal(claim.Run!.BriefVersion,
            (await store.GetTaskHistoryAsync(project.ProjectId, task.TaskId, 0, default))!.Runs.Single().BriefVersion);

        await store.MoveTaskAsync(project.ProjectId, task.TaskId,
            new MoveTaskRequest("0-backlog", Reason: "Sharpen brief", RunIntent: "revoke"),
            "human:owner", default);
        var stopped = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RenewLeaseAsync(
            claim.Run!.RunId,
            new LeaseRenewRequest("runner-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence),
            "runner-a", default));
        Assert.Equal("lease-not-active", stopped.Code);
        var denied = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            claim.Run!.RunId, Completion(claim, "old-completion"), "runner-a", default));
        Assert.Contains(denied.Code, new[] { "lease-not-active", "stale-fence" });
        Assert.Equal("0-backlog", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
        const string quarantineSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var quarantineBranch = $"agent-studio/quarantine/runner-a/task/run/fence-1/{quarantineSha}";
        var retainedLease = claim.Lease!;
        await store.RecordRevokedRunReferenceAsync(claim.Run.RunId,
            new RevokedRunReferenceRequest("runner-a", "instance-a", retainedLease.LeaseId,
                retainedLease.Fence, quarantineBranch, quarantineSha), "runner-a", default);
        var history = (await store.GetTaskHistoryAsync(project.ProjectId, task.TaskId, 0, default))!;
        Assert.Contains(history.Audit, item => item.Action == "run.quarantine-retained"
            && item.DetailJson.Contains(quarantineBranch, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Live_run_move_requires_explicit_intent()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        var denied = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.MoveTaskAsync(
            project.ProjectId, task.TaskId, new MoveTaskRequest("0-backlog"), "human:owner", default));

        Assert.Equal("run-intent-required", denied.Code);
        Assert.Equal("3-progress", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
    }

    [Fact]
    public async Task Progress_card_without_a_live_lease_does_not_require_run_intent()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
            new UpdateTaskRequest(null, null, "3-progress", task.Version), "test", default);

        await store.MoveTaskAsync(project.ProjectId, task.TaskId,
            new MoveTaskRequest("0-backlog"), "human:owner", default);

        Assert.Equal("0-backlog", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
    }

    [Fact]
    public async Task Explicit_continue_steer_keeps_claim_deliverable()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        await store.ContinueTaskAsync(project.ProjectId, task.TaskId,
            new ContinueTaskRequest("Continue with this correction.", Mode: "steer"),
            "human:owner", default);
        await store.CompleteRunAsync(claim.Run!.RunId, Completion(claim, "steered-completion"), "runner-a", default);

        Assert.Equal("4-auto-review", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
    }

    [Fact]
    public async Task Edited_brief_parks_old_completion_as_offer()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var active = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
            new UpdateTaskRequest(null, "Sharpened brief", null, active.Version), "human:owner", default);

        await store.CompleteRunAsync(claim.Run!.RunId, Completion(claim, "older-brief-completion"), "runner-a", default);

        Assert.Equal("5e-escalated", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
        Assert.Equal("pending", (await store.GetOlderBriefDeliveryAsync(project.ProjectId, task.TaskId, default))!.Status);
    }

    [Fact]
    public async Task Older_brief_choice_accepts_immutable_result_only_after_operator_decision()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var active = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
            new UpdateTaskRequest(null, "New brief", null, active.Version), "human:owner", default);
        var handoff = await store.AcknowledgeResultHandoffAsync(
            claim.Run!.RunId, Handoff(claim.Run.RunId, claim.Lease!, 1), "runner-a", default);
        await store.CompleteRunAsync(claim.Run.RunId, new CompleteRunRequest(
            "runner-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence, "success",
            ResultEnvelopeDigest: handoff.EnvelopeDigest, IdempotencyKey: "old-success", Sequence: 2),
            "runner-a", default);
        Assert.Equal("5e-escalated", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);

        await store.DecideOlderBriefDeliveryAsync(project.ProjectId, task.TaskId, "accept", "human:owner", default);

        Assert.Equal("4-auto-review", (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!.State);
        Assert.Equal("accept", (await store.GetOlderBriefDeliveryAsync(project.ProjectId, task.TaskId, default))!.Status);
    }

    [Fact]
    public async Task Older_brief_starting_point_is_consumed_by_next_claim()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var active = (await store.GetTaskAsync(project.ProjectId, task.TaskId, default))!;
        await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
            new UpdateTaskRequest(null, "New brief", null, active.Version), "human:owner", default);
        const string salvageRef = "refs/heads/agent-studio/salvage/old-run";
        const string salvageSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await store.CompleteRunAsync(claim.Run!.RunId, Completion(claim, "old-salvage") with
        {
            SalvageBranch = salvageRef,
            SalvageCommitSha = salvageSha,
        }, "runner-a", default);

        await store.DecideOlderBriefDeliveryAsync(project.ProjectId, task.TaskId,
            "starting-point", "human:owner", default);
        var next = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        Assert.Equal(salvageRef, next.ContinuationBaseRef);
        Assert.Equal(salvageSha, next.ContinuationBaseSha);
        Assert.Equal("starting-point", (await store.GetOlderBriefDeliveryAsync(project.ProjectId, task.TaskId, default))!.Status);
    }

    private static CompleteRunRequest Completion(ClaimResponse claim, string key)
        => new("runner-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence,
            ExecutionOutcomeKind.LaunchFailure.ToString(),
            IdempotencyKey: key, Sequence: 1);
}
