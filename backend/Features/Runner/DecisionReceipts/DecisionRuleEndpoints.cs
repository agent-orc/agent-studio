using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Runner;

/// <summary>Human Operator API for drafts, counterfactual replay, activation and rollback.</summary>
public static class DecisionRuleEndpoints
{
    /// <param name="proxyProfile">
    /// True when a standalone Task Server owns workspaces; every rule route
    /// then answers the typed 409 instead of reading Studio's local registry.
    /// </param>
    public static void MapDecisionRuleEndpoints(this WebApplication app, bool proxyProfile = false)
    {
        var api = app.MapGroup("/api/workspaces/{workspaceId}/decision-rules");
        if (proxyProfile)
            api.AddEndpointFilter((_, _) => ValueTask.FromResult<object?>(
                OrchestratorDecisionEndpoints.ProxyProfileConflict()));
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
            // Same access predicate as the receipt list. Receipts name a card's
            // project by its registry name; overlays use the project id.
            var replayed = receipts.RecentReceipts(workspaceId, receipt =>
                OrchestratorDecisionEndpoints.AllowsProject(context, receipt.Scope.ProjectId, projects)
                && (projectId is null || ProjectIdOf(receipt.Scope.ProjectId, projects) == projectId));
            try { return Results.Ok(store.DryRun(workspaceId, projectId, versionId, replayed)); }
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

    internal static string? ProjectIdOf(string? receiptProject, ProjectRegistry projects)
        => string.IsNullOrWhiteSpace(receiptProject) ? null
            : projects.FindByIdOrDisplayName(receiptProject)?.Id ?? receiptProject;

    private static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            ? human.User.Id : "local-operator";
}
