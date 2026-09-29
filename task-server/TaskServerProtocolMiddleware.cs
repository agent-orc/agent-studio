using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed class TaskServerProtocolMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresProtocolHeader(context.Request.Path))
        {
            await next(context);
            return;
        }

        var raw = context.Request.Headers[TaskServerProtocol.HeaderName].FirstOrDefault();
        if (!int.TryParse(raw, out var version) || !TaskServerProtocol.Supports(version))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsJsonAsync(new ApiError(
                "protocol-unsupported",
                $"Client protocol '{raw ?? "missing"}' is outside the supported range " +
                $"{TaskServerProtocol.MinimumSupported}-{TaskServerProtocol.MaximumSupported}.",
                new
                {
                    received = raw,
                    minimumSupported = TaskServerProtocol.MinimumSupported,
                    maximumSupported = TaskServerProtocol.MaximumSupported,
                }));
            return;
        }

        await next(context);
    }

    private static bool RequiresProtocolHeader(PathString path)
        => path.StartsWithSegments("/api/v1")
           && !path.Equals("/api/v1/protocol", StringComparison.OrdinalIgnoreCase)
           && !path.Equals("/api/v1/protocol/compatibility", StringComparison.OrdinalIgnoreCase)
           // The attach handshake carries its own ranges and answers a mismatch
           // with a negotiation reason instead of a bare header rejection.
           && !path.Equals("/api/v1/protocol/attach", StringComparison.OrdinalIgnoreCase);
}
