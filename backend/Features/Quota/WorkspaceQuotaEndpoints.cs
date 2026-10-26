using AgentStudio.Projects;
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
            Results.Ok(quotas.Admit(project)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Claim);
        app.MapGet("/api/workspaces/{workspaceId}/quota/admission", (string workspaceId,
            WorkspaceQuotaService quotas) =>
        {
            try { return Results.Ok(quotas.BoardAdmission(workspaceId)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
        app.MapGet("/api/workspaces/{workspaceId}/quota", (string workspaceId,
            WorkspaceQuotaService quotas) => Read(() => quotas.Snapshot(workspaceId)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
        app.MapGet("/api/workspaces/{workspaceId}/projects/{projectId}/quota", (string workspaceId,
            string projectId, WorkspaceQuotaService quotas) => Read(() => quotas.Snapshot(workspaceId, projectId)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
        app.MapGet("/api/workspaces/{workspaceId}/quota/audit", (HttpContext context, string workspaceId,
            WorkspaceQuotaService quotas) => Owner(context) ? Results.Ok(quotas.Audit(workspaceId, null)) : Results.Forbid())
            .RequirePermission(IamPermissions.WorkspaceAdminister)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
        // Running environments get their slots before a new limit can admit work.
        app.MapPut("/api/workspaces/{workspaceId}/quota", (HttpContext context, string workspaceId,
            QuotaLimits limits, WorkspaceQuotaService quotas, ProjectEnvironmentService environments) =>
            Set(context, () =>
            {
                environments.ReconcileHostingSlots();
                quotas.SetLimits(workspaceId, null, limits);
            }))
            .RequirePermission(IamPermissions.WorkspaceAdminister)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
        app.MapPut("/api/workspaces/{workspaceId}/projects/{projectId}/quota", (HttpContext context,
            string workspaceId, string projectId, QuotaLimits limits, WorkspaceQuotaService quotas,
            ProjectEnvironmentService environments) =>
            Set(context, () =>
            {
                environments.ReconcileHostingSlots();
                quotas.SetLimits(workspaceId, projectId, limits);
            }))
            .RequirePermission(IamPermissions.WorkspaceAdminister)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
        app.MapPost("/api/workspaces/{workspaceId}/quota/override", (HttpContext context,
            string workspaceId, QuotaOverrideRequest request, WorkspaceQuotaService quotas) =>
            Set(context, () => quotas.Override(workspaceId, null, Actor(context), request.Reason)))
            .RequirePermission(IamPermissions.WorkspaceAdminister)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
        app.MapPost("/api/workspaces/{workspaceId}/projects/{projectId}/quota/override", (HttpContext context,
            string workspaceId, string projectId, QuotaOverrideRequest request, WorkspaceQuotaService quotas) =>
            Set(context, () => quotas.Override(workspaceId, projectId, Actor(context), request.Reason)))
            .RequirePermission(IamPermissions.WorkspaceAdminister)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);
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
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    /// <summary>Attribute non-human calls to the service account that owns the token.</summary>
    internal static string Actor(HttpContext context)
        => context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is ServiceAccountPrincipal account
            ? account.ActorId
            : (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Id
              ?? "local-owner";

    internal static bool Owner(HttpContext context)
        => context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is null
           && (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal principal
               || principal.User.Role == StudioRoles.Owner);
}
