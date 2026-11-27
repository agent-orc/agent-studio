using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Operator;

public static class CodingCapacityEndpoints
{
    public static void MapCodingCapacityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/operator/coding-capacity", (HttpContext context, CodingCapacityService capacity,
            ProjectRegistry registry, TimeProvider clock) =>
        {
            var scoped = context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access
                && !access.Allows(IamPermissions.WorkspaceOperate, PermissionTarget.WorkspaceWide);
            var report = capacity.Read(clock.GetUtcNow().UtcDateTime,
                project => ProjectAccessAuthorization.AllowsTasks(context, [project], IamPermissions.ProjectRead, registry),
                includeQuota: !scoped);
            return report is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(report);
        }).RequirePermission(IamPermissions.ProjectRead);
    }
}
