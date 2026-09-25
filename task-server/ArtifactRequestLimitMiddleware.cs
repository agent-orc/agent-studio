using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

/// <summary>Reports the configured artifact body ceiling as a typed 413.</summary>
public sealed class ArtifactRequestLimitMiddleware(
    RequestDelegate next,
    IOptions<TaskServerOptions> configured)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!HttpMethods.IsPost(context.Request.Method)
            || !path.StartsWith("/api/v1/runs/", StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith("/artifacts", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var limit = configured.Value.MaxRequestBodyBytes;
        var received = context.Request.ContentLength;
        if (received > limit)
        {
            await RejectAsync(context, limit, received);
            return;
        }

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                                                 && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await RejectAsync(context, limit, context.Request.ContentLength);
        }
    }

    private static Task RejectAsync(HttpContext context, long limit, long? received)
        => Problem(limit, received).ExecuteAsync(context);

    internal static IResult Problem(long limit, long? received)
        => Results.Json(new
        {
            type = "artifact-request-too-large",
            title = "Artifact request exceeds the server limit.",
            status = StatusCodes.Status413PayloadTooLarge,
            limitBytes = limit,
            receivedBytes = received,
        }, statusCode: StatusCodes.Status413PayloadTooLarge,
            contentType: "application/problem+json");
}
