using AgentStudio.Registry;
using AgentStudio.Security;
using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>One card of an acceptance-readiness request.</summary>
public sealed record AcceptanceReadinessRequestItem(string JobId, string? Project = null, string? WatchPath = null);

/// <summary>Either the named cards or every Human Review card of <see cref="Project"/>.</summary>
public sealed record AcceptanceReadinessRequest(
    string? Project = null,
    IReadOnlyList<AcceptanceReadinessRequestItem>? Items = null);

/// <summary>
/// AGT-3564 - <c>POST /api/tasks/acceptance-readiness</c> answers, for each
/// Human Review card, what its acceptance would answer now: ready, or which
/// check refuses it and why. The route is a dry run of the completion and
/// changes nothing; it is a POST because the completion checks read Git, and
/// read requests serve published facts only (AGT-3321). Accepting the ready
/// cards goes through the normal batch move, a round through the normal
/// continue route.
/// </summary>
public static class AcceptanceReadinessEndpoints
{
    public const int MaxItems = 500;

    public static void MapAcceptanceReadinessEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/acceptance-readiness", (
            AcceptanceReadinessRequest? req,
            HttpContext context,
            TaskScannerService scanner,
            ProjectRegistry projects,
            AcceptanceReadinessService readiness,
            CancellationToken ct) =>
        {
            if (req is null || (req.Items is not { Count: > 0 } && string.IsNullOrWhiteSpace(req.Project)))
                return Results.BadRequest(new { error = "items or project is required" });
            if (req.Items is { Count: > MaxItems })
                return Results.BadRequest(new { error = $"items must contain no more than {MaxItems} entries" });

            List<TaskInfo> cards;
            if (req.Items is { Count: > 0 } items)
            {
                cards = items
                    .Select(item => string.IsNullOrWhiteSpace(item.JobId) ? null
                        : scanner.FindJob(item.JobId, ResolveWatchPath(projects, item.Project, item.WatchPath)))
                    .OfType<TaskInfo>()
                    .ToList();
            }
            else
            {
                var storage = ResolveWatchPath(projects, req.Project, null);
                if (storage is null)
                    return Results.NotFound(new { error = $"Project '{req.Project}' was not found." });
                cards = scanner.ScanAllJobs()
                    .Where(card => card.State == TaskStates.HumanReview
                        && string.Equals(card.WatchPath, storage, StringComparison.OrdinalIgnoreCase))
                    .Select(card => scanner.FindJob(card.Id, card.WatchPath))
                    .OfType<TaskInfo>()
                    .ToList();
            }

            if (!ProjectAccessAuthorization.AllowsTasks(context, cards.Select(card => card.ProjectName),
                    IamPermissions.ProjectRead, projects))
                return Results.Json(
                    new { error = "project-scope-denied", message = "This account cannot read every task in the request." },
                    statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(readiness.Evaluate(cards, ct));
        });
    }
}
