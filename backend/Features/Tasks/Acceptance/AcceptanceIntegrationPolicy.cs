using AgentStudio.Pipeline;

namespace AgentStudio.Tasks;

/// <summary>
/// Pure acceptance policy. Coding deliveries require a successful integration
/// outcome before they may remain in Completed. Code-free deliveries can be
/// declared by their delivery contract.
/// </summary>
public static class AcceptanceIntegrationPolicy
{
    public static bool IsIntegrationRequiredAtCreation(CreateTaskRequest request)
    {
        if (request.RequiresIntegration.HasValue) return request.RequiresIntegration.Value;
        return !request.NoBranchExpected
            && !TaskModes.IsReadOnly(request.Mode)
            && !TaskKinds.IsEpic(request.Kind)
            && !IsNoBranchTaskType(request.TaskType);
    }

    public static bool IsIntegrationRequired(TaskInfo task)
    {
        // Delivery evidence outranks the configurable class. A concept or a
        // no-branch card that actually changed the repository still integrates.
        var changedCommit = (task.Commits.Count > 0 ? task.Commits : task.Commit is null ? [] : [task.Commit])
            .Any(TaskCommitSupersession.IsEffectiveDelivery);
        var subject = AgentStudio.Pipeline.ReviewSubjectStore.Read(task.FolderPath);
        var changedResult = subject is not null
            && !string.IsNullOrWhiteSpace(subject.BaseSha)
            && !string.Equals(subject.ResultSha, subject.BaseSha, StringComparison.OrdinalIgnoreCase);
        if (changedCommit || changedResult)
            return true;
        if (task.RequiresIntegration.HasValue) return task.RequiresIntegration.Value;
        if (task.NoBranchExpected
            || TaskModes.IsReadOnly(task.Mode)
            || TaskKinds.IsEpic(task.Kind))
        {
            return false;
        }

        return !IsNoBranchTaskType(task.TaskType);
    }

    public static bool IsNoBranchTaskType(string? taskType) =>
        string.Equals(taskType, "concept", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskType, "decision", StringComparison.OrdinalIgnoreCase);

    public static AcceptedIntegrationLaneDecision Decide(
        MergeIntoIntegrationOutcome outcome,
        bool operatorOverride = false,
        bool integrationRequired = true)
    {
        if (!integrationRequired)
            return AcceptedIntegrationLaneDecision.Complete;

        return outcome.IsSuccessfulIntegration()
            ? AcceptedIntegrationLaneDecision.Complete
            : AcceptedIntegrationLaneDecision.ReturnToHumanReview;
    }
}

public enum AcceptedIntegrationLaneDecision
{
    Complete,
    ReturnToHumanReview,
}
