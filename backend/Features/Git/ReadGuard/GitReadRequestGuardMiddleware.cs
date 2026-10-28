using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace AgentStudio.Git;

/// <summary>
/// Opens the ambient Git read scope for every GET request that routing matched
/// to an endpoint, keyed by the route template from endpoint metadata. It runs
/// right after routing, outside the feature filters, so nested service labels
/// (<c>wiki/*</c>, <c>board/merge-status</c>, ...) and the board read gate's
/// shared build all report into the outer HTTP scope. SignalR hubs are not
/// read requests: their invocations reuse the negotiating GET's context.
/// </summary>
internal sealed class GitReadRequestGuardMiddleware(RequestDelegate next, ILogger<GitReadRequestGuardMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || context.GetEndpoint() is not RouteEndpoint endpoint
            || endpoint.Metadata.GetMetadata<HubMetadata>() is not null)
        {
            await next(context);
            return;
        }

        var template = GitReadRouteKey.Template(endpoint);
        var scope = GitReadRequestGuard.BeginRead(HttpMethods.Get, template, logger);
        try
        {
            await next(context);
        }
        finally
        {
            scope.Dispose();
            Notify(context, scope);
        }

        // A helper that swallows exceptions can hide the strict-mode throw.
        // The request still fails, so the test that issued it sees the breach.
        if (GitReadRequestGuard.Mode == GitReadGuardMode.Strict && scope.Violated && !scope.ThrownAtSpawn)
        {
            var violation = scope.Violation(string.Join(",", scope.Commands));
            if (!context.Response.HasStarted) throw violation;
            logger.LogError(violation, "git-read-guard-violation-after-response route={Route}", template);
            context.Abort();
        }
    }

    private static void Notify(HttpContext context, GitReadRequestScope scope)
    {
        var observers = context.RequestServices.GetServices<IGitReadRequestObserver>();
        if (!observers.Any()) return;
        var record = new GitReadRequestRecord(scope.Method, scope.RouteTemplate,
            context.Request.Path + context.Request.QueryString, context.Response.StatusCode,
            scope.Commands, scope.Verdict, scope.Exception?.SpawnCeiling);
        foreach (var observer in observers) observer.OnCompleted(record);
    }
}

/// <summary>Spawn evidence of one finished GET request, for census tests and diagnostics.</summary>
public sealed record GitReadRequestRecord(
    string Method,
    string RouteTemplate,
    string Path,
    int StatusCode,
    IReadOnlyList<string> Commands,
    GitReadSpawnVerdict Verdict,
    int? SpawnCeiling)
{
    public int Spawns => Commands.Count;
}

/// <summary>
/// Receives every finished GET request's spawn evidence. Registered per host,
/// so a test observes only its own requests.
/// </summary>
public interface IGitReadRequestObserver
{
    void OnCompleted(GitReadRequestRecord record);
}

/// <summary>The route key the guard and the census share.</summary>
public static class GitReadRouteKey
{
    public static string Template(RouteEndpoint endpoint)
        => "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
}

internal static class GitReadRequestGuardExtensions
{
    /// <summary>
    /// Adds the read guard after routing. <c>GitReadGuard:Mode</c> (Warn or
    /// Strict) overrides the process default.
    /// </summary>
    internal static IApplicationBuilder UseGitReadRequestGuard(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (Enum.TryParse<GitReadGuardMode>(configuration["GitReadGuard:Mode"], ignoreCase: true, out var mode))
            GitReadRequestGuard.Mode = mode;
        return app.UseMiddleware<GitReadRequestGuardMiddleware>();
    }
}
