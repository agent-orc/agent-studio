using AgentStudio.Registry;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Projects;

/// <summary>
/// The repository visibility that review plans and claim checks use (AGT-3408):
/// the project setting combined with what the connected GitHub App reports, by
/// <see cref="ReviewRepositoryAccessPolicy.EffectiveVisibility"/>. Only cached
/// GitHub answers are read, so planning never waits for GitHub; the host
/// credential refresh keeps them current.
/// </summary>
public sealed class ProjectRepositoryVisibility(
    ProjectSettingsService settings,
    ProjectRegistry projects,
    GitHubAppBroker? github = null)
{
    public string Effective(ProjectRecord? project, string? projectSetting)
        => ReviewRepositoryAccessPolicy.EffectiveVisibility(projectSetting,
            github?.ReportedPrivate(RemoteProjectRepositoryResolver.ResolveRepositoryUrl(project)));

    /// <param name="projectSettings">The settings the caller already read; null reads them again.</param>
    public string Effective(TaskInfo task, ProjectSettings? projectSettings = null)
        => Effective(
            projects.FindByStorageLocation(task.WatchPath) ?? projects.FindByIdOrDisplayName(task.ProjectName),
            (projectSettings ?? settings.Get(task.ProjectName)).RepositoryVisibility);

    public bool IsPrivate(TaskInfo task, ProjectSettings? projectSettings = null)
        => ReviewRepositoryAccessPolicy.IsPrivate(Effective(task, projectSettings));
}
