using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Host;

/// <summary>
/// A monolith that owns the local /api/v1 plane still serves only part of the
/// Runner surface. Device sign-in and host Git setup live on the standalone Task
/// Server, so a Runner route the monolith does not map answers a typed 404 that
/// tells the host's poller to stop instead of retrying every two seconds (AGT-3432).
/// </summary>
public static class MonolithRunnerRouteFallback
{
    public static void MapMonolithRunnerRouteFallback(this WebApplication app)
        => app.MapFallback("/api/v1/runners/{runnerId}/{**rest}", (HttpContext context) =>
            Results.NotFound(new ApiError(ApiErrorCodes.RouteUnavailable,
                $"This Studio server does not offer {context.Request.Method} {context.Request.Path}. "
                + "Device sign-in and host Git setup need the standalone Task Server.")));
}
