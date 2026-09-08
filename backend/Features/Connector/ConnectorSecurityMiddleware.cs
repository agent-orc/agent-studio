namespace AgentStudio.Connector;

public sealed class ConnectorSecurityMiddleware(
    RequestDelegate next,
    ConnectorOptions options,
    ConnectorSessionStore sessions)
{
    private static readonly HashSet<string> SafeMethods = new(
        [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options],
        StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.Host.Value, options.Authority, StringComparison.Ordinal))
        {
            await RejectAsync(context, StatusCodes.Status400BadRequest, "connector-host-rejected");
            return;
        }

        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (origin is not null && !string.Equals(origin, options.StudioOrigin, StringComparison.Ordinal))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "connector-origin-rejected");
            return;
        }

        var sessionBootstrap = context.Request.Path == "/connector/session"
            && HttpMethods.IsGet(context.Request.Method);
        var livenessProbe = context.Request.Path == "/healthz" || context.Request.Path == "/readyz";
        var protectedSurface = context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/hubs")
            || (context.Request.Path == "/connector/session" && !sessionBootstrap);
        var unsafeRequest = !SafeMethods.Contains(context.Request.Method);
        var websocketUpgrade = context.WebSockets.IsWebSocketRequest;

        if ((unsafeRequest || websocketUpgrade || sessionBootstrap)
            && !string.Equals(origin, options.StudioOrigin, StringComparison.Ordinal))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "connector-origin-required");
            return;
        }

        if (protectedSurface && !livenessProbe)
        {
            if (!sessions.ValidateSession(context.Request, out var session))
            {
                await RejectAsync(context, StatusCodes.Status401Unauthorized, "connector-session-required");
                return;
            }
            if (unsafeRequest && !sessions.ValidateCsrf(context.Request, session))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden, "connector-csrf-rejected");
                return;
            }
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await next(context);
    }

    private static async Task RejectAsync(HttpContext context, int statusCode, string code)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { code });
    }
}

public static class ConnectorSessionEndpoints
{
    public static void MapConnectorSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/connector/session", (HttpResponse response, ConnectorSessionStore sessions) =>
        {
            sessions.Issue(response);
            return Results.Ok(new { status = "issued" });
        }).WithMetadata(new ConnectorControlEndpointMetadata());
        app.MapDelete("/connector/session", (HttpRequest request, HttpResponse response, ConnectorSessionStore sessions) =>
        {
            sessions.Logout(request, response);
            return Results.NoContent();
        }).WithMetadata(new ConnectorControlEndpointMetadata());
    }
}
