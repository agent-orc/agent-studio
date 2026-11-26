using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Dispatch;

public static class DispatchWaveEndpoints
{
    public static void MapDispatchWaveEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{project}/dispatch/waves/{wave}/plan", async (string project, string wave,
            HttpContext context, ProjectRegistry projects, DispatchWaveService service) =>
        {
            var registered = projects.FindByIdOrDisplayName(project);
            if (registered is null) return Results.NotFound();
            if (!ProjectAccessAuthorization.AllowsTasks(context, [registered.DisplayName], IamPermissions.ProjectCardsWrite, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var client = DispatchRecutEndpoints.Client(context);
            try { return Results.Ok(await service.PlanAsync(registered.Id, wave,
                new DispatchRecutTaskApi(client, registered.Id, DispatchRecutService.ReceiptId(registered.DisplayName, wave, true)), context.RequestAborted)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (HttpRequestException) { return Results.Conflict(new { error = "A wave task operation was refused; retry wave planning after checking the cards." }); }
        }).RequireStudioRole(StudioRoles.Owner).RequirePermission(IamPermissions.ProjectCardsWrite);
    }
}
