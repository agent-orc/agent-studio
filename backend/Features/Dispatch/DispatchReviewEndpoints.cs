using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Dispatch;

public sealed record DispatchWeightsRequest(IReadOnlyDictionary<string, int> Weights, double Gate = 0.8, string? Reason = null);
public sealed record DispatchExplanationRatingRequest(string EntryId, string Persona, double Score);
public sealed record DispatchLeaseControlRequest(string Action, long Fence, string? Persona = null, string? GuardId = null);

public static class DispatchReviewEndpoints
{
    public static void MapDispatchReviewEndpoints(this WebApplication app)
    {
        app.MapPut("/api/projects/{project}/dispatch/review/lease", (string project, DispatchLeaseControlRequest request,
            HttpContext context, TaskScannerService scanner, ProjectRegistry projects, DispatchLeaseScheduler scheduler) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal owner
                || owner.User.Role != StudioRoles.Owner
                || !ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectSettingsWrite, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.Action is not ("pin" or "stop" or "undo-guard"))
                return Results.BadRequest(new { error = "Use pin, stop or undo-guard." });
            try { return Results.Ok(scheduler.Refresh(entry.Name, action: request.Action, persona: request.Persona,
                expectedFence: request.Fence, expectedGuard: request.GuardId, actor: owner.User.Id)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.WorkspaceAdminister);

        app.MapGet("/api/projects/{project}/dispatch/review", (string project, string? window, string? weights,
            HttpContext context, TaskScannerService scanner, ProjectRegistry projects, DispatchReviewService review) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            var registered = entry is null ? null : projects.FindByIdOrDisplayName(entry.Name);
            if (entry is null || registered is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(review.Review(entry.Name, registered.WorkspaceId, entry.Path, window, weights)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequirePermission(IamPermissions.ProjectRead);

        app.MapPut("/api/workspaces/{id}/dispatch/review/weights", (string id, DispatchWeightsRequest request,
            HttpContext context, WorkspaceRegistry workspaces, WorkspaceSettingsService settings, TimeProvider time) =>
        {
            if (workspaces.Find(id) is null) return Results.NotFound();
            // Role metadata is enforced by middleware. A non-human credential never writes owner settings.
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal owner
                || owner.User.Role != StudioRoles.Owner) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(settings.ReviseDispatchReviewWeights(id, request.Weights, request.Gate,
                owner.User.Id, time.GetUtcNow(), request.Reason)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.WorkspaceAdminister);

        app.MapPut("/api/projects/{project}/dispatch/review/explanation-rating", (string project,
            DispatchExplanationRatingRequest request, HttpContext context, TaskScannerService scanner,
            ProjectRegistry projects, DispatchStore store, TimeProvider time) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => e.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal owner
                || owner.User.Role != StudioRoles.Owner
                || !ProjectAccessAuthorization.AllowsTasks(context, [entry.Name], IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var rating = new DispatchExplanationRating(request.EntryId, request.Persona, request.Score, owner.User.Id, time.GetUtcNow());
                store.RateExplanation(entry.Name, rating);
                return Results.Ok(rating);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.WorkspaceAdminister);
    }
}
