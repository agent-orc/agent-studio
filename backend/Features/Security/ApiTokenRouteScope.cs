using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace AgentStudio.Security;

/// <summary>The project address a mapped API route promises to enforce for API tokens.</summary>
public sealed record ApiTokenRouteScope(ProjectRouteScope Scope);

public static class ApiTokenRouteScopes
{
    /// <summary>
    /// Publish an explicit scope on every mapped API endpoint. Project and task
    /// address patterns are scoped; all other route patterns are workspace-wide.
    /// A query string never promotes a workspace route to a project route.
    /// </summary>
    public static void Declare(WebApplication app)
    {
        var routes = (IEndpointRouteBuilder)app;
        var published = routes.DataSources.SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint is RouteEndpoint route && IsApi(route)
                ? CloneWithScope(route)
                : endpoint)
            .ToArray();
        routes.DataSources.Clear();
        routes.DataSources.Add(new StaticEndpointDataSource(published));
    }

    private static bool IsApi(RouteEndpoint route)
        => (route.RoutePattern.RawText ?? string.Empty).StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    private static RouteEndpoint CloneWithScope(RouteEndpoint route)
    {
        var builder = new RouteEndpointBuilder(route.RequestDelegate, route.RoutePattern, route.Order)
        {
            DisplayName = route.DisplayName,
        };
        foreach (var item in route.Metadata) builder.Metadata.Add(item);
        builder.Metadata.Add(new ApiTokenRouteScope(Classify(route.RoutePattern.RawText ?? string.Empty)));
        return (RouteEndpoint)builder.Build();
    }

    internal static ProjectRouteScope Classify(string pattern)
    {
        var parts = pattern.Trim('/').Split('/');
        if (parts.Length >= 3 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[1].Equals("projects", StringComparison.OrdinalIgnoreCase) && IsParameter(parts[2]))
                return ProjectRouteScope.Addressed;
            if (parts[1].Equals("tasks", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 3 && parts[2] is "reorder" or "batch-move" or "reference-status" or "archive")
                    return ProjectRouteScope.DeferredToHandler;
                if (IsParameter(parts[2])) return ProjectRouteScope.Addressed;
            }
        }
        if (pattern.TrimEnd('/').Equals("/api/tasks", StringComparison.OrdinalIgnoreCase))
            return ProjectRouteScope.DeferredToHandler;
        return ProjectRouteScope.WorkspaceWide;
    }

    private static bool IsParameter(string part) => part.StartsWith('{') && part.EndsWith('}') && !part.StartsWith("{**", StringComparison.Ordinal);

    private sealed class StaticEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }
}
