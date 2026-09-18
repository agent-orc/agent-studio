namespace AgentStudio.Tasks;

/// <summary>
/// Persists the normal repository-aware projection before the periodic rail
/// acts. No separate checkout resolver or integration policy exists here.
/// </summary>
public sealed class IntegrationGenerationReconcileSweep(TaskMutationService mutations)
{
    public bool Reconcile(TaskInfo task, TaskIntegrationStatus status)
        => mutations.RecordCommitIntegrationOnFolder(task.FolderPath,
            status.Repositories.SelectMany(repository => repository.Commits).ToList(), task.EnteredLaneAt);
}
