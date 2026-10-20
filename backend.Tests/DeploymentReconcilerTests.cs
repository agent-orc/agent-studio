using AgentStudio.Deploy;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure reconciler inputs; the code under test never reads a clock.
public sealed class DeploymentReconcilerTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Artifact = "registry.example/app@sha256:" + new string('a', 64);
    private static readonly DeployEnvironment Staging = new("stage", "demo", "Stage", 0, EnvironmentKinds.Staging);
    private static readonly DeployEnvironment Production = new("production", "demo", "Production", 1, EnvironmentKinds.Production);
    private static readonly Release R1 = new("r1", "demo", "2026.10.16.1", new string('1', 40), [Artifact],
        "process", "variables", ["AGT-1"], T0, ReleaseSnapshot.DefaultChannel);

    [Fact]
    public void AdoptsQueueJob_ThenFollowsClaimAndReport_WithStepsLogAndAudit()
    {
        var queued = Job("job-1", "stage", "queued", T0);
        var first = Reconcile([], [queued]);
        var run = Assert.Single(first.Deployments);
        Assert.Equal(("job-1", "r1", DeploymentStates.Queued, DeploymentTriggers.Deploy),
            (run.JobId, run.ReleaseId, run.State, run.Trigger));
        Assert.False(run.NeedsApproval);
        Assert.Equal([DeployStepNames.StartContainer, DeployStepNames.Health], run.Steps.Select(step => step.Name));
        Assert.All(run.Steps, step => Assert.Equal(DeploymentStepStates.Pending, step.State));
        Assert.Equal("deployment-queued", Assert.Single(first.Audit).Action);

        var claimed = queued with { Status = "claimed", ClaimedAt = T0.AddMinutes(1) };
        var second = Reconcile(first.Deployments, [claimed]);
        run = Assert.Single(second.Deployments);
        Assert.Equal(DeploymentStates.Running, run.State);
        Assert.Equal(T0.AddMinutes(1), run.StartedAtUtc);
        Assert.Equal("stage-host", run.HostId);
        Assert.Equal(DeploymentStates.Running, run.Steps[0].State);
        Assert.Contains(second.Log, line => line.Message.Contains("stage-host claimed"));

        var healthy = claimed with { Status = "healthy", FinishedAt = T0.AddMinutes(3), Evidence = DeployEvidence.HealthOk };
        var third = Reconcile(second.Deployments, [healthy]);
        run = Assert.Single(third.Deployments);
        Assert.Equal(DeploymentStates.Succeeded, run.State);
        Assert.Equal(T0.AddMinutes(3), run.FinishedAtUtc);
        Assert.Equal(
            [
                new DeploymentStepRecord(DeployStepNames.StartContainer, DeploymentStates.Succeeded, T0.AddMinutes(1), T0.AddMinutes(3)),
                new DeploymentStepRecord(DeployStepNames.Health, DeploymentStates.Succeeded, T0.AddMinutes(3), T0.AddMinutes(3)),
            ],
            run.Steps);
        Assert.Equal("deployment-succeeded", Assert.Single(third.Audit).Action);
        Assert.Equal(2, third.Log.Count);
    }

    [Fact]
    public void ClaimAndReportBetweenPasses_RecordBothTransitions()
    {
        var job = Job("job-1", "stage", "failed", T0) with
        { ClaimedAt = T0.AddMinutes(1), FinishedAt = T0.AddMinutes(2), Evidence = DeployEvidence.DeployFailed };
        var run = Assert.Single(Reconcile([], [job]).Deployments);
        Assert.Equal(DeploymentStates.Failed, run.State);
        Assert.Equal(T0.AddMinutes(1), run.StartedAtUtc);
        Assert.Equal((DeploymentStates.Failed, DeploymentStepStates.Skipped), (run.Steps[0].State, run.Steps[1].State));
    }

    [Fact]
    public void HealthFailure_FailsOnlyTheHealthStep()
    {
        var job = Job("job-1", "stage", "failed", T0) with
        { ClaimedAt = T0.AddMinutes(1), FinishedAt = T0.AddMinutes(2), Evidence = DeployEvidence.HealthFailed };
        var result = Reconcile([], [job]);
        var run = Assert.Single(result.Deployments);
        Assert.Equal((DeploymentStates.Succeeded, DeploymentStates.Failed), (run.Steps[0].State, run.Steps[1].State));
        Assert.Contains(result.Log, line => line.Step == DeployStepNames.Health && line.Message.Contains("failed"));
    }

    [Fact]
    public void SecondPassOverItsOwnOutput_ChangesNothing()
    {
        var job = Job("job-1", "stage", "healthy", T0) with
        { ClaimedAt = T0.AddMinutes(1), FinishedAt = T0.AddMinutes(2), Evidence = DeployEvidence.HealthOk };
        var promotion = new EnvironmentPromotion(Artifact, "stage", "production", T0.AddMinutes(2), "pending",
            ExpiresAt: T0.AddHours(24));
        var first = Reconcile([], [job], [promotion]);
        Assert.True(first.Changed);
        var second = Reconcile(first.Deployments, [job], [promotion]);
        Assert.False(second.Changed);
        Assert.Empty(second.Log);
        Assert.Empty(second.Audit);
        Assert.Equal(first.Deployments, second.Deployments);
    }

    [Fact]
    public void JobsFromBeforeTheRelease_StayUnattributed()
    {
        var result = Reconcile([], [Job("legacy", "stage", "healthy", T0.AddMinutes(-5))]);
        Assert.Empty(result.Deployments);
        Assert.False(result.Changed);
    }

    [Fact]
    public void PendingPromotion_WaitsForApproval_ThenLinksTheApprovedJob()
    {
        var stageHealthy = T0.AddMinutes(2);
        var promotion = new EnvironmentPromotion(Artifact, "stage", "production", stageHealthy, "pending",
            ExpiresAt: stageHealthy.AddHours(24));
        var waiting = Reconcile([], [], [promotion]);
        var run = Assert.Single(waiting.Deployments);
        Assert.Equal((DeploymentStates.AwaitingApproval, "production", DeploymentReconciler.LifecycleActor),
            (run.State, run.EnvironmentId, run.RequestedBy));
        Assert.Equal(DeploymentStates.AwaitingApproval, run.Steps[0].State);
        Assert.Equal("approval-requested", Assert.Single(waiting.Audit).Action);

        // The AGT-3176 approve route queued production directly.
        var approvedJob = Job("job-2", "production", "queued", T0.AddMinutes(5)) with { RequestedBy = "maintainer" };
        var linked = Reconcile(waiting.Deployments, [approvedJob],
            [promotion with { Status = "production-queued", Actor = "maintainer" }]);
        run = Assert.Single(linked.Deployments);
        Assert.Equal(("job-2", DeploymentStates.Queued, "maintainer"), (run.JobId, run.State, run.ApprovedBy));
        Assert.Equal(new DeploymentStepRecord(DeployStepNames.Approval, DeploymentStates.Succeeded, stageHealthy,
            T0.AddMinutes(5)), run.Steps[0]);
        Assert.Equal("deployment-approved", Assert.Single(linked.Audit).Action);
    }

    [Theory]
    [InlineData("expired", "approval-expired", "Approval window expired.")]
    [InlineData("rejected", "deployment-rejected", "Rejected by maintainer.")]
    public void ClosedPromotion_CancelsTheWaitingRun(string status, string action, string message)
    {
        var stageHealthy = T0.AddMinutes(2);
        var promotion = new EnvironmentPromotion(Artifact, "stage", "production", stageHealthy, "pending",
            ExpiresAt: stageHealthy.AddHours(24));
        var waiting = Reconcile([], [], [promotion]);
        var closed = Reconcile(waiting.Deployments, [], [promotion with { Status = status, Actor = "maintainer" }]);
        var run = Assert.Single(closed.Deployments);
        Assert.Equal(DeploymentStates.Cancelled, run.State);
        Assert.Equal(DeploymentStates.Cancelled, run.Steps[0].State);
        Assert.Equal(action, Assert.Single(closed.Audit).Action);
        Assert.Equal(message, Assert.Single(closed.Log).Message);
        // A closed promotion never asks again.
        Assert.False(Reconcile(closed.Deployments, [], [promotion with { Status = status }]).Changed);
    }

    [Fact]
    public void PromotionJob_NeverAnswersAWaitingRedeploy()
    {
        var redeploy = new DeploymentRecord
        {
            Id = "waiting-redeploy", ReleaseId = "r1", EnvironmentId = "production",
            State = DeploymentStates.AwaitingApproval, Trigger = DeploymentTriggers.Redeploy,
            DeployStep = DeployStepNames.StartContainer, NeedsApproval = true, RequestedBy = "operator",
            CreatedAtUtc = T0.AddMinutes(1),
        };
        var job = Job("job-5", "production", "queued", T0.AddMinutes(2)) with { RequestedBy = "stage-health-rule" };
        var result = Reconcile([redeploy], [job]);
        Assert.Equal(2, result.Deployments.Count);
        Assert.Equal(DeploymentStates.AwaitingApproval, result.Deployments.Single(item => item.Id == "waiting-redeploy").State);
        Assert.Equal("stage-health-rule", result.Deployments.Single(item => item.JobId == "job-5").ApprovedBy);
    }

    [Fact]
    public void AutomaticRollback_IsAdoptedWithoutApprovalStep()
    {
        var rollback = Job("job-3", "production", "queued", T0.AddMinutes(9)) with
        { IsRollback = true, RequestedBy = "automatic-rollback" };
        var run = Assert.Single(Reconcile([], [rollback]).Deployments);
        Assert.Equal(DeploymentTriggers.AutomaticRollback, run.Trigger);
        Assert.False(run.NeedsApproval);
        Assert.DoesNotContain(run.Steps, step => step.Name == DeployStepNames.Approval);
    }

    [Fact]
    public void OperatorRollbackInProduction_RecordsTheApprover()
    {
        var rollback = Job("job-4", "production", "queued", T0.AddMinutes(9)) with
        { IsRollback = true, RequestedBy = "owner" };
        var run = Assert.Single(Reconcile([], [rollback]).Deployments);
        Assert.Equal((DeploymentTriggers.Rollback, "owner"), (run.Trigger, run.ApprovedBy));
        Assert.Equal(DeploymentStates.Succeeded, run.Steps[0].State);
    }

    [Fact]
    public void PromotionRun_TakesItsDeployStepFromTheReleaseSnapshot()
    {
        var promotion = new EnvironmentPromotion(Artifact, "stage", "production", T0.AddMinutes(2), "pending",
            ExpiresAt: T0.AddHours(24));
        var staticProduction = new ReleaseRecord(R1, [Declaration("stage", "container"), Declaration("production", "static")]);
        var run = Assert.Single(DeploymentReconciler.Reconcile(new([Staging, Production], [staticProduction], [], [],
            [promotion])).Deployments);
        Assert.Equal(DeployStepNames.PublishStatic, run.DeployStep);
    }

    [Fact]
    public void Promotion_without_a_phase_in_the_release_channel_is_not_adopted()
    {
        var promotion = new EnvironmentPromotion(Artifact, "stage", "production", T0.AddMinutes(2), "pending");
        var record = new ReleaseRecord(R1 with { Channel = "staging-only" }, [Declaration("stage", "container")])
        {
            Lifecycle = [new ProjectLifecyclePhase("staging", true, [], null)],
        };
        var result = DeploymentReconciler.Reconcile(new([Staging, Production], [record], [], [], [promotion]));
        Assert.Empty(result.Deployments);
    }

    [Fact]
    public void EachReclaimByARestartedHost_IsLogged()
    {
        var claimed = Job("job-1", "stage", "claimed", T0) with { ClaimedAt = T0.AddMinutes(1), Claims = 1 };
        var first = Reconcile([], [claimed]);
        Assert.Equal(1, Assert.Single(first.Deployments).HostClaims);

        var reclaimed = claimed with { ClaimedAt = T0.AddMinutes(7), Claims = 2 };
        var second = Reconcile(first.Deployments, [reclaimed]);
        var run = Assert.Single(second.Deployments);
        Assert.Equal((DeploymentStates.Running, T0.AddMinutes(1), 2), (run.State, run.StartedAtUtc, run.HostClaims));
        var line = Assert.Single(second.Log);
        Assert.Equal((T0.AddMinutes(7), DeployStepNames.StartContainer), (line.AtUtc, line.Step));
        Assert.Contains("claimed the job again", line.Message);
        Assert.Equal(("deployment-reclaimed", "stage-host"), (Assert.Single(second.Audit).Action, second.Audit[0].Actor));
        Assert.False(Reconcile(second.Deployments, [reclaimed]).Changed);
    }

    [Fact]
    public void LostHost_FailsTheRunWithTheReason()
    {
        var claimed = Job("job-1", "stage", "claimed", T0) with { ClaimedAt = T0.AddMinutes(1), Claims = 1 };
        var running = Reconcile([], [claimed]).Deployments;
        var lost = claimed with { Status = "failed", FinishedAt = T0.AddMinutes(16), Evidence = DeployEvidence.HostLost };
        var result = Reconcile(running, [lost]);
        var run = Assert.Single(result.Deployments);
        Assert.Equal((DeploymentStates.Failed, T0.AddMinutes(16)), (run.State, run.FinishedAtUtc));
        Assert.Equal((DeploymentStates.Failed, DeploymentStepStates.Skipped), (run.Steps[0].State, run.Steps[1].State));
        Assert.Contains(result.Log, item => item.Message.Contains("stopped reporting") && item.Message.Contains("host-lost"));
        Assert.Contains("host-lost", Assert.Single(result.Audit).Detail);
    }

    private static ReconcileResult Reconcile(IReadOnlyList<DeploymentRecord> runs, IReadOnlyList<DeployJob> jobs,
        IReadOnlyList<EnvironmentPromotion>? promotions = null)
        => DeploymentReconciler.Reconcile(new([Staging, Production],
            [new ReleaseRecord(R1, [Declaration("stage", "container"), Declaration("production", "container")])],
            runs, jobs, promotions ?? []));

    private static EnvironmentDeclaration Declaration(string id, string template)
        => new(id, id, id == "stage" ? "stage" : "production", id + "-host", "https://example", template,
            template == "static" ? "/srv/site" : "8081", "https://example/health", "person", [], 8080);

    private static DeployJob Job(string id, string environmentId, string status, DateTime createdAt)
        => new(id, "demo", environmentId, environmentId == "stage" ? "stage-host" : "prod-host", Artifact,
            "container", "8081", "https://stage.example/health", [], 8080, null, status, createdAt,
            RequestedBy: "release-gate");
}
