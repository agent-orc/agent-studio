using AgentStudio.Security;

namespace AgentStudio.Operator;

public sealed record DeployPlanRequest(string TargetVersion, int WindowMinutes = 30,
    bool OverrideBlocker = false);

/// <summary>Read-only preflight and an expiring server-first update plan.</summary>
public static class DeployPreflightEndpoints
{
    public static void MapDeployPreflightEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/operator");
        group.MapGet("/preflight/restart", (HttpContext context, DeployPreflightService service,
            bool? overrideBlocker) => Preflight(context, service, "restart", overrideBlocker == true))
            .RequirePermission(IamPermissions.WorkspaceRead);
        group.MapGet("/preflight/deploy", (HttpContext context, DeployPreflightService service,
            bool? overrideBlocker) => Preflight(context, service, "deploy", overrideBlocker == true))
            .RequirePermission(IamPermissions.WorkspaceRead);
        group.MapPost("/deploy/plan", async (HttpContext context, DeployPreflightService service,
            DeployPlanRequest request, CancellationToken ct) =>
        {
            if (ScopeRefusal(context) is { } scope) return scope;
            if (request.OverrideBlocker) return OverrideUnavailable();
            if (string.IsNullOrWhiteSpace(request.TargetVersion)
                || request.TargetVersion.Length > 64
                || request.TargetVersion.Any(char.IsControl))
                return Results.BadRequest(new { error = "invalid-target-version" });
            if (request.WindowMinutes is < 5 or > 120)
                return Results.BadRequest(new { error = "invalid-plan-window", minMinutes = 5, maxMinutes = 120 });
            var preflight = service.Check("deploy");
            if (!preflight.Allowed) return Results.Conflict(preflight);
            try { return Results.Ok(await service.PlanAsync(request.TargetVersion, request.WindowMinutes, ct)); }
            catch (HttpRequestException)
            {
                return Results.Json(new { error = "task-server-version-unavailable" }, statusCode: 503);
            }
        }).RequirePermission(IamPermissions.WorkspaceRead);
        // SM-5 has not delivered digest-bound Owner approval. A blocker
        // override is classified T2 and remains refused for every caller.
        group.MapPost("/preflight/{operation}/override", (string operation) =>
            operation is "restart" or "deploy" ? OverrideUnavailable()
                : Results.NotFound(new { error = "unknown-preflight" }))
            .RequirePermission(IamPermissions.WorkspaceOperate);
    }

    private static IResult Preflight(HttpContext context, DeployPreflightService service,
        string operation, bool overrideBlocker)
    {
        if (ScopeRefusal(context) is { } scope) return scope;
        if (overrideBlocker) return OverrideUnavailable();
        var snapshot = service.Check(operation);
        return snapshot.Allowed ? Results.Ok(snapshot) : Results.Conflict(snapshot);
    }

    private static IResult? ScopeRefusal(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            && !ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User)
            ? Results.Json(new { error = "installation-scope-required" }, statusCode: 403)
            : null;

    private static IResult OverrideUnavailable()
        => Results.Json(new { error = "owner-approval-unavailable", tier = "T2",
            message = "Blocker override requires Owner step-up approval, which is not available yet." }, statusCode: 403);
}
