namespace AgentStudio.Security;

/// <summary>
/// Hardening headers for every networked Studio response (AGT-3230). The
/// Caddy frontend of the networked Compose stack sends the shell policy for the
/// static Studio shell; <c>StudioResponseHeaderPolicyTests</c> keeps the shared
/// values in step.
/// </summary>
public static class StudioResponseHeaderPolicy
{
    /// <summary>
    /// Policy of the static Studio shell. Only <c>Caddyfile.frontend</c> sends
    /// it; the API never applies it, because <c>'unsafe-inline'</c> in
    /// <c>script-src</c> would let stored HTML run on the Studio origin. It stays
    /// here only for the parity test. The shell keeps <c>'unsafe-inline'</c>:
    /// sandboxed <c>srcdoc</c> frames inherit this policy and run the inline
    /// scripts of repository HTML on their opaque origin, and the production
    /// index loads its stylesheet through an inline <c>onload</c>.
    /// </summary>
    public const string FrontendShellContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "media-src 'self' data: blob:; " +
        "connect-src 'self'; " +
        "frame-src 'self'; " +
        "worker-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'self'";

    /// <summary>
    /// API responses a browser can render as a document (agent-written task
    /// files, wiki SVG assets). The sandbox gives them an opaque origin and no
    /// script, so stored markup never runs as Studio.
    /// </summary>
    public const string DocumentContentSecurityPolicy =
        "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; frame-ancestors 'self'";

    /// <summary>Every other API response (JSON, text, binary).</summary>
    public const string ApiContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";

    private static readonly string[] DocumentMediaTypes =
        ["text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml"];

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    /// <summary>The policy for a response without its own, by media type.</summary>
    public static string DefaultContentSecurityPolicy(string? contentType)
    {
        var mediaType = (contentType ?? string.Empty).Split(';', 2)[0].Trim();
        return DocumentMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
            ? DocumentContentSecurityPolicy
            : ApiContentSecurityPolicy;
    }

    /// <summary>
    /// Adds the baseline without replacing a header the endpoint chose. A route
    /// with its own Content-Security-Policy (the URL preview proxy, task result
    /// HTML, the recovery console) keeps it. When that policy sandboxes the
    /// document, X-Frame-Options stays absent: the document has an opaque
    /// origin and Studio frames it, so its own policy governs framing.
    /// </summary>
    public static void Apply(IHeaderDictionary headers)
    {
        var ownPolicy = headers.ContentSecurityPolicy.ToString();
        var sandboxed = ownPolicy.Split(';', StringSplitOptions.TrimEntries)
            .Any(directive => directive.Equals("sandbox", StringComparison.OrdinalIgnoreCase)
                              || directive.StartsWith("sandbox ", StringComparison.OrdinalIgnoreCase));
        SetDefault(headers, "Content-Security-Policy", DefaultContentSecurityPolicy(headers.ContentType));
        if (!sandboxed) SetDefault(headers, "X-Frame-Options", "SAMEORIGIN");
        SetDefault(headers, "Cross-Origin-Opener-Policy", "same-origin");
        SetDefault(headers, "Permissions-Policy", PermissionsPolicy);
        SetDefault(headers, "Referrer-Policy", "same-origin");
        SetDefault(headers, "Cache-Control", "no-store");
        headers.XContentTypeOptions = "nosniff";
    }

    private static void SetDefault(IHeaderDictionary headers, string name, string value)
    {
        if (string.IsNullOrEmpty(headers[name].ToString())) headers[name] = value;
    }
}

/// <summary>Applies <see cref="StudioResponseHeaderPolicy"/> when the response starts, after the endpoint ran.</summary>
public sealed class StudioResponseHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            StudioResponseHeaderPolicy.Apply(context.Response.Headers);
            // API and hub responses may contain private data even when an endpoint
            // selected a browser cache lifetime. Cloudflare must not store them.
            if (context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/hubs")
                || context.Request.Cookies.ContainsKey(AccessSecurityStore.SessionCookieName)
                || context.Items.ContainsKey(AccessSecurityMiddleware.HumanPrincipalItem))
                context.Response.Headers.CacheControl = "no-store";
            // UseHsts sets this before the endpoint runs; the exception handler
            // clears it with the other headers of the failed response.
            if (context.Request.IsHttps && string.IsNullOrEmpty(context.Response.Headers.StrictTransportSecurity))
                context.Response.Headers.StrictTransportSecurity = StudioResponseHeaderPolicy.StrictTransportSecurity;
            return Task.CompletedTask;
        });
        return next(context);
    }
}

public static class StudioResponseHeadersExtensions
{
    public static IApplicationBuilder UseStudioResponseHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<StudioResponseHeadersMiddleware>();
}
