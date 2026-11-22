namespace AgentStudio.Publishing;

/// <summary>
/// PUB-1 - read-only publish-status route. <c>GET
/// /api/projects/{project}/publish-status</c> returns the project's derived
/// publish targets (package + website) and their pending deltas, straight from
/// <see cref="PublishTargetService"/>. Additive to the project snapshot (which
/// folds the same status in under <c>publishTargets</c> for the Hub overview
/// poll); the standalone route lets the Git View and tests read it directly.
/// </summary>
public static class PublishEndpoints
{
    public static void MapPublishEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{project}/publish-status", (string project, PublishTargetService publish) =>
            Results.Ok(publish.GetProjectPublishStatus(project)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        app.MapGet("/api/projects/{project}/publish/{targetId}/panel", (
            string project, string targetId, PublishActionService actions) =>
            ActionResult(() => Results.Ok(actions.GetPanel(project, targetId))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        app.MapPut("/api/projects/{project}/publish/automation", (
            string project,
            SetPublishAutomationRequest request,
            ProjectSettingsService settings,
            PublishTargetService targets) =>
        {
            var target = targets.GetProjectPublishStatus(project).Targets
                .FirstOrDefault(x => string.Equals(x.Id, request.TargetId, StringComparison.OrdinalIgnoreCase));
            if (target is null) return Results.NotFound(new { error = $"Unknown publish target '{request.TargetId}'." });
            if (!PublishAutomationModes.All.Contains(request.Mode, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unsupported automation mode '{request.Mode}'." });
            settings.SetPublishAutomation(project, target.Id, request.Mode);
            var resolved = PublishAutomationModes.Normalize(target.Id, request.Mode);
            return Results.Ok(new { targetId = target.Id, mode = resolved });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        app.MapPost("/api/projects/{project}/publish/package", (
            string project,
            PublishPackageRequest request,
            PublishActionService actions,
            CancellationToken cancellationToken) =>
            ActionResult(() => Results.Ok(actions.PublishPackage(
                project,
                request.TargetId,
                request.Version,
                cancellationToken,
                request.DryRun))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        app.MapPost("/api/projects/{project}/publish/website", (
            string project, DeployWebsiteRequest request, PublishActionService actions) =>
            ActionResult(() => Results.Ok(actions.DeployWebsite(project))))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        app.MapGet("/api/projects/{project}/publish/{targetId}/run", (
            string project, string targetId, PublishActionService actions) =>
            ActionResult(() => actions.RefreshRun(project, targetId) is { } run
                ? Results.Ok(run)
                : Results.NotFound(new { error = "No publish workflow has been triggered for this target." })))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }

    private static IResult ActionResult(Func<IResult> action)
    {
        try { return action(); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
}
