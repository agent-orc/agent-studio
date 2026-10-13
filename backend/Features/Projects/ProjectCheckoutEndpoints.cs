using AgentStudio.Git;
using AgentStudio.Registry;

namespace AgentStudio.Projects;

/// <summary>Interactive synchronization of the registered developer checkout.</summary>
public static class ProjectCheckoutEndpoints
{
    public static void MapProjectCheckoutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/checkout/status", (
            string projectName, TaskScannerService scanner, ProjectRegistry registry,
            ProjectSettingsService settings, GitService git, CancellationToken ct) =>
            Resolve(projectName, scanner, registry, settings, git) is { } target
                ? Results.Ok(git.ObserveDeveloperCheckoutStatus(target.Path, target.Branch, ct))
                : Results.NotFound(new { error = $"Unknown project '{projectName}' or repository unavailable." }));

        app.MapPost("/api/projects/{projectName}/checkout/sync", (
            string projectName, TaskScannerService scanner, ProjectRegistry registry,
            ProjectSettingsService settings, GitService git, CancellationToken ct) =>
            Resolve(projectName, scanner, registry, settings, git) is { } target
                ? Results.Ok(git.SyncDeveloperCheckout(target.Path, target.Branch, ct))
                : Results.NotFound(new { error = $"Unknown project '{projectName}' or repository unavailable." }));
    }

    internal static (string Path, string Branch)? Resolve(
        string projectName, TaskScannerService scanner, ProjectRegistry registry,
        ProjectSettingsService settings, GitService git)
    {
        var entry = scanner.GetWatchPaths().FirstOrDefault(value =>
            string.Equals(value.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        var registered = registry.FindByStorageLocation(entry.Path);
        var path = !string.IsNullOrWhiteSpace(registered?.RepositoryPath) ? registered.RepositoryPath
            : !string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RepositoryPath
            : !string.IsNullOrWhiteSpace(registered?.RootPath) ? registered.RootPath : entry.RootPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        return (path, git.ResolveIntegrationBranch(path, settings.Get(projectName).IntegrationBranch));
    }
}
