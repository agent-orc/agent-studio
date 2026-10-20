using AgentStudio.Deploy;
using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StandardLifecyclePolicyTests
{
    private static readonly DeployEnvironment Staging = new("stage", "demo", "Stage", 0, EnvironmentKinds.Staging);
    private static readonly DeployEnvironment Production = new("production", "demo", "Production", 1, EnvironmentKinds.Production);
    private static readonly DeployActor Owner = new("owner", true);
    private static readonly DeployActor Operator = new("operator", false);

    [Fact]
    public void Staging_IsAutomatic_AndProductionRequiresApproval()
    {
        Assert.True(StandardLifecyclePolicy.IsAutomatic(Staging));
        Assert.False(StandardLifecyclePolicy.RequiresApproval(Staging));
        Assert.False(StandardLifecyclePolicy.IsAutomatic(Production));
        Assert.True(StandardLifecyclePolicy.RequiresApproval(Production));
    }

    [Theory]
    // environment, approveNow, approver, stagingSucceeded -> outcome, approvedBy
    [InlineData("stage", false, false, false, LifecycleOutcome.Queue, null)]
    [InlineData("stage", true, false, false, LifecycleOutcome.Queue, null)]
    [InlineData("production", false, false, true, LifecycleOutcome.AwaitApproval, null)]
    [InlineData("production", false, true, true, LifecycleOutcome.AwaitApproval, null)]
    [InlineData("production", true, true, true, LifecycleOutcome.Queue, "owner")]
    [InlineData("production", true, false, true, LifecycleOutcome.Reject, null)]
    [InlineData("production", false, true, false, LifecycleOutcome.Reject, null)]
    [InlineData("production", true, true, false, LifecycleOutcome.Reject, null)]
    public void Deploy_FollowsStandardLifecycle(string environmentId, bool approveNow, bool approver,
        bool stagingSucceeded, LifecycleOutcome expected, string? approvedBy)
    {
        var environment = environmentId == "stage" ? Staging : Production;
        var decision = StandardLifecyclePolicy.Decide(Request(DeployIntent.Deploy, environment,
            approver ? Owner : Operator, approveNow, previousSucceeded: stagingSucceeded));

        Assert.Equal(expected, decision.Outcome);
        Assert.Equal(approvedBy, decision.ApprovedBy);
        Assert.Equal(expected == LifecycleOutcome.Reject, decision.Reason is not null);
    }

    [Fact]
    public void Production_WithoutStagingSuccess_NamesTheRule()
    {
        var decision = StandardLifecyclePolicy.Decide(Request(DeployIntent.Deploy, Production, Owner, true,
            previousSucceeded: false));
        Assert.Contains("previous environment", decision.Reason);
    }

    [Fact]
    public void NonApprover_CannotApproveProductionInOneStep()
    {
        var decision = StandardLifecyclePolicy.Decide(Request(DeployIntent.Deploy, Production, Operator, true,
            previousSucceeded: true));
        Assert.Equal(LifecycleOutcome.Reject, decision.Outcome);
        Assert.Contains("Owner or Maintainer", decision.Reason);
        Assert.True(decision.Forbidden);
    }

    [Theory]
    [InlineData(DeployIntent.Deploy)]
    [InlineData(DeployIntent.Redeploy)]
    [InlineData(DeployIntent.Rollback)]
    public void ActiveRunOfSameRelease_IsRejected(DeployIntent intent)
    {
        var decision = StandardLifecyclePolicy.Decide(Request(intent, Staging, Owner, false,
            position: PositionFor(intent), rollbackTarget: true, activeForRelease: true, onHost: true));
        Assert.Equal(LifecycleOutcome.Reject, decision.Outcome);
        Assert.Contains("already", decision.Reason);
    }

    [Theory]
    [InlineData(DeployIntent.Deploy)]
    [InlineData(DeployIntent.Redeploy)]
    [InlineData(DeployIntent.Rollback)]
    public void EveryIntent_WaitsForTheRunOnTheHost(DeployIntent intent)
    {
        var decision = StandardLifecyclePolicy.Decide(Request(intent, Production, Owner, true,
            position: PositionFor(intent), rollbackTarget: true, onHost: true));
        Assert.Equal(LifecycleOutcome.Reject, decision.Outcome);
        Assert.Contains("Wait until", decision.Reason);
        Assert.False(decision.Forbidden);
    }

    [Theory]
    // intent, position, rollback target -> allowed, reason fragment
    [InlineData(DeployIntent.Deploy, ReleasePosition.NoCurrent, false, true, null)]
    [InlineData(DeployIntent.Deploy, ReleasePosition.Newer, false, true, null)]
    [InlineData(DeployIntent.Deploy, ReleasePosition.Current, false, false, "Use redeploy")]
    [InlineData(DeployIntent.Deploy, ReleasePosition.Older, true, false, "Use rollback")]
    [InlineData(DeployIntent.Redeploy, ReleasePosition.Current, false, true, null)]
    [InlineData(DeployIntent.Redeploy, ReleasePosition.Older, false, false, "current release")]
    [InlineData(DeployIntent.Redeploy, ReleasePosition.Newer, false, false, "current release")]
    [InlineData(DeployIntent.Rollback, ReleasePosition.Older, true, true, null)]
    [InlineData(DeployIntent.Rollback, ReleasePosition.Older, false, false, "before the current one")]
    [InlineData(DeployIntent.Rollback, ReleasePosition.Newer, false, false, "before the current one")]
    [InlineData(DeployIntent.Rollback, ReleasePosition.Current, false, false, "before the current one")]
    public void OnlyARollbackMovesBack(DeployIntent intent, ReleasePosition position, bool rollbackTarget,
        bool allowed, string? reason)
    {
        var decision = StandardLifecyclePolicy.Decide(Request(intent, Staging, Owner, false,
            position: position, rollbackTarget: rollbackTarget));
        Assert.Equal(allowed ? LifecycleOutcome.Queue : LifecycleOutcome.Reject, decision.Outcome);
        if (reason is not null) Assert.Contains(reason, decision.Reason);
    }

    [Theory]
    [InlineData(DeployIntent.Redeploy)]
    [InlineData(DeployIntent.Rollback)]
    public void RedeployAndRollback_InProduction_DoNotNeedStagingAgain_ButNeedApproval(DeployIntent intent)
    {
        var waiting = StandardLifecyclePolicy.Decide(Request(intent, Production, Operator, false,
            previousSucceeded: false, position: PositionFor(intent), rollbackTarget: true));
        Assert.Equal(LifecycleOutcome.AwaitApproval, waiting.Outcome);
        var approved = StandardLifecyclePolicy.Decide(Request(intent, Production, Owner, true,
            previousSucceeded: false, position: PositionFor(intent), rollbackTarget: true));
        Assert.Equal(LifecycleOutcome.Queue, approved.Outcome);
        Assert.Equal("owner", approved.ApprovedBy);
        var refused = StandardLifecyclePolicy.Decide(Request(intent, Production, Operator, true,
            previousSucceeded: false, position: PositionFor(intent), rollbackTarget: true));
        Assert.True(refused.Forbidden);
    }

    [Theory]
    [InlineData(DeploymentStates.AwaitingApproval, true, null)]
    [InlineData(DeploymentStates.AwaitingApproval, false, "Owner or Maintainer")]
    [InlineData(DeploymentStates.Queued, true, "not awaiting approval")]
    [InlineData(DeploymentStates.Succeeded, true, "not awaiting approval")]
    public void Approval_RequiresWaitingRunAndApprover(string state, bool approver, string? reason)
    {
        var rejection = StandardLifecyclePolicy.ApprovalRejection(state, approver ? Owner : Operator);
        if (reason is null) Assert.Null(rejection);
        else Assert.Contains(reason, rejection);
    }

    [Theory]
    // queued intent, waiting intent, waiting release newer -> superseded
    [InlineData(DeployIntent.Deploy, DeployIntent.Deploy, false, true)]
    [InlineData(DeployIntent.Deploy, DeployIntent.Deploy, true, false)]
    [InlineData(DeployIntent.Deploy, DeployIntent.Redeploy, false, true)]
    [InlineData(DeployIntent.Deploy, DeployIntent.Rollback, false, true)]
    [InlineData(DeployIntent.Rollback, DeployIntent.Deploy, true, false)]
    [InlineData(DeployIntent.Rollback, DeployIntent.Redeploy, true, true)]
    [InlineData(DeployIntent.Redeploy, DeployIntent.Deploy, true, false)]
    [InlineData(DeployIntent.Redeploy, DeployIntent.Rollback, false, false)]
    public void QueuedRun_SupersedesTheWaitingRunsItInvalidates(DeployIntent queued, DeployIntent waiting,
        bool waitingNewer, bool superseded)
        => Assert.Equal(superseded, StandardLifecyclePolicy.Supersedes(queued, waiting, waitingNewer));

    [Theory]
    // state, environment, approver, closes promotion -> rejection fragment
    [InlineData(DeploymentStates.Queued, "production", false, false, null)]
    [InlineData(DeploymentStates.AwaitingApproval, "production", false, false, null)]
    [InlineData(DeploymentStates.AwaitingApproval, "production", false, true, "Closing the approval")]
    [InlineData(DeploymentStates.AwaitingApproval, "production", true, true, null)]
    [InlineData(DeploymentStates.Running, "stage", false, false, null)]
    [InlineData(DeploymentStates.Running, "production", false, false, "Aborting a running")]
    [InlineData(DeploymentStates.Running, "production", true, false, null)]
    [InlineData(DeploymentStates.Succeeded, "stage", true, false, "already finished")]
    [InlineData(DeploymentStates.Failed, "stage", true, false, "already finished")]
    [InlineData(DeploymentStates.Cancelled, "stage", true, false, "already finished")]
    public void Cancel_BeforeTheHostClaims_AbortWhileItRuns(string state, string environmentId, bool approver,
        bool closesPromotion, string? reason)
    {
        var rejection = StandardLifecyclePolicy.CancelRejection(state, environmentId == "stage" ? Staging : Production,
            approver ? Owner : Operator, closesPromotion);
        if (reason is null) Assert.Null(rejection);
        else Assert.Contains(reason, rejection);
    }

    [Theory]
    [InlineData(StudioRoles.Owner, false, false, true)]
    [InlineData(StudioRoles.Operator, false, false, false)]
    [InlineData(StudioRoles.Operator, true, false, true)]
    [InlineData(StudioRoles.Contributor, true, false, true)]
    [InlineData(StudioRoles.Owner, false, true, false)]
    [InlineData(StudioRoles.Viewer, false, false, false)]
    [InlineData(StudioRoles.Owner, false, false, false, true)]
    [InlineData(StudioRoles.Operator, true, false, false, true)]
    public void ApprovalAuthority_IsOwnerOrProjectMaintainer(string role, bool grant, bool disabled, bool approver,
        bool mustChangePassword = false)
    {
        var user = new StudioUser
        {
            Id = "user-1", Username = "user", DisplayName = "User", Role = role, PasswordHash = "",
            ProductionApprovalProjects = grant ? ["Demo"] : ["other"], Disabled = disabled,
            MustChangePassword = mustChangePassword,
        };
        var actor = DeployApprovalAuthority.Resolve(user, "demo");
        Assert.Equal("user-1", actor.Id);
        Assert.Equal(approver, actor.IsApprover);
    }

    [Fact]
    public void ApprovalAuthority_WithoutSession_IsTheLocalOperatorAndNeverApproves()
    {
        // A coding agent on the local API has no Studio session.
        var actor = DeployApprovalAuthority.Resolve(null, "demo");
        Assert.Equal(DeployApprovalAuthority.LocalOperator, actor.Id);
        Assert.False(actor.IsApprover);
    }

    private static LifecycleRequest Request(DeployIntent intent, DeployEnvironment environment, DeployActor actor,
        bool approveNow, bool previousSucceeded = true, ReleasePosition position = ReleasePosition.Newer,
        bool rollbackTarget = false, bool activeForRelease = false, bool onHost = false)
        => new(intent, environment, actor, approveNow, previousSucceeded, position, rollbackTarget, activeForRelease,
            onHost);

    private static ReleasePosition PositionFor(DeployIntent intent) => intent switch
    {
        DeployIntent.Redeploy => ReleasePosition.Current,
        DeployIntent.Rollback => ReleasePosition.Older,
        _ => ReleasePosition.Newer,
    };
}
