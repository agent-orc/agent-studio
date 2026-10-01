namespace AgentStudio.Connector;

public sealed class ConnectorSecurityMiddleware(
    RequestDelegate next,
    ConnectorOptions options,
    ConnectorSessionStore sessions,
    ConnectorRouteInventory inventory)
{
    private const string SessionPath = "/connector/session";

    // The Studio negotiates on the inventory's hub path (/hubs/v1/studio since
    // AGT-2983), so the CSRF-free negotiate follows that path, not a literal.
    private readonly PathString _hubNegotiatePath = inventory.TaskServerOperations
        .Single(operation => operation.Method == "WS").Path.TrimEnd('/') + "/negotiate";

    private static readonly HashSet<string> SafeMethods = new(
        [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options],
        StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var origin = request.Headers.Origin.FirstOrDefault();
        var unsafeRequest = !SafeMethods.Contains(request.Method);
        var surface = Classify(request);
        var sessionValid = sessions.ValidateSession(request, out var session);
        var facts = new ConnectorRequestFacts(
            HostAllowed: string.Equals(request.Host.Value, options.Authority, StringComparison.Ordinal),
            Origin: origin is null
                ? ConnectorOriginFact.Absent
                : options.IsStudioOrigin(origin) ? ConnectorOriginFact.Studio : ConnectorOriginFact.Foreign,
            SameOriginFetch: string.Equals(
                request.Headers["Sec-Fetch-Site"].FirstOrDefault(),
                "same-origin",
                StringComparison.OrdinalIgnoreCase),
            Surface: surface,
            UnsafeMethod: unsafeRequest,
            WebSocketUpgrade: context.WebSockets.IsWebSocketRequest,
            HubNegotiate: HttpMethods.IsPost(request.Method)
                && request.Path.Equals(_hubNegotiatePath, StringComparison.OrdinalIgnoreCase),
            SessionValid: sessionValid,
            CsrfValid: sessionValid && unsafeRequest && sessions.ValidateCsrf(request, session));

        var admission = ConnectorRequestPolicy.Decide(facts);
        if (admission.Rejected)
        {
            await RejectAsync(context, admission.RejectStatus!.Value, admission.RejectCode!);
            return;
        }
        if (admission.IssueSession) sessions.Issue(context.Response);

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await next(context);
    }

    private static ConnectorSurface Classify(HttpRequest request)
    {
        if (request.Path == "/healthz" || request.Path == "/readyz") return ConnectorSurface.Liveness;
        if (request.Path == SessionPath)
        {
            return HttpMethods.IsGet(request.Method) || HttpMethods.IsPost(request.Method)
                ? ConnectorSurface.SessionBootstrap
                : ConnectorSurface.SessionControl;
        }
        return request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/hubs")
            ? ConnectorSurface.Protected
            : ConnectorSurface.Other;
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
        app.MapMethods("/connector/session", [HttpMethods.Get, HttpMethods.Post], (HttpResponse response, ConnectorSessionStore sessions) =>
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
