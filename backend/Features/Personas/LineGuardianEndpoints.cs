using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Personas;

public sealed record GuardianModeRequest(bool Live);
public sealed record GuardianOperationRequest(string RunId, string Test, string Operation);
public static class LineGuardianEndpoints
{
    public static void MapLineGuardianEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{name}/line-guardian", (string name, ProjectRegistry projects, LineGuardianStore store) =>
        {
            var project = projects.FindByIdOrDisplayName(name) ?? projects.FindByShortCode(name);
            return project is null ? Results.NotFound() : Results.Ok(store.Read(project.Id));
        }).RequirePermission(IamPermissions.ProjectRead);
        app.MapGet("/api/projects/{name}/line-guardian/role", (string name, ProjectRegistry projects) =>
        {
            var project = projects.FindByIdOrDisplayName(name) ?? projects.FindByShortCode(name);
            return project is null ? Results.NotFound()
                : Results.Ok(new { Role = LineGuardianRole.Descriptor, Rights = LineGuardianRole.Rights });
        }).RequirePermission(IamPermissions.ProjectRead);
        app.MapPost("/api/projects/{name}/line-guardian/requests", (string name, GuardianOperationRequest request,
            ProjectRegistry projects, PostMergeIncidentService incidents) =>
        {
            var project = projects.FindByIdOrDisplayName(name) ?? projects.FindByShortCode(name);
            if (project is null) return Results.NotFound();
            try { return Results.Ok(incidents.RequestGuardianOperation(project, request.RunId, request.Test, request.Operation)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
        app.MapPut("/api/projects/{name}/line-guardian/mode", (string name, GuardianModeRequest request,
            HttpContext context, ProjectRegistry projects, LineGuardianStore store, TimeProvider clock) =>
        {
            if (!TestQuarantineEndpoints.Owner(context)) return Results.Forbid();
            var project = projects.FindByIdOrDisplayName(name) ?? projects.FindByShortCode(name);
            if (project is null) return Results.NotFound();
            store.SetLive(project.Id, request.Live, AccessSecurityMiddleware.ActorId(context, "owner"), true, clock.GetUtcNow());
            return Results.Ok(store.Read(project.Id));
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
    }
}
