using System.Globalization;

namespace AgentStudio.Git;

/// <summary>
/// Reports the Git processes one request started in <c>X-Git-Spawns</c>
/// (AGT-3318). The card-open provenance and file-history routes carry it, so
/// the Agent Studio board measurement can prove zero request-time Git under
/// derived source facts and see the count under the legacy read.
/// </summary>
internal sealed class GitSpawnCountFilter(ILogger<GitSpawnCountFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "X-Git-Spawns";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var scope = GitProcessTelemetry.BeginRequest("tasks/card-open", logger, includeNested: true);
        var result = await next(context);
        context.HttpContext.Response.Headers[HeaderName] =
            (GitProcessTelemetry.CurrentTally()?.Spawns ?? 0).ToString(CultureInfo.InvariantCulture);
        return result;
    }
}
