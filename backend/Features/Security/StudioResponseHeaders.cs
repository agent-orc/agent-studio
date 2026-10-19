namespace AgentStudio.Security;

/// <summary>
/// Hardening headers for every networked Studio response (AGT-3230). The
/// Caddy frontend of the networked Compose stack sends the same values for the
/// static shell; <c>StudioResponseHeaderPolicyTests</c> keeps both in step.
/// </summary>
public static class StudioResponseHeaderPolicy
{
    /// <summary>
    /// Baseline for the shell and API responses. <c>'unsafe-inline'</c> stays in
    /// <c>script-src</c>: sandboxed <c>srcdoc</c> frames inherit this policy and
    /// run the inline scripts of repository HTML on their opaque origin, and the
    /// production index loads its stylesheet through an inline <c>onload</c>.
    /// </summary>
    public const string ContentSecurityPolicy =
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

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// Adds the baseline without replacing a header the endpoint chose. A route
    /// with its own Content-Security-Policy (the URL preview proxy, task result
    /// HTML) keeps it. When that policy sandboxes the document, X-Frame-Options
    /// stays absent: the document has an opaque origin and Studio frames it, so
    /// its own policy governs framing.
    /// </summary>
    public static void Apply(IHeaderDictionary headers)
    {
        var ownPolicy = headers.ContentSecurityPolicy.ToString();
        var sandboxed = ownPolicy.Split(';', StringSplitOptions.TrimEntries)
            .Any(directive => directive.Equals("sandbox", StringComparison.OrdinalIgnoreCase)
                              || directive.StartsWith("sandbox ", StringComparison.OrdinalIgnoreCase));
        SetDefault(headers, "Content-Security-Policy", ContentSecurityPolicy);
        if (!sandboxed) SetDefault(headers, "X-Frame-Options", "SAMEORIGIN");
        SetDefault(headers, "Cross-Origin-Opener-Policy", "same-origin");
        SetDefault(headers, "Permissions-Policy", PermissionsPolicy);
        SetDefault(headers, "Referrer-Policy", "same-origin");
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
