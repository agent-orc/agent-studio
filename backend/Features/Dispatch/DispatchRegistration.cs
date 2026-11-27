using AgentStudio.Projects;
using AgentStudio.Security;

namespace AgentStudio.Dispatch;

public static class DispatchRegistration
{
    public static IServiceCollection AddDispatch(this IServiceCollection services)
    {
        services.AddSingleton<DispatchStore>();
        services.AddSingleton<DispatchWaveService>();
        services.AddSingleton<IDispatchRecutDrafting, DispatchRecutDrafting>();
        services.AddSingleton<DispatchRecutService>();
        services.AddSingleton<DispatchReviewService>();
        services.AddSingleton<DispatchLeaseScheduler>();
        services.AddSingleton<AgentStudio.Runner.IAiOperatorEffect>(provider => provider.GetRequiredService<DispatchLeaseScheduler>());
        services.AddHostedService(provider => provider.GetRequiredService<DispatchLeaseScheduler>());
        services.AddSingleton<DispatcherShadowService>();
        services.AddSingleton<DispatchForecastService>();
        services.AddHostedService(provider => provider.GetRequiredService<DispatchForecastService>());
        return services;
    }

    public static void MapDispatchEndpoints(this WebApplication app)
    {
        app.MapDispatchRecutEndpoints();
        app.MapDispatchWaveEndpoints();
        app.MapDispatchReviewEndpoints();
        app.MapGet("/api/projects/{project}/dispatch/mode", (string project,
            TaskScannerService scanner, ProjectSettingsService settings, HttpContext context, ProjectRegistry projects) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(new { mode = settings.Get(entry.Name).DispatchMode });
        })
            .RequirePermission(IamPermissions.ProjectRead);
        app.MapPut("/api/projects/{project}/dispatch/mode", (string project, DispatchModeRequest request,
            TaskScannerService scanner, ProjectSettingsService settings) =>
        {
            if (!scanner.GetWatchPaths().Any(e => e.Name.Equals(project, StringComparison.OrdinalIgnoreCase))) return Results.NotFound();
            if (request.Mode is not ("off" or "shadow" or "live"))
                return Results.BadRequest(new { error = "Use off, shadow or live." });
            settings.SetDispatchMode(project, request.Mode);
            return Results.Ok(new { mode = settings.Get(project).DispatchMode });
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.WorkspaceAdminister);

        app.MapGet("/api/projects/{project}/dispatch/personas", (string project, HttpContext context,
            TaskScannerService scanner, ProjectRegistry projects, DispatchStore store) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(new { lease = store.ReadLease(entry.Name), shadow = store.ReadShadow(entry.Name).Where(r => r.Shadow),
                live = store.ReadLive(entry.Name), runDecisions = store.ReadRunDecisions(entry.Name),
                log = store.ReadDispatcherLog(entry.Name), chainIntact = store.VerifyDispatcherLog(entry.Name) });
        }).RequirePermission(IamPermissions.ProjectRead);

        app.MapGet("/api/projects/{project}/dispatch/conflicts", (string project, DateTimeOffset? from,
            DateTimeOffset? to, HttpContext context, TaskScannerService scanner, ProjectRegistry projects, DispatchStore store) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (from is not null && to is not null && from >= to)
                return Results.BadRequest(new { error = "Use a start time before the end time." });
            return Results.Ok(store.ReadConflicts(entry.Name, from, to));
        }).RequirePermission(IamPermissions.ProjectRead);

        app.MapPost("/api/projects/{project}/dispatch/conflicts/backfill", (string project,
            TaskScannerService scanner, AgentStudio.Git.GitService git) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            var repo = git.ResolveRepositoryRoot(entry);
            if (repo is null) return Results.Conflict(new { error = "The project repository is unavailable." });
            return Results.Ok(new { added = git.BackfillDispatchConflicts(repo, entry.Name, DateTimeOffset.UtcNow) });
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.WorkspaceAdminister);

        app.MapGet("/api/projects/{project}/dispatch/forecasts", async (string project, string? cards,
            HttpContext context, TaskScannerService scanner, TaskIndexCache tasks, ProjectRegistry projects, DispatchStore store) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var requested = cards?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, TouchForecastV1?>();
            if (requested is null)
            {
                foreach (var card in tasks.GetResidentTasks().Where(card => card.ProjectName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)))
                    result[card.TaskKey] = store.Read(entry.Name, card.TaskKey);
            }
            else
            {
                if (requested.Count > 200) return Results.BadRequest(new { error = "Request at most 200 cards." });
                foreach (var identity in requested)
                {
                    var facts = await tasks.GetDetailFactsAsync(identity, entry.Path, context.RequestAborted);
                    if (facts.Info is { } card && card.ProjectName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                        result[card.TaskKey] = store.Read(entry.Name, card.TaskKey);
                }
            }
            return Results.Ok(result);
        }).RequirePermission(IamPermissions.ProjectRead);

        app.MapGet("/api/projects/{project}/dispatch/hub-files", (string project, HttpContext context,
            TaskScannerService scanner, ProjectRegistry projects, ProjectSettingsService settings) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(settings.Get(entry.Name).DispatchHubFiles ?? TouchForecast.DefaultHubFiles);
        }).RequirePermission(IamPermissions.ProjectRead);

        app.MapPut("/api/projects/{project}/dispatch/hub-files", (string project, DispatchHubFilesRequest request,
            TaskScannerService scanner, ProjectSettingsService settings, DispatchForecastService forecasts) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(entry => entry.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (request.Files is { } files && (files.Count > 200 || files.Any(file => string.IsNullOrWhiteSpace(file)
                || file.Contains('\\') || file.StartsWith('/') || file.Contains(':') || file.Split('/').Any(part => part is ".." or "." or ""))))
                return Results.BadRequest(new { error = "Use at most 200 repository-relative file paths." });
            settings.SetDispatchHubFiles(entry.Name, request.Files);
            forecasts.Refresh();
            return Results.Ok(settings.Get(entry.Name).DispatchHubFiles ?? TouchForecast.DefaultHubFiles);
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.WorkspaceAdminister);
    }
}

public sealed record DispatchHubFilesRequest(IReadOnlyList<string>? Files);

public sealed record DispatchModeRequest(string Mode);
