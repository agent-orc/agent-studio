namespace AgentStudio.Management;

/// <summary>
/// Enforces the management maintenance projection at the HTTP authority
/// boundary. Reads and recovery controls remain available. In-flight Runners
/// may renew/release leases and persist completion evidence while new claims
/// and ordinary mutations are refused.
/// </summary>
public sealed class ManagementModeMiddleware(RequestDelegate next)
{
    private static readonly string[] DrainSafeWrites =
    [
        "/api/runner/lease/renew",
        "/api/runner/lease/release",
        "/api/runner/logs",
        "/api/runner/events",
        "/api/runner/artifacts",
        "/api/runner/completion",
    ];

    public async Task InvokeAsync(HttpContext context, ManagementService management)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            await next(context);
            return;
        }

        var path = Normalize(context.Request.Path.Value);
        var state = management.CurrentMaintenance();
        if (state.Mode == "normal"
            || path.StartsWith("/api/v1/management", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)
            || DrainSafeWrites.Contains(path, StringComparer.OrdinalIgnoreCase)
            // Application telemetry writes only the telemetry store, never task state.
            || path.StartsWith("/api/telemetry/otlp/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "maintenance-active",
            mode = state.Mode,
            message = "The Task Server is not admitting ordinary mutations. Use the management API to inspect or exit maintenance.",
        });
    }

    private static string Normalize(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}

public static class ManagementModeMiddlewareExtensions
{
    public static IApplicationBuilder UseManagementMode(this IApplicationBuilder app)
        => app.UseMiddleware<ManagementModeMiddleware>();
}
