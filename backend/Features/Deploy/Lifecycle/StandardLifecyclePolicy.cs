namespace AgentStudio.Deploy;

public enum DeployIntent { Deploy, Redeploy, Rollback }

public enum LifecycleOutcome { Queue, AwaitApproval, Reject }

/// <summary>Plain facts the application service collects before asking the lifecycle.</summary>
public sealed record LifecycleRequest(
    DeployIntent Intent,
    DeployEnvironment Environment,
    DeployActor Actor,
    bool ApproveNow,
    bool ReleaseSucceededInPreviousEnvironment,
    bool ReleaseSucceededInEnvironment,
    bool ActiveDeploymentForRelease,
    bool ActiveDeploymentInEnvironment);

public sealed record LifecycleDecision(LifecycleOutcome Outcome, string? ApprovedBy, string? Reason)
{
    public static LifecycleDecision Reject(string reason) => new(LifecycleOutcome.Reject, null, reason);
}

/// <summary>
/// The P1 standard lifecycle (Dossier "Bereitstellung", section 2): Staging
/// deploys automatically after integration; Production needs a successful
/// Staging deploy of the same release and approval by an Owner or Maintainer.
/// Pure: no clock, storage or HTTP.
/// </summary>
public static class StandardLifecyclePolicy
{
    public const string ApprovalRoles = "Owner or Maintainer";

    public static bool IsAutomatic(DeployEnvironment environment)
        => environment.Kind == EnvironmentKinds.Staging;

    public static bool RequiresApproval(DeployEnvironment environment)
        => environment.Kind == EnvironmentKinds.Production;

    public static LifecycleDecision Decide(LifecycleRequest request)
    {
        var environment = request.Environment;
        if (request.ActiveDeploymentForRelease)
            return LifecycleDecision.Reject(
                $"This release is already queued, running or awaiting approval in {environment.Name}.");
        if (request.Intent is DeployIntent.Redeploy or DeployIntent.Rollback)
        {
            if (request.ActiveDeploymentInEnvironment)
                return LifecycleDecision.Reject(
                    $"Wait until the active deployment in {environment.Name} has finished.");
            if (!request.ReleaseSucceededInEnvironment)
                return LifecycleDecision.Reject(
                    $"The release has not been deployed successfully to {environment.Name} before.");
        }
        else if (environment.Order > 0 && !request.ReleaseSucceededInPreviousEnvironment)
        {
            return LifecycleDecision.Reject(
                $"{environment.Name} requires a successful deployment of this release to the previous environment.");
        }

        if (!RequiresApproval(environment))
            return new(LifecycleOutcome.Queue, null, null);
        if (!request.ApproveNow)
            return new(LifecycleOutcome.AwaitApproval, null, null);
        return request.Actor.IsApprover
            ? new(LifecycleOutcome.Queue, request.Actor.Id, null)
            : LifecycleDecision.Reject($"{environment.Name} approval requires the {ApprovalRoles} role.");
    }

    /// <summary>Approve or reject a deployment that waits for approval.</summary>
    public static string? ApprovalRejection(string deploymentState, DeployActor actor)
    {
        if (deploymentState != DeploymentStates.AwaitingApproval)
            return "The deployment is not awaiting approval.";
        return actor.IsApprover ? null : $"Approval requires the {ApprovalRoles} role.";
    }

    /// <summary>Only work that has not reached a deploy host can be cancelled.</summary>
    public static string? CancelRejection(string deploymentState)
        => deploymentState is DeploymentStates.Queued or DeploymentStates.AwaitingApproval
            ? null : "Only queued deployments or deployments awaiting approval can be cancelled.";
}
