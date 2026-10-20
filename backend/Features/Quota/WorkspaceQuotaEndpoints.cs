using AgentStudio.Security;

namespace AgentStudio.Quota;

public sealed record QuotaOverrideRequest(string Reason);

public static class WorkspaceQuotaEndpoints
{
    public static void MapWorkspaceQuotaEndpoints(this WebApplication app)
    {
        // An unregistered project is admitted with MeterUnavailable so a name
        // mismatch surfaces as a meter event instead of stopping its claims.
        app.MapGet("/api/quota/admission/{project}", (string project, WorkspaceQuotaService quotas) =>
            Results.Ok(quotas.Admit(project)));
        app.MapGet("/api/workspaces/{workspaceId}/quota/admission", (string workspaceId,
            WorkspaceQuotaService quotas) =>
        {
            try { return Results.Ok(quotas.BoardAdmission(workspaceId)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
        app.MapGet("/api/workspaces/{workspaceId}/quota", (string workspaceId,
            WorkspaceQuotaService quotas) => Read(() => quotas.Snapshot(workspaceId)));
        app.MapGet("/api/workspaces/{workspaceId}/projects/{projectId}/quota", (string workspaceId,
            string projectId, WorkspaceQuotaService quotas) => Read(() => quotas.Snapshot(workspaceId, projectId)));
        app.MapGet("/api/workspaces/{workspaceId}/quota/audit", (HttpContext context, string workspaceId,
            WorkspaceQuotaService quotas) => Owner(context) ? Results.Ok(quotas.Audit(workspaceId, null)) : Results.Forbid());
        app.MapPut("/api/workspaces/{workspaceId}/quota", (HttpContext context, string workspaceId,
            QuotaLimits limits, WorkspaceQuotaService quotas) => Set(context, () => quotas.SetLimits(workspaceId, null, limits)));
        app.MapPut("/api/workspaces/{workspaceId}/projects/{projectId}/quota", (HttpContext context,
            string workspaceId, string projectId, QuotaLimits limits, WorkspaceQuotaService quotas) =>
            Set(context, () => quotas.SetLimits(workspaceId, projectId, limits)));
        app.MapPost("/api/workspaces/{workspaceId}/quota/override", (HttpContext context,
            string workspaceId, QuotaOverrideRequest request, WorkspaceQuotaService quotas) =>
            Set(context, () => quotas.Override(workspaceId, null, Actor(context), request.Reason)));
        app.MapPost("/api/workspaces/{workspaceId}/projects/{projectId}/quota/override", (HttpContext context,
            string workspaceId, string projectId, QuotaOverrideRequest request, WorkspaceQuotaService quotas) =>
            Set(context, () => quotas.Override(workspaceId, projectId, Actor(context), request.Reason)));
    }

    private static IResult Read(Func<QuotaSnapshot> read)
    {
        try { return Results.Ok(read()); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    }

    private static IResult Set(HttpContext context, Action write)
    {
        if (!Owner(context))
            return Results.Forbid();
        try { write(); return Results.NoContent(); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    /// <summary>An API token acts as its owner but is audited under its own token id.</summary>
    internal static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.ApiTokenPrincipalItem] is ApiTokenPrincipal token
            ? "api:" + token.Token.Id
            : (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Id
              ?? "local-owner";

    private static bool Owner(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal principal
           || principal.User.Role == StudioRoles.Owner;
}
