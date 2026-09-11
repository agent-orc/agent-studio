namespace AgentStudio.Pipeline;

public static class FailureInterventionEndpoints
{
    public static void MapFailureInterventionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{project}/interventions", (
            string project,
            bool? openOnly,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            FailureInterventionService service) =>
        {
            var registered = projects.FindByIdOrDisplayName(project);
            var watch = scanner.GetWatchPaths().FirstOrDefault(item =>
                string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Path, project, StringComparison.OrdinalIgnoreCase)
                || (registered is not null
                    && string.Equals(item.Path, registered.StorageLocation, StringComparison.OrdinalIgnoreCase)));
            if (watch is null)
                return Results.NotFound(new { error = $"Unknown project '{project}'" });
            var items = service.List(watch.Path, openOnly == true);
            return Results.Ok(new
            {
                project = watch.Name,
                summary = FailureInterventionReporting.Summarize(items),
                items,
            });
        });
    }
}
