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
    }

    [Theory]
    [InlineData(DeployIntent.Deploy)]
    [InlineData(DeployIntent.Redeploy)]
    [InlineData(DeployIntent.Rollback)]
    public void ActiveRunOfSameRelease_IsRejected(DeployIntent intent)
    {
        var decision = StandardLifecyclePolicy.Decide(Request(intent, Staging, Owner, false,
            previousSucceeded: true, succeededHere: true, activeForRelease: true, activeInEnvironment: true));
        Assert.Equal(LifecycleOutcome.Reject, decision.Outcome);
        Assert.Contains("already", decision.Reason);
    }

    [Theory]
    [InlineData(DeployIntent.Redeploy)]
    [InlineData(DeployIntent.Rollback)]
    public void RedeployAndRollback_WaitForActiveRunInEnvironment(DeployIntent intent)
    {
        var decision = StandardLifecyclePolicy.Decide(Request(intent, Production, Owner, true,
            succeededHere: true, activeInEnvironment: true));
        Assert.Equal(LifecycleOutcome.Reject, decision.Outcome);
        Assert.Contains("Wait until", decision.Reason);
    }

    [Theory]
    [InlineData(DeployIntent.Redeploy)]
    [InlineData(DeployIntent.Rollback)]
    public void RedeployAndRollback_RequireAnEarlierSuccessInTheEnvironment(DeployIntent intent)
    {
        var refused = StandardLifecyclePolicy.Decide(Request(intent, Staging, Owner, false, succeededHere: false));
        Assert.Equal(LifecycleOutcome.Reject, refused.Outcome);

        // A production rollback does not need the release in staging again,
        // but it still needs approval.
        var waiting = StandardLifecyclePolicy.Decide(Request(intent, Production, Operator, false,
            previousSucceeded: false, succeededHere: true));
        Assert.Equal(LifecycleOutcome.AwaitApproval, waiting.Outcome);
        var approved = StandardLifecyclePolicy.Decide(Request(intent, Production, Owner, true,
            previousSucceeded: false, succeededHere: true));
        Assert.Equal(LifecycleOutcome.Queue, approved.Outcome);
        Assert.Equal("owner", approved.ApprovedBy);
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
    [InlineData(DeploymentStates.Queued, true)]
    [InlineData(DeploymentStates.AwaitingApproval, true)]
    [InlineData(DeploymentStates.Running, false)]
    [InlineData(DeploymentStates.Succeeded, false)]
    [InlineData(DeploymentStates.Failed, false)]
    [InlineData(DeploymentStates.Cancelled, false)]
    public void Cancel_OnlyBeforeAHostClaims(string state, bool allowed)
        => Assert.Equal(allowed, StandardLifecyclePolicy.CancelRejection(state) is null);

    [Theory]
    [InlineData(StudioRoles.Owner, false, false, true)]
    [InlineData(StudioRoles.Operator, false, false, false)]
    [InlineData(StudioRoles.Operator, true, false, true)]
    [InlineData(StudioRoles.Contributor, true, false, true)]
    [InlineData(StudioRoles.Owner, false, true, false)]
    [InlineData(StudioRoles.Viewer, false, false, false)]
    public void ApprovalAuthority_IsOwnerOrProjectMaintainer(string role, bool grant, bool disabled, bool approver)
    {
        var user = new StudioUser
        {
            Id = "user-1", Username = "user", DisplayName = "User", Role = role, PasswordHash = "",
            ProductionApprovalProjects = grant ? ["Demo"] : ["other"], Disabled = disabled,
        };
        var actor = DeployApprovalAuthority.Resolve(user, "demo", localProfile: true);
        Assert.Equal("user-1", actor.Id);
        Assert.Equal(approver, actor.IsApprover);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ApprovalAuthority_WithoutSession_IsTheLocalOperatorOnlyInLocalProfile(bool local, bool approver)
    {
        var actor = DeployApprovalAuthority.Resolve(null, "demo", local);
        Assert.Equal(DeployApprovalAuthority.LocalOperator, actor.Id);
        Assert.Equal(approver, actor.IsApprover);
    }

    private static LifecycleRequest Request(DeployIntent intent, DeployEnvironment environment, DeployActor actor,
        bool approveNow, bool previousSucceeded = true, bool succeededHere = false, bool activeForRelease = false,
        bool activeInEnvironment = false)
        => new(intent, environment, actor, approveNow, previousSucceeded, succeededHere, activeForRelease,
            activeInEnvironment);
}
