using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class HostOrchestratorStoreTests
{
    [Fact]
    public async Task Host_report_permit_queue_restart_reconcile_and_post_processing_form_one_fenced_flow()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        Assert.Contains("host-orchestrator", first.Status().Protocol.Capabilities ?? []);
        var workspace = await first.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await first.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"),
            "test",
            default);
        var task = await first.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Task", "Do the work", "2-ready"),
            "test",
            default);
        var delta = new MechanicalRoundDelta(new string('b', 40),
            "refs/heads/result", new string('a', 40), ["src/Feature.cs"],
            "Resolve the conflict.", "Run focused tests.");
        await first.UpdateTaskAsync(project.ProjectId, task.TaskKey,
            new UpdateTaskRequest(null, null, "2-ready", task.Version, delta), "test", default);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "runner-a", default);

        var report1 = Report(
            sequence: 1,
            capacity: new HostCapacityDto(2, 2, 0, 0, 2));
        var available = await first.AcceptHostReportAsync("runner-a", report1, "runner-a", default);
        var permit = Assert.Single(available.AvailableWork);

        var acceptance = await first.AcceptWorkPermitAsync(
            permit.PermitId,
            new WorkPermitAcceptRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-a",
                "runner-a",
                available.AcceptedSequence,
                available.PolicyVersion,
                "accept-once"),
            "runner-a",
            default);
        Assert.Equal("accepted", acceptance.Status);
        Assert.Equal(delta.DeliverySha, acceptance.MechanicalDelta?.DeliverySha);
        Assert.Equal("pending-mechanical-continuation", acceptance.MechanicalFreshRoute?.Reason);
        Assert.Equal("gpt-5.6-terra", acceptance.MechanicalFreshRoute?.Model);
        var replay = await first.AcceptWorkPermitAsync(
            permit.PermitId,
            new WorkPermitAcceptRequest(
                HostOrchestratorContract.Current, "host-a", "instance-a", "runner-a",
                available.AcceptedSequence, available.PolicyVersion, "accept-once"),
            "runner-a", default);
        Assert.Equal(delta.DeliverySha, replay.MechanicalDelta?.DeliverySha);
        Assert.Equal(acceptance.MechanicalFreshRoute, replay.MechanicalFreshRoute);
        Assert.Equal(2, acceptance.PostProcessingPlan.Count);
        var postStep = Assert.Single(
            acceptance.PostProcessingPlan,
            step => step.StepId == HostPostStepIds.WorktreeContainment);
        var resultStep = Assert.Single(
            acceptance.PostProcessingPlan,
            step => step.StepId == HostPostStepIds.ResultFinalization);
        Assert.Equal("runner-a", postStep.EligibleRunnerId);
        Assert.Equal("runner-a", resultStep.EligibleRunnerId);

        var queuedWork = Work(acceptance, "queued", queuePosition: 0);
        var report2 = Report(
            sequence: 2,
            capacity: new HostCapacityDto(2, 2, 0, 1, 2),
            work: [queuedWork]);
        await first.AcceptHostReportAsync("runner-a", report2, "runner-a", default);
        var queuedProjection = Assert.Single(await first.ListHostProjectionsAsync(default));
        Assert.Equal(2, queuedProjection.Sequence);
        Assert.Equal(1, queuedProjection.Capacity.Queued);
        Assert.Equal("queued", Assert.Single(queuedProjection.Work).Phase);

        var incomplete = await Assert.ThrowsAsync<TaskServerConflictException>(() => first.CompleteRunAsync(
            acceptance.Run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                "success"),
            "runner-a",
            default));
        Assert.Equal("host-post-processing-incomplete", incomplete.Code);

        // Simulate a central process restart while the host process continues.
        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        await restarted.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-replacement"),
            "runner-a",
            default);
        var runningWork = Work(acceptance, "running", processId: 4242);
        var report3 = Report(
            sequence: 3,
            capacity: new HostCapacityDto(2, 2, 1, 0, 1),
            work: [runningWork],
            instanceId: "instance-replacement");
        await restarted.AcceptHostReportAsync("runner-a", report3, "runner-a", default);
        var reconciled = await restarted.ReconcileRunAsync(
            acceptance.Run.RunId,
            new RunReconcileRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-replacement",
                "runner-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                3,
                LeaseInstanceId: "instance-a"),
            "runner-a",
            default);
        Assert.Equal("reconciled", reconciled.Status);
        Assert.Equal(acceptance.Lease.Fence, reconciled.Lease.Fence);

        await restarted.RegisterRunnerAsync("runner-b", Runner("instance-b", "host-b"), "runner-b", default);
        var duplicate = await restarted.ClaimAsync(new ClaimRequest("runner-b", "instance-b"), "runner-b", default);
        Assert.Equal("empty", duplicate.Status);

        var claim = await restarted.ClaimPostStepAsync(
            acceptance.Run.RunId,
            postStep.StepExecutionId,
            new PostStepClaimRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-replacement",
                "runner-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                3,
                "post-claim-once",
                LeaseInstanceId: "instance-a"),
            "runner-a",
            default);
        var completion = await restarted.CompletePostStepAsync(
            acceptance.Run.RunId,
            postStep.StepExecutionId,
            new PostStepCompleteRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-replacement",
                "runner-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                claim.ClaimFence,
                "passed",
                ["sha256:host-evidence"],
                "post-complete-once",
                LeaseInstanceId: "instance-a"),
            "runner-a",
            default);
        Assert.Equal("completed", completion.Status);
        Assert.Equal("runner-a", completion.Step.EligibleRunnerId);
        var completedClaimReplay = await restarted.ClaimPostStepAsync(
            acceptance.Run.RunId,
            postStep.StepExecutionId,
            new PostStepClaimRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-replacement",
                "runner-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                3,
                "post-claim-once",
                LeaseInstanceId: "instance-a"),
            "runner-a",
            default);
        Assert.Equal("completed", completedClaimReplay.Status);

        var resultFinalization = await restarted.FinalizeResultAsync(
            acceptance.Run.RunId,
            new ResultFinalizationRequest(
                "runner-a",
                "instance-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                1,
                "result-finalization-once"),
            "runner-a",
            default);
        Assert.Equal(ResultFinalizationStatus.Ready, resultFinalization.Status);
        var resultClaim = await restarted.ClaimPostStepAsync(
            acceptance.Run.RunId,
            resultStep.StepExecutionId,
            new PostStepClaimRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-replacement",
                "runner-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                3,
                "result-step-claim-once",
                LeaseInstanceId: "instance-a"),
            "runner-a",
            default);
        await restarted.CompletePostStepAsync(
            acceptance.Run.RunId,
            resultStep.StepExecutionId,
            new PostStepCompleteRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-replacement",
                "runner-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                resultClaim.ClaimFence,
                "passed",
                [resultFinalization.ArtifactSha256!],
                "result-step-complete-once",
                LeaseInstanceId: "instance-a"),
            "runner-a",
            default);

        var envelope = new ImmutableResultEnvelope(
            "project",
            acceptance.Run.RunId,
            new string('1', 40),
            new string('2', 40),
            FencedGitRefs.ImmutableResult(
                acceptance.Run.RunId,
                acceptance.Lease.Fence,
                new string('2', 40)),
            null,
            new string('3', 64));
        var envelopeDigest = ResultEnvelopeDigest.Compute(envelope);
        await restarted.AcknowledgeResultHandoffAsync(
            acceptance.Run.RunId,
            new ResultHandoffRequest(
                "runner-a",
                "instance-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                1,
                $"handoff:{acceptance.Run.RunId}:{envelopeDigest}",
                envelopeDigest,
                envelope),
            "runner-a",
            default);
        await restarted.CompleteRunAsync(
            acceptance.Run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                acceptance.Lease.LeaseId,
                acceptance.Lease.Fence,
                "success",
                ResultEnvelopeDigest: envelopeDigest,
                IdempotencyKey: $"completion:{acceptance.Run.RunId}",
                Sequence: 2),
            "runner-a",
            default);
        var completedTask = await restarted.GetTaskAsync(project.ProjectId, task.TaskKey, default);
        Assert.Equal("4-auto-review", completedTask!.State);
        _ = await restarted.UpdateTaskAsync(
            project.ProjectId,
            task.TaskKey,
            new UpdateTaskRequest(null, null, "2-ready", completedTask.Version),
            "review-reissue",
            default);
        var nextReport = await restarted.AcceptHostReportAsync(
            "runner-a",
            Report(
                sequence: 4,
                capacity: new HostCapacityDto(2, 2, 0, 0, 2),
                instanceId: "instance-replacement"),
            "runner-a",
            default);
        Assert.NotEqual(permit.PermitId, Assert.Single(nextReport.AvailableWork).PermitId);
        var history = await restarted.GetTaskHistoryAsync(project.ProjectId, task.TaskKey, 0, default);
        var postProcessingEvent = Assert.Single(
            history!.Events,
            item => item.Kind == LifecycleEventKinds.PostProcessingCompleted);
        Assert.Contains("\"reviewAuthority\":\"task-server\"", postProcessingEvent.PayloadJson);

        var audit = await restarted.ListAuditAsync(0, default);
        Assert.Contains(audit, item => item.Action == "host.report.accepted");
        Assert.Contains(audit, item => item.Action == "work.permit.accepted");
        Assert.Contains(audit, item => item.Action == "run.reconciled");
        Assert.Contains(audit, item => item.Action == "post-step.completed");
    }

    [Fact]
    public async Task Report_sequence_is_idempotent_and_capacity_invariants_fail_closed()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "runner-a", default);
        var report = Report(1, new HostCapacityDto(4, 3, 1, 2, 2));

        var accepted = await store.AcceptHostReportAsync("runner-a", report, "runner-a", default);
        var replayed = await store.AcceptHostReportAsync("runner-a", report, "runner-a", default);
        Assert.Equal("accepted", accepted.Status);
        Assert.Equal("replayed", replayed.Status);

        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.AcceptHostReportAsync(
            "runner-a",
            report with { Faults = [new HostFaultDto("changed", "host", "different payload")] },
            "runner-a",
            default));
        Assert.Equal("host-report-sequence-conflict", conflict.Code);

        var invalid = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.AcceptHostReportAsync(
            "runner-a",
            Report(2, new HostCapacityDto(2, 2, 1, 0, 2)),
            "runner-a",
            default));
        Assert.Equal("host-capacity-invalid", invalid.Code);
        Assert.Equal(1, Assert.Single(await store.ListHostProjectionsAsync(default)).Sequence);

        var nonPositive = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.AcceptHostReportAsync(
            "runner-a",
            Report(0, new HostCapacityDto(2, 2, 0, 0, 2)),
            "runner-a",
            default));
        Assert.Equal("host-report-sequence-invalid", nonPositive.Code);
    }

    [Fact]
    public async Task Permit_acceptance_rechecks_central_task_state_and_host_identity()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest("Task", "Do the work", "2-ready"),
            "test",
            default);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "runner-a", default);
        var report = await store.AcceptHostReportAsync(
            "runner-a",
            Report(1, new HostCapacityDto(1, 1, 0, 0, 1)),
            "runner-a",
            default);
        var permit = Assert.Single(report.AvailableWork);

        var wrongHost = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.AcceptWorkPermitAsync(
            permit.PermitId,
            new WorkPermitAcceptRequest(
                HostOrchestratorContract.Current,
                "spoofed-host",
                "instance-a",
                "runner-a",
                report.AcceptedSequence,
                report.PolicyVersion,
                "wrong-host"),
            "runner-a",
            default));
        Assert.Equal("runner-host-mismatch", wrongHost.Code);

        _ = await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskKey,
            new UpdateTaskRequest(null, null, "0-backlog", task.Version),
            "test",
            default);
        var staleState = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.AcceptWorkPermitAsync(
            permit.PermitId,
            new WorkPermitAcceptRequest(
                HostOrchestratorContract.Current,
                "host-a",
                "instance-a",
                "runner-a",
                report.AcceptedSequence,
                report.PolicyVersion,
                "stale-state"),
            "runner-a",
            default));
        Assert.Equal("work-permit-task-not-ready", staleState.Code);
    }

    [Fact]
    public async Task Host_project_policy_filters_permits_and_is_rechecked_on_acceptance()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"),
            "test",
            default);
        var blockedProject = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Blocked", "BLK"),
            "test",
            default);
        await store.CreateTaskAsync(
            blockedProject.ProjectId,
            new CreateTaskRequest("Blocked task", "work", "2-ready"),
            "test",
            default);
        var allowedProject = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Allowed", "ALW"),
            "test",
            default);
        await store.CreateTaskAsync(
            allowedProject.ProjectId,
            new CreateTaskRequest("Allowed task", "work", "2-ready"),
            "test",
            default);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "runner-a", default);
        var policy = await store.UpdateHostProjectPolicyAsync(
            "host-a",
            new UpdateHostProjectPolicyRequest(false, [allowedProject.ProjectId], 0),
            "operator",
            default);

        var report = await store.AcceptHostReportAsync(
            "runner-a",
            Report(1, new HostCapacityDto(1, 1, 0, 0, 1)),
            "runner-a",
            default);
        var permit = Assert.Single(report.AvailableWork);
        Assert.Equal(allowedProject.ProjectId, permit.Task.ProjectId);

        await store.UpdateHostProjectPolicyAsync(
            "host-a",
            new UpdateHostProjectPolicyRequest(false, [], policy.Version),
            "operator",
            default);
        var blocked = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AcceptWorkPermitAsync(
                permit.PermitId,
                new WorkPermitAcceptRequest(
                    HostOrchestratorContract.Current,
                    "host-a",
                    "instance-a",
                    "runner-a",
                    report.AcceptedSequence,
                    report.PolicyVersion,
                    "policy-changed"),
                "runner-a",
                default));
        Assert.Equal("work-permit-project-not-allowed", blocked.Code);
    }

    private static TaskServerStore Store(string dataDirectory)
        => new(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = dataDirectory,
                MinimumLeaseSeconds = 30,
                MaximumLeaseSeconds = 600,
            }),
            TimeProvider.System);

    private static RegisterRunnerRequest Runner(string instanceId, string hostId = "host-a")
        => new(
            "runner",
            hostId,
            instanceId,
            "1.0.0",
            TaskServerProtocol.Current,
            [
                ReviewCapabilities.CodingExecutor,
                "permits",
                "local-queue",
                "host-post-processing",
            ],
            HostOrchestratorContract.MinimumSupported,
            HostOrchestratorContract.MaximumSupported);

    private static HostReportRequest Report(
        long sequence,
        HostCapacityDto capacity,
        IReadOnlyList<HostWorkStatusDto>? work = null,
        string instanceId = "instance-a")
        => new(
            HostOrchestratorContract.Current,
            "host-a",
            instanceId,
            sequence,
            DateTime.UtcNow,
            capacity,
            [new HostCapabilityDto("git-push", "ready", ObservedAt: DateTime.UtcNow)],
            work ?? [],
            [],
            []);

    private static HostWorkStatusDto Work(
        WorkPermitAcceptanceDto acceptance,
        string phase,
        int? queuePosition = null,
        int? processId = null)
        => new(
            acceptance.PermitId,
            acceptance.Task.TaskId,
            acceptance.Task.TaskKey,
            acceptance.Run.RunId,
            acceptance.Lease.LeaseId,
            acceptance.Lease.Fence,
            phase,
            queuePosition,
            processId,
            acceptance.Lease.AcquiredAt,
            DateTime.UtcNow);
}
