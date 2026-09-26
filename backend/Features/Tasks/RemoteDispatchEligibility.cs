namespace AgentStudio.Tasks;

/// <summary>
/// Canonical non-repository eligibility rules shared by Remote claim
/// selection and the queue-depth watcher. Keeping the rules here prevents the
/// watcher from counting cards that the next claim poll must refuse.
/// Repository registration and cached host preflight remain claim-time checks.
/// </summary>
public static class RemoteDispatchEligibility
{
    public static bool IsAssignedAndRunnable(
        TaskInfo task,
        ProjectSettings project,
        string runnerId,
        string? runnerName,
        TaskReferenceIndex references)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(references);

        return IsAssignedAndRoutable(task, project, runnerId, runnerName, references)
               && BuildProfileGate.AllowsAutoPickup(project.BuildProfile);
    }

    /// <summary>
    /// Evaluates every non-repository claim rule except the build-profile gate.
    /// Claim and starvation scans use this form so gate-blocked Ready cards stay
    /// visible and can receive a durable rejection reason.
    /// </summary>
    public static bool IsAssignedAndRoutable(
        TaskInfo task,
        ProjectSettings project,
        string runnerId,
        string? runnerName,
        TaskReferenceIndex references)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(references);

        return ProjectExecutionPolicy.AllowsAutomaticPickup(project)
               && !TaskKinds.IsDecision(task.Kind)
               && ProjectExecutionPolicy.IsAssignedRemote(project, runnerId, runnerName)
               && AgentTypes.IsAutoPickupEligible(task.Agent)
               && !TaskSlugs.IsHumanDecisionNeeded(task.Id)
               && (!project.IntakeEnabled.GetValueOrDefault()
                   || task.Phase == LifecyclePhases.IntakePassed)
               && !references.EvaluateWaitsOn(task).Blocked;
    }

    public static bool IsReadOnlyRefused(TaskInfo task, bool runnerReadOnly) =>
        runnerReadOnly && !TaskKinds.IsEpic(task.Kind);

    public static bool IsClaimableReady(
        TaskInfo task,
        ProjectSettings project,
        string runnerId,
        string? runnerName,
        bool runnerReadOnly,
        TaskReferenceIndex references) =>
        task.State == TaskStates.Ready
        && IsAssignedAndRunnable(task, project, runnerId, runnerName, references)
        && !IsReadOnlyRefused(task, runnerReadOnly);
}
