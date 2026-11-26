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
        app.MapGet("/api/projects/{name}/line-guardian/review", (string name, DateTimeOffset? week,
            ProjectRegistry projects, LineGuardianStore store, AgentStudio.Pipeline.PostMergeRunStore runs,
            AgentStudio.Pipeline.TestQuarantineStore quarantines, TimeProvider clock,
            AgentStudio.Tasks.TaskScannerService scanner, AgentStudio.Pipeline.PipelineExecutionLog executions, AgentStudio.Tasks.TimelineLog timeline) =>
        {
            var project = projects.FindByIdOrDisplayName(name) ?? projects.FindByShortCode(name);
            if (project is null) return Results.NotFound();
            try { return Results.Ok(new GuardianReviewService(store, runs, quarantines, clock, scanner, executions, timeline).Review(project.Id, week, project.DisplayName)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequirePermission(IamPermissions.ProjectRead);
        app.MapPut("/api/projects/{name}/line-guardian/review/weights", (string name,
            Dictionary<string, double> weights, HttpContext context, ProjectRegistry projects, LineGuardianStore store) =>
        {
            if (!TestQuarantineEndpoints.Owner(context)) return Results.Forbid();
            var project = projects.FindByIdOrDisplayName(name) ?? projects.FindByShortCode(name);
            if (project is null) return Results.NotFound();
            try { store.SetReviewWeights(project.Id, weights); return Results.Ok(weights); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
        app.MapPost("/api/projects/{name}/line-guardian/review/evidence", (string name,
            GuardianReviewEvidence evidence, HttpContext context, ProjectRegistry projects, LineGuardianStore store) =>
        {
            if (!TestQuarantineEndpoints.Owner(context)) return Results.Forbid();
            var project = projects.FindByIdOrDisplayName(name) ?? projects.FindByShortCode(name);
            if (project is null) return Results.NotFound();
            try { store.RecordReviewEvidence(project.Id, evidence); return Results.Ok(evidence); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
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
            try
            {
                store.SetLive(project.Id, request.Live, AccessSecurityMiddleware.ActorId(context, "owner"), true, clock.GetUtcNow());
                return Results.Ok(store.Read(project.Id));
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequirePermission(IamPermissions.WorkspaceAdminister);
    }
}
