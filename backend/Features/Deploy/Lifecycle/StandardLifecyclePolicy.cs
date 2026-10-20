namespace AgentStudio.Deploy;

public enum DeployIntent { Deploy, Redeploy, Rollback }

public enum LifecycleOutcome { Queue, AwaitApproval, Reject }

/// <summary>Where a release stands against the environment's current release.</summary>
public enum ReleasePosition { NoCurrent, Newer, Current, Older }

/// <summary>
/// Plain facts the application service collects before asking the lifecycle.
/// "Other" runs exclude the run being approved.
/// </summary>
public sealed record LifecycleRequest(
    DeployIntent Intent,
    DeployEnvironment Environment,
    DeployActor Actor,
    bool ApproveNow,
    bool ReleaseSucceededInPreviousEnvironment,
    ReleasePosition Position,
    bool IsRollbackTarget,
    bool OtherActiveRunForRelease,
    bool OtherRunOnHost)
{
    public string Channel { get; init; } = ReleaseSnapshot.DefaultChannel;
    public IReadOnlyList<AgentStudio.TaskServer.Contracts.ProjectLifecyclePhase> Phases { get; init; } = [];
}

/// <param name="Forbidden">The rejection is about the actor's approval role, not the environment's state.</param>
public sealed record LifecycleDecision(LifecycleOutcome Outcome, string? ApprovedBy, string? Reason, bool Forbidden = false)
{
    public static LifecycleDecision Reject(string reason) => new(LifecycleOutcome.Reject, null, reason);
}

/// <summary>
/// The P1 standard lifecycle (Dossier "Bereitstellung", section 2): Staging
/// deploys automatically after integration; Production needs a successful
/// Staging deploy of the same release and approval by an Owner or Maintainer.
/// One run at a time reaches the deploy host of an environment, a deploy only
/// moves forward, and only a rollback moves back. Pure: no clock, storage or
/// HTTP.
/// </summary>
public static class StandardLifecyclePolicy
{
    public const string ApprovalRoles = "Owner or Maintainer";

    public static bool IsAutomatic(DeployEnvironment environment)
        => environment.Kind == EnvironmentKinds.Staging;

    public static bool RequiresApproval(DeployEnvironment environment)
        => environment.Kind == EnvironmentKinds.Production;

    public static DeployIntent IntentFor(string trigger) => trigger switch
    {
        DeploymentTriggers.Redeploy => DeployIntent.Redeploy,
        DeploymentTriggers.Rollback or DeploymentTriggers.AutomaticRollback => DeployIntent.Rollback,
        _ => DeployIntent.Deploy,
    };

    /// <summary>
    /// Decides a new request, and decides again when a waiting run is
    /// approved, because the environment may have moved on meanwhile.
    /// </summary>
    public static LifecycleDecision Decide(LifecycleRequest request)
    {
        var environment = request.Environment;
        var phase = request.Phases.Count == 0 ? null : request.Phases.FirstOrDefault(item => item.Phase == environment.Kind);
        if (request.Phases.Count > 0 && phase is null)
            return LifecycleDecision.Reject($"Channel '{request.Channel}' lifecycle has no phase for {environment.Name}.");
        if (request.OtherActiveRunForRelease)
            return LifecycleDecision.Reject(
                $"This release is already queued, running or awaiting approval in {environment.Name}.");
        if (request.OtherRunOnHost)
            return LifecycleDecision.Reject(
                $"Wait until the active deployment in {environment.Name} has finished.");
        var rejection = request.Intent switch
        {
            DeployIntent.Deploy => request.Position switch
            {
                ReleasePosition.Current => $"The release is already current in {environment.Name}. Use redeploy.",
                ReleasePosition.Older => $"The release is older than the current release in {environment.Name}. Use rollback.",
                _ => (phase?.Requires is not null || phase is null && environment.Order > 0)
                    && !request.ReleaseSucceededInPreviousEnvironment
                    ? $"Channel '{request.Channel}' lifecycle rule '{phase?.Requires ?? "previous environment"}' requires a successful deployment of this release before {environment.Name}."
                    : null,
            },
            DeployIntent.Redeploy => request.Position == ReleasePosition.Current
                ? null : $"Redeploy runs only the current release of {environment.Name}.",
            _ => request.IsRollbackTarget
                ? null : $"Rollback runs only the release before the current one in {environment.Name}.",
        };
        if (rejection is not null) return LifecycleDecision.Reject(rejection);

        if (phase is not null ? phase.Approval.Count == 0 : !RequiresApproval(environment))
            return new(LifecycleOutcome.Queue, null, null);
        if (!request.ApproveNow)
            return new(LifecycleOutcome.AwaitApproval, null, null);
        return request.Actor.IsApprover
            ? new(LifecycleOutcome.Queue, request.Actor.Id, null)
            : new(LifecycleOutcome.Reject, null, $"{environment.Name} approval requires the {ApprovalRoles} role.", true);
    }

    /// <summary>Approve or reject a deployment that waits for approval.</summary>
    public static string? ApprovalRejection(string deploymentState, DeployActor actor)
    {
        if (deploymentState != DeploymentStates.AwaitingApproval)
            return "The deployment is not awaiting approval.";
        return actor.IsApprover ? null : $"Approval requires the {ApprovalRoles} role.";
    }

    /// <summary>
    /// When a run is queued, which other run waiting for approval in the same
    /// environment can no longer be approved validly. A forward deploy of a
    /// newer release stays valid; so does a rollback when the current release
    /// is only redeployed. Every other waiting run is superseded.
    /// </summary>
    public static bool Supersedes(DeployIntent queued, DeployIntent waiting, bool waitingReleaseIsNewer)
        => !(waiting == DeployIntent.Deploy && waitingReleaseIsNewer)
           && !(queued == DeployIntent.Redeploy && waiting == DeployIntent.Rollback);

    /// <summary>
    /// Cancel before a host claims, abort while the host runs. Closing a
    /// pending production promotion is an approval decision, and aborting a
    /// running production deploy interrupts production, so both need the
    /// approver.
    /// </summary>
    public static string? CancelRejection(string deploymentState, DeployEnvironment environment, DeployActor actor,
        bool closesPromotion)
    {
        if (DeploymentStates.IsFinished(deploymentState))
            return "The deployment has already finished.";
        var needsApprover = deploymentState switch
        {
            DeploymentStates.AwaitingApproval => closesPromotion,
            DeploymentStates.Running => RequiresApproval(environment),
            _ => false,
        };
        return needsApprover && !actor.IsApprover
            ? $"{(deploymentState == DeploymentStates.Running ? "Aborting a running" : "Closing the approval of a")} "
              + $"{environment.Name} deployment requires the {ApprovalRoles} role."
            : null;
    }
}
