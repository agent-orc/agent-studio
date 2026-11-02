using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>Reports artifact and review report body ceilings as typed 413 responses.</summary>
public sealed class ArtifactRequestLimitMiddleware(
    RequestDelegate next,
    IOptions<TaskServerOptions> configured)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var artifact = HttpMethods.IsPost(context.Request.Method)
                       && path.StartsWith("/api/v1/runs/", StringComparison.OrdinalIgnoreCase)
                       && path.EndsWith("/artifacts", StringComparison.OrdinalIgnoreCase);
        var reviewReport = HttpMethods.IsPost(context.Request.Method)
                           && path.StartsWith("/api/v1/reviews/attempts/", StringComparison.OrdinalIgnoreCase)
                           && path.EndsWith("/report", StringComparison.OrdinalIgnoreCase);
        if (!artifact && !reviewReport)
        {
            await next(context);
            return;
        }

        var limit = reviewReport
            ? ReviewReportLimits.MaxRequestBodyBytes
            : configured.Value.MaxRequestBodyBytes;
        if (reviewReport
            && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            bodyLimit.MaxRequestBodySize = limit;
        var received = context.Request.ContentLength;
        if (received > limit)
        {
            await RejectAsync(context, limit, received, reviewReport);
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
            await RejectAsync(context, limit, context.Request.ContentLength, reviewReport);
        }
    }

    private static Task RejectAsync(HttpContext context, long limit, long? received, bool reviewReport)
        => (reviewReport ? ReviewProblem(limit, received) : Problem(limit, received)).ExecuteAsync(context);

    internal static IResult ReviewProblem(long limit, long? received)
        => Results.Json(new
        {
            type = "review-report-too-large",
            title = "Review report exceeds the server limit.",
            status = StatusCodes.Status413PayloadTooLarge,
            limitBytes = limit,
            receivedBytes = received,
        }, statusCode: StatusCodes.Status413PayloadTooLarge,
            contentType: "application/problem+json");

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
