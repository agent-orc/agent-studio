using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Deploy;

/// <summary>
/// Persisted deploy run. The AGT-3176 deploy job (<see cref="JobId"/>) stays
/// the unit the deploy host executes; this record adds release identity,
/// approval, steps and the log.
/// </summary>
public sealed record DeploymentRecord
{
    public required string Id { get; init; }
    public required string ReleaseId { get; init; }
    public required string EnvironmentId { get; init; }
    public required string State { get; init; }
    public required string Trigger { get; init; }
    /// <summary><c>publish-static</c> or <c>start-container</c>, from the environment template.</summary>
    public required string DeployStep { get; init; }
    public bool NeedsApproval { get; init; }
    public string? RequestedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
    public string? CancelledBy { get; init; }
    public string? JobId { get; init; }
    public List<string> PriorJobIds { get; init; } = [];
    public string? HostId { get; init; }
    /// <summary>Claims of the job seen so far; a higher count on the job is a reclaim.</summary>
    public int HostClaims { get; init; }
    /// <summary>Stage-healthy time of the AGT-3176 promotion that asked for this approval.</summary>
    public DateTime? PromotionStageHealthyAtUtc { get; init; }
    public string? Evidence { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public bool GuidedFailure { get; init; }
    public int InterventionTimeoutMinutes { get; init; } = 60;
    public DateTime? InterventionExpiresAtUtc { get; init; }
    public string? FailedStep { get; init; }
    /// <summary>The step executed by the current retry job, if this run is retrying.</summary>
    public string? RetryStep { get; init; }
    public string? InterventionDecidedBy { get; init; }
    public string? InterventionDecision { get; init; }
    public List<DeploymentStepRecord> Steps { get; init; } = [];
}

public sealed record DeploymentStepRecord(string Name, string State, DateTime? StartedAtUtc, DateTime? FinishedAtUtc);

public sealed record DeploymentLogEntry(string DeploymentId, string Step, DateTime AtUtc, string Message);

public static class DeployStepNames
{
    public const string Approval = "approval";
    public const string PublishStatic = "publish-static";
    public const string StartContainer = "start-container";
    public const string Health = "health";

    public static string ForTemplate(string template) => template == "static" ? PublishStatic : StartContainer;
}

/// <summary>Host result codes accepted by the AGT-3176 report route.</summary>
public static class DeployEvidence
{
    public const string HealthOk = "health-ok";
    public const string HealthFailed = "health-http-failed";
    public const string DeployFailed = "deploy-step-failed";
    /// <summary>The claim expired without report or reclaim.</summary>
    public const string HostLost = "host-lost";
    /// <summary>A person aborted the run while the host ran it.</summary>
    public const string Aborted = "deploy-aborted";
}

public static class DeployIds
{
    public static string For(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
}

/// <summary>Derives the P1 step list from the run's facts. Pure.</summary>
public static class DeploymentStepPlan
{
    public static List<DeploymentStepRecord> Build(DeploymentRecord run)
    {
        var steps = new List<DeploymentStepRecord>();
        var cancelled = run.State == DeploymentStates.Cancelled;
        if (run.NeedsApproval)
        {
            var state = run.ApprovedBy is not null ? DeploymentStates.Succeeded
                : cancelled ? DeploymentStates.Cancelled : DeploymentStates.AwaitingApproval;
            steps.Add(new(DeployStepNames.Approval, state, run.CreatedAtUtc,
                run.ApprovedAtUtc ?? (cancelled ? run.FinishedAtUtc : null)));
        }

        var healthFailed = run.Evidence == DeployEvidence.HealthFailed;
        var healthRetry = run.RetryStep == DeployStepNames.Health;
        var completedDeploy = healthRetry
            ? run.Steps.FirstOrDefault(step => step.Name == run.DeployStep && step.State == DeploymentStates.Succeeded)
            : null;
        var deployState = run.State switch
        {
            _ when healthRetry => DeploymentStates.Succeeded,
            DeploymentStates.Running => DeploymentStates.Running,
            DeploymentStates.Succeeded when run.InterventionDecision == "skip" && !healthFailed => DeploymentStepStates.Skipped,
            DeploymentStates.Succeeded => DeploymentStates.Succeeded,
            DeploymentStates.Failed => healthFailed ? DeploymentStates.Succeeded : DeploymentStates.Failed,
            DeploymentStates.WaitingForIntervention => healthFailed ? DeploymentStates.Succeeded : DeploymentStates.Failed,
            DeploymentStates.Cancelled => DeploymentStates.Cancelled,
            _ => DeploymentStepStates.Pending,
        };
        var finished = DeploymentStates.IsFinished(run.State) && run.StartedAtUtc is not null;
        steps.Add(new(run.DeployStep, deployState,
            completedDeploy is not null ? completedDeploy.StartedAtUtc : run.StartedAtUtc,
            completedDeploy is not null ? completedDeploy.FinishedAtUtc : finished ? run.FinishedAtUtc : null));

        var healthState = run.State switch
        {
            DeploymentStates.Succeeded when run.InterventionDecision == "skip" => DeploymentStepStates.Skipped,
            DeploymentStates.Succeeded => DeploymentStates.Succeeded,
            DeploymentStates.Failed => healthFailed ? DeploymentStates.Failed : DeploymentStepStates.Skipped,
            DeploymentStates.WaitingForIntervention => healthFailed ? DeploymentStates.Failed : DeploymentStepStates.Skipped,
            DeploymentStates.Cancelled => DeploymentStates.Cancelled,
            _ => DeploymentStepStates.Pending,
        };
        // The host reports deploy and health together, so the health step has
        // one observed instant: the report.
        var healthAt = healthState is DeploymentStates.Succeeded or DeploymentStates.Failed ? run.FinishedAtUtc : null;
        steps.Add(new(DeployStepNames.Health, healthState, healthAt, healthAt));
        return steps;
    }
}
