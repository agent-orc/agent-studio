using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Tasks;

namespace AgentStudio.Operator;

public static class OperatorLogEndpoints
{
    public static void MapOperatorLogEndpoints(this WebApplication app)
    {
        app.MapPost("/api/operator/log", async (HttpContext context, OperatorLogRequest request,
            OperatorCoordinationStore store, ProjectRegistry projects, CancellationToken ct) =>
        {
            if (!CanWrite(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(request.Project))
                return Results.BadRequest(new { error = "project-required" });
            if (request.Project != "*" && !ProjectAccessAuthorization.AllowsTasks(context, [request.Project],
                    IamPermissions.ProjectCardsWrite, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.Project == "*" && context.Items[AccessSecurityMiddleware.HumanPrincipalItem]
                    is not HumanPrincipal { User.Role: StudioRoles.Owner })
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return Results.Ok(await store.AppendAsync(OperatorEndpoints.Actor(context), request, ct)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequirePermission(IamPermissions.ProjectCardsWrite);

        app.MapGet("/api/operator/log", async (HttpContext context, string? project, long? after,
            OperatorCoordinationStore store, ProjectRegistry projects, CancellationToken ct) =>
        {
            if (after is < 0) return Results.BadRequest(new { error = "invalid-cursor" });
            if (project is not null && project != "*"
                && !ProjectAccessAuthorization.AllowsTasks(context, [project],
                    IamPermissions.ProjectRead, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var entries = (await store.ReadAsync(project, after ?? 0, ct))
                .Where(entry => entry.Project == "*"
                    ? context.Items[AccessSecurityMiddleware.HumanPrincipalItem]
                        is HumanPrincipal { User.Role: StudioRoles.Owner }
                    : ProjectAccessAuthorization.AllowsTasks(context, [entry.Project],
                        IamPermissions.ProjectRead, projects)).ToArray();
            return Results.Ok(new { entries, cursor = entries.LastOrDefault()?.Cursor ?? after ?? 0 });
        }).RequirePermission(IamPermissions.ProjectRead);
    }

    private static bool CanWrite(HttpContext context)
        => context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is ServiceAccountPrincipal
            || context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                && human.User.Role == StudioRoles.Owner
            || context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is null
                && context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is null;
}
