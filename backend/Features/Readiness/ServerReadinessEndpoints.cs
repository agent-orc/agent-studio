using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Readiness;

/// <summary>
/// Readiness routes (AGT-3412). The operations page reads the latest report and
/// can ask for a recheck; the move tool reads the same report from inside the
/// API container through the loopback-only health route.
/// </summary>
public static class ServerReadinessEndpoints
{
    public const string ProbePath = "/healthz/readiness";

    public static void MapServerReadinessEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/readiness", (HttpContext context, ServerReadinessMonitor monitor, ProjectRegistry projects) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(Visible(context, monitor.Latest, projects));
        }).RequireStudioRole(StudioRoles.Operator);

        // A recheck is the only path that collects inside a request; it is a POST,
        // and the Git facts it reads stay cached for a minute per repository.
        app.MapPost("/api/system/readiness/refresh", async (HttpContext context, ServerReadinessMonitor monitor,
            ProjectRegistry projects, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(Visible(context, await monitor.RefreshAsync(ct), projects));
        }).RequireStudioRole(StudioRoles.Operator);

        // Like /healthz/container, only a caller inside the API container is answered:
        // the report names projects, paths and hosts.
        app.MapGet(ProbePath, (HttpContext context, ServerReadinessMonitor monitor) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None)
                ? Results.Ok(monitor.Latest)
                : Results.NotFound();
        });
    }

    /// <summary>Project lines only for projects the signed-in person may see; server and host lines for every operator.</summary>
    private static ServerReadinessReport Visible(HttpContext context, ServerReadinessReport report, ProjectRegistry projects)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            ? report.Where(check => check.Scope != ReadinessScopes.Project
                || ProjectAccessAuthorization.Allows(human.User, check.Subject, projects))
            : report;
}
