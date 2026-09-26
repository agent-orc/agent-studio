namespace AgentStudio.Diagnostics;

/// <summary>Gives artifact callers a machine-readable 413 before JSON binding reads the body.</summary>
public sealed class ArtifactRequestLimitMiddleware(RequestDelegate next, ArtifactRequestLimits limits)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsArtifactUpload(context.Request))
        {
            await next(context);
            return;
        }

        var received = context.Request.ContentLength;
        if (received > limits.MaxRequestBodyBytes)
        {
            await RejectAsync(context, limits.MaxRequestBodyBytes, received);
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
            await RejectAsync(context, limits.MaxRequestBodyBytes,
                context.Request.ContentLength ?? context.Request.Body.LengthIfSeekable());
        }
    }

    private static bool IsArtifactUpload(HttpRequest request)
        => HttpMethods.IsPost(request.Method)
           && string.Equals(request.Path.Value, "/api/runner/artifacts", StringComparison.OrdinalIgnoreCase);

    internal static Task RejectAsync(HttpContext context, long limit, long? received)
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

internal static class ArtifactRequestStreamExtensions
{
    public static long? LengthIfSeekable(this Stream stream)
        => stream.CanSeek ? stream.Length : null;
}
