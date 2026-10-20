using System.Net.Http.Json;
using AgentStudio.Host;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Projects;

/// <summary>Copies the existing project visibility setting to registered Task Server repositories.</summary>
public sealed class ProjectRepositoryVisibilitySync(
    IConfiguration configuration,
    IHttpClientFactory clients,
    ProjectRegistry projects,
    ProjectSettingsService settings,
    ILogger<ProjectRepositoryVisibilitySync> logger) : BackgroundService
{
    public async Task SyncAsync(string projectName, string? visibility, CancellationToken ct)
    {
        if (!TaskServerPlaneProxy.IsConfigured(configuration)) return;
        var project = projects.FindByIdOrDisplayName(projectName);
        var repositoryUrl = RemoteProjectRepositoryResolver.ResolveRepositoryUrl(project);
        if (repositoryUrl is null) return;
        using var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName).PutAsJsonAsync(
            "/api/v1/projects/repository-visibility",
            new SetProjectRepositoryVisibilityRequest(repositoryUrl, visibility), ct);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TaskServerPlaneProxy.IsConfigured(configuration)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            foreach (var project in projects.List())
            {
                try
                {
                    await SyncAsync(project.Id, settings.Get(project.DisplayName).RepositoryVisibility,
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "project-repository-visibility-sync-failed project={ProjectId}", project.Id);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
