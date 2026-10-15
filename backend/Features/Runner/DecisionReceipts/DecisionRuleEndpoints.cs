using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Runner;

/// <summary>Human Operator API for drafts, counterfactual replay, activation and rollback.</summary>
public static class DecisionRuleEndpoints
{
    public static void MapDecisionRuleEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/workspaces/{workspaceId}/decision-rules");
        api.MapGet("", (HttpContext context, string workspaceId, string? projectId,
            WorkspaceRegistry workspaces, ProjectRegistry projects, DecisionRuleVersionStore store) =>
            Scope(context, workspaceId, projectId, workspaces, projects, false) is { } error ? error
                : Results.Ok(store.View(workspaceId, projectId)));
        api.MapPost("/versions", (HttpContext context, string workspaceId, string? projectId,
            DecisionRuleDraftRequest request, WorkspaceRegistry workspaces, ProjectRegistry projects,
            DecisionRuleVersionStore store) =>
        {
            if (Scope(context, workspaceId, projectId, workspaces, projects, true) is { } error) return error;
            try { return Results.Ok(store.Draft(workspaceId, projectId, request with { Actor = Actor(context) })); }
            catch (ArgumentException e) { return Results.BadRequest(new { reasons = new[] { e.Message } }); }
            catch (InvalidOperationException e) { return Results.Conflict(new { reason = e.Message }); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        api.MapPost("/versions/{versionId}/activate", (HttpContext context, string workspaceId,
            string? projectId, string versionId, DecisionRuleActivateRequest request,
            WorkspaceRegistry workspaces, ProjectRegistry projects, DecisionRuleVersionStore store) =>
        {
            if (Scope(context, workspaceId, projectId, workspaces, projects, true) is { } error) return error;
            try { return Results.Ok(store.Activate(workspaceId, projectId, versionId,
                request with { Actor = Actor(context) })); }
            catch (ArgumentException e) { return Results.BadRequest(new { reasons = new[] { e.Message } }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException e) { return Results.Conflict(new { reason = e.Message }); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        api.MapPost("/versions/{versionId}/rollback", (HttpContext context, string workspaceId,
            string? projectId, string versionId, DecisionRuleActivateRequest request,
            WorkspaceRegistry workspaces, ProjectRegistry projects, DecisionRuleVersionStore store) =>
        {
            if (Scope(context, workspaceId, projectId, workspaces, projects, true) is { } error) return error;
            try { return Results.Ok(store.Rollback(workspaceId, projectId, versionId,
                request with { Actor = Actor(context) })); }
            catch (ArgumentException e) { return Results.BadRequest(new { reasons = new[] { e.Message } }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException e) { return Results.Conflict(new { reason = e.Message }); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
        api.MapGet("/versions/{versionId}/dry-run", (HttpContext context, string workspaceId,
            string? projectId, string versionId, WorkspaceRegistry workspaces, ProjectRegistry projects,
            DecisionRuleVersionStore store, OrchestratorDecisionStore receipts) =>
        {
            if (Scope(context, workspaceId, projectId, workspaces, projects, false) is { } error) return error;
            try { return Results.Ok(store.DryRun(workspaceId, projectId, versionId,
                receipts.RecentReceipts(workspaceId, projectId))); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
    }

    private static IResult? Scope(HttpContext context, string workspaceId, string? projectId,
        WorkspaceRegistry workspaces, ProjectRegistry projects, bool mutation)
    {
        if (workspaces.Find(workspaceId) is null) return Results.NotFound();
        if (projectId is not null && projects.FindById(projectId)?.WorkspaceId != workspaceId)
            return Results.NotFound();
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
        {
            if (mutation && human.User.Role != StudioRoles.Owner) return Results.Forbid();
            if (projectId is not null && !ProjectAccessAuthorization.Allows(human.User, projectId, projects))
                return Results.Forbid();
        }
        return null;
    }

    private static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            ? human.User.Id : "local-operator";
}
