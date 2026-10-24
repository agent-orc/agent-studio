using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Security;

/// <summary>
/// The public-demo browser edge (W34 dossier §8 S4, depends on the §8 S2
/// route inventory). No-ops entirely outside the <c>public-demo-readonly</c>
/// profile. Runs after <see cref="PublicDemoExecutionLockMiddleware"/>, so a
/// route S2 already denies by identity keeps its specific
/// <c>execution-disabled</c> code; this middleware is the broader net behind
/// it. Every remaining unsafe visitor method is denied outright, apart from
/// the exact SignalR negotiate and authenticated replay-ingest protocol
/// exceptions, and every safe API method must match an explicit read
/// allowlist. It also carries the edge's other visitor-facing duties:
/// same-origin TLS, hardening response headers, the ephemeral viewer cookie,
/// and an Origin check for cross-site callers.
/// </summary>
public sealed class PublicDemoEdgeMiddleware(
    RequestDelegate next,
    StartupExecutionAdmission admission,
    PublicDemoViewerSessionStore viewerSessions)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-src 'self'; " +
        "frame-ancestors 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!admission.IsPublicDemo)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        var isReplayIngest = path.Equals(
            "/api/runner/replay/events",
            StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(context.Request.Method);
        if (IsHealthCheck(path))
        {
            await next(context);
            return;
        }

        context.Response.OnStarting(() =>
        {
            ApplySecurityHeaders(context.Response);
            return Task.CompletedTask;
        });

        // The VM exposes only the TLS edge. The replay service reaches this
        // one authenticated route over the private loopback/container network,
        // where terminating TLS again would add no visitor protection.
        if (!context.Request.IsHttps && !isReplayIngest)
        {
            await Deny(context, StatusCodes.Status426UpgradeRequired, "https-required", "The public demo requires HTTPS.");
            return;
        }

        if (!OriginMatchesRequest(context.Request))
        {
            await Deny(
                context,
                StatusCodes.Status403Forbidden,
                PublicDemoEdgePolicy.ReadOnlyDeniedCode,
                "Cross-origin requests are not accepted by the public demo edge.");
            return;
        }

        if (!isReplayIngest) EnsureViewerSession(context);

        var isHub = path.Equals("/hubs", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase);
        if (isHub)
        {
            // SignalR needs one narrowly identified POST for negotiate. Every
            // other unsafe hub probe is still a raw visitor mutation and gets
            // the same typed edge denial as an unsafe API request.
            var allowedHubMethod = PublicDemoEdgePolicy.IsSafeMethod(context.Request.Method)
                                   || PublicDemoReadAllowlist.AllowsJobsHubNegotiate(
                                       path,
                                       context.Request.Method);
            if (!PublicDemoReadAllowlist.AllowsJobsHubPath(path) || !allowedHubMethod)
            {
                await Deny(
                    context,
                    StatusCodes.Status403Forbidden,
                    PublicDemoEdgePolicy.ReadOnlyDeniedCode,
                    "This hub route or method is not on the public demo allowlist.");
                return;
            }
            await next(context);
            return;
        }

        var isApi = path.Equals("/api", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (!isReplayIngest)
        {
            var decision = PublicDemoEdgePolicy.Decide(
                admission.StartupProfile,
                context.Request.Method,
                !isApi || PublicDemoReadAllowlist.Allows(path));
            if (!decision.Allowed)
            {
                await Deny(context, StatusCodes.Status403Forbidden, decision.Code, decision.Message);
                return;
            }
        }

        await next(context);
    }

    private void EnsureViewerSession(HttpContext context)
    {
        var existing = context.Request.Cookies[PublicDemoViewerSessionStore.CookieName];
        if (viewerSessions.Touch(existing)) return;

        var issued = viewerSessions.Issue();
        context.Response.Cookies.Append(PublicDemoViewerSessionStore.CookieName, issued, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            MaxAge = viewerSessions.SessionLifetime,
            Path = "/",
        });
    }

    private static void ApplySecurityHeaders(HttpResponse response)
    {
        var headers = response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=()";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
    }

    private static bool OriginMatchesRequest(HttpRequest request)
    {
        var origin = request.Headers.Origin.FirstOrDefault();
        // No Origin header: a same-site top-level navigation or a simple GET
        // that browsers do not attach one to. Nothing to verify - the safe
        // methods it can carry stay bounded by the read allowlist below.
        if (string.IsNullOrEmpty(origin)) return true;
        return BrowserOriginPolicy.OriginMatches(origin, request.Scheme, request.Host);
    }

    private static bool IsHealthCheck(string path)
        => path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/drain", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/healthz/orchestration-engine", StringComparison.OrdinalIgnoreCase);

    private static async Task Deny(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            error = code,
            message,
            profile = ExecutionAdmissionPolicy.PublicDemoProfile,
        });
    }
}

public static class PublicDemoEdgeMiddlewareExtensions
{
    public static IApplicationBuilder UsePublicDemoReadOnlyEdge(this IApplicationBuilder app)
        => app.UseMiddleware<PublicDemoEdgeMiddleware>();
}
