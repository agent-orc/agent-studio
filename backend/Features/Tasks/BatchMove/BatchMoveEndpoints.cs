using AgentStudio.Security;

namespace AgentStudio.Tasks;

public static class BatchMoveEndpoints
{
    public static void MapBatchMoveEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/batch-move", (
            BatchMoveRequest req,
            HttpContext context,
            BatchMoveJobCoordinator jobs,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            if (req?.Items is null || req.Items.Count == 0)
                return Results.BadRequest(new { error = "items is required and must contain at least one entry" });
            if (req.Items.Count > 500)
                return Results.BadRequest(new { error = "items must contain no more than 500 entries" });

            var projectNames = req.Items
                .Select(item => scanner.FindJob(
                    item.JobId,
                    string.IsNullOrWhiteSpace(item.WatchPath) ? null : item.WatchPath)?.ProjectName)
                .ToArray();
            // Permission-aware per task: the Product role never batch-moves,
            // also not where the caller holds a stronger role elsewhere.
            if (!ProjectAccessAuthorization.AllowsTasks(context, projectNames, IamPermissions.ProjectCardsWrite, projects)
                || projectNames.Any(project => ProjectAccessAuthorization.IsProductRoleIn(context, project)))
            {
                return Results.Json(
                    new { error = "project-scope-denied", message = "This account is not a member of every task in the batch." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var clientId = context.Items["ClientId"] as string
                ?? context.Request.Headers["X-Client-Id"].FirstOrDefault();
            var job = jobs.Enqueue(
                req.Items,
                projectNames,
                TimelineActors.Human(clientId ?? string.Empty));
            return Results.Accepted($"/api/tasks/batch-move/{job.Id}", job);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        group.MapGet("/batch-move/{batchId}", (
            string batchId,
            HttpContext context,
            BatchMoveJobCoordinator jobs,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            if (!jobs.TryGetProjectNames(batchId, out var projectNames)
                || !jobs.TryGet(batchId, out var job))
            {
                return Results.NotFound(new { error = $"Unknown batch move job '{batchId}'" });
            }

            if (!ProjectAccessAuthorization.AllowsTasks(context, projectNames, projects))
            {
                return Results.Json(
                    new { error = "project-scope-denied", message = "This account is not a member of every task in the batch." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(job);
        });
    }
}
