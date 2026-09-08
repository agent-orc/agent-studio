using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace AgentStudio.Connector;

public static class ConnectorRouteSurface
{
    public static void MapAndValidate(WebApplication app, ConnectorRouteInventory inventory)
    {
        app.MapAllEndpoints();
        app.MapConnectorDevSeatCompatibilityEndpoints();
        MapTaskServerOperations(app, inventory);
        app.MapConnectorSessionEndpoints();
        MapHealth(app, inventory);

        var routeBuilder = (IEndpointRouteBuilder)app;
        var candidates = routeBuilder.DataSources.SelectMany(source => source.Endpoints).ToArray();
        var published = new List<Endpoint>();

        foreach (var operation in inventory.DevSeatOperations)
            published.AddRange(CloneLocalEndpoints(operation, candidates));

        foreach (var operation in inventory.TaskServerOperations.Where(operation => operation.Method != "WS"))
        {
            var endpoint = FindClassifiedEndpoint(operation, candidates)
                ?? throw Missing(operation);
            published.Add(endpoint);
        }

        var hubOperation = inventory.TaskServerOperations.Single(operation => operation.Method == "WS");
        var hubEndpoints = candidates
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<ConnectorClassifiedEndpointMetadata>().Any(metadata
                => metadata.Method == "WS"
                   && ConnectorRouteKey.NormalizePath(metadata.Path) == ConnectorRouteKey.NormalizePath(hubOperation.Path)))
            .ToArray();
        if (hubEndpoints.Length == 0) throw Missing(hubOperation);
        published.AddRange(hubEndpoints);

        published.AddRange(candidates.Where(endpoint =>
            endpoint.Metadata.GetMetadata<ConnectorControlEndpointMetadata>() is not null));

        var uniquePublished = published.Distinct().ToArray();
        routeBuilder.DataSources.Clear();
        routeBuilder.DataSources.Add(new StaticEndpointDataSource(uniquePublished));
        ValidatePublishedSurface(uniquePublished, inventory);
    }

    private static IReadOnlyList<Endpoint> CloneLocalEndpoints(
        ConnectorRouteOperation operation,
        IReadOnlyList<Endpoint> candidates)
    {
        var method = ConnectorRouteKey.NormalizeMethod(operation.Method);
        var matches = candidates
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ConnectorClassifiedEndpointMetadata>() is null)
            .Where(endpoint => ConnectorRouteKey.NormalizePhysicalPath(endpoint.RoutePattern.RawText ?? string.Empty)
                == ConnectorRouteKey.NormalizePhysicalPath(operation.Path))
            .Where(endpoint => SupportsMethod(endpoint, method))
            .OrderByDescending(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                ConnectorRouteKey.ToAspNetPattern(operation.Path),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            matches = candidates
                .OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.Metadata.GetMetadata<ConnectorClassifiedEndpointMetadata>() is null)
                .Where(endpoint => InventoryPatternMatchesActual(operation.Path, endpoint.RoutePattern.RawText ?? string.Empty))
                .Where(endpoint => SupportsMethod(endpoint, method))
                .ToArray();
        }
        if (matches.Length == 0) throw Missing(operation);
        return matches.Select(source => CloneLocalEndpoint(operation, method, source)).ToArray();
    }

    private static Endpoint CloneLocalEndpoint(
        ConnectorRouteOperation operation,
        string method,
        RouteEndpoint source)
    {
        var builder = new RouteEndpointBuilder(source.RequestDelegate, source.RoutePattern, source.Order)
        {
            DisplayName = $"Connector local {method} {operation.Path}",
        };
        foreach (var metadata in source.Metadata)
        {
            if (metadata is not IHttpMethodMetadata) builder.Metadata.Add(metadata);
        }
        builder.Metadata.Add(new HttpMethodMetadata([method]));
        builder.Metadata.Add(new ConnectorClassifiedEndpointMetadata(method, operation.Path, operation.Classification));
        return builder.Build();
    }

    private static bool InventoryPatternMatchesActual(string inventoryPath, string actualPath)
    {
        var approved = ConnectorRouteKey.NormalizePath(inventoryPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var actual = ConnectorRouteKey.NormalizePath(actualPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (approved.Length != actual.Length) return false;
        for (var index = 0; index < approved.Length; index++)
        {
            if (approved[index].StartsWith('{')) continue;
            if (!string.Equals(approved[index], actual[index], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static void MapTaskServerOperations(WebApplication app, ConnectorRouteInventory inventory)
    {
        foreach (var group in inventory.TaskServerOperations
                     .Where(operation => operation.Method != "WS")
                     .GroupBy(operation => new ConnectorRouteKey(
                         ConnectorRouteKey.NormalizeMethod(operation.Method),
                         ConnectorRouteKey.NormalizePhysicalPath(operation.Path))))
        {
            var operation = group.First();
            var pattern = ConnectorRouteKey.ToAspNetPattern(operation.Path);
            var method = ConnectorRouteKey.NormalizeMethod(operation.Method);
            var endpoint = app.MapMethods(pattern, [method], (HttpContext context, ConnectorProxy proxy) =>
                proxy.ForwardHttpAsync(context, operation));
            foreach (var alias in group)
                endpoint.WithMetadata(new ConnectorClassifiedEndpointMetadata(method, alias.Path, alias.Classification));
        }

        var hub = inventory.TaskServerOperations.Single(operation => operation.Method == "WS");
        foreach (var pattern in new[] { "/hubs/jobs", "/hubs/jobs/{**path}" })
        {
            app.MapMethods(pattern, [HttpMethods.Get, HttpMethods.Post, HttpMethods.Delete, HttpMethods.Options],
                    (HttpContext context, ConnectorProxy proxy) => proxy.ForwardHubAsync(context, hub))
                .WithMetadata(new ConnectorClassifiedEndpointMetadata("WS", hub.Path, hub.Classification));
        }
    }

    private static void MapHealth(WebApplication app, ConnectorRouteInventory inventory)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "live", role = "connector" }))
            .WithMetadata(new ConnectorControlEndpointMetadata());
        app.MapGet("/readyz", async (ConnectorUpstreamManager upstream, CancellationToken cancellationToken) =>
        {
            var snapshot = upstream.Capture();
            var probe = await upstream.ProbeAsync(snapshot, cancellationToken);
            var body = new
            {
                status = probe.Ready ? "ready" : "not-ready",
                mode = snapshot.Mode,
                generation = snapshot.Generation,
                upstream = snapshot.MaskedName,
                protocol = probe.Protocol,
                routeChecksum = inventory.RouteChecksum,
                lastSuccessfulProbeAt = probe.SuccessfulAtUtc ?? upstream.LastSuccessfulProbeUtc,
                failureCode = probe.FailureCode,
            };
            return probe.Ready
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).WithMetadata(new ConnectorControlEndpointMetadata());
    }

    private static Endpoint? FindClassifiedEndpoint(
        ConnectorRouteOperation operation,
        IEnumerable<Endpoint> candidates)
        => candidates.FirstOrDefault(endpoint => endpoint.Metadata
            .GetOrderedMetadata<ConnectorClassifiedEndpointMetadata>()
            .Any(metadata => metadata.Method == ConnectorRouteKey.NormalizeMethod(operation.Method)
                && ConnectorRouteKey.NormalizePath(metadata.Path) == ConnectorRouteKey.NormalizePath(operation.Path)
                && metadata.Classification == operation.Classification));

    private static bool SupportsMethod(RouteEndpoint endpoint, string method)
    {
        var metadata = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
        return metadata is null || metadata.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidatePublishedSurface(
        IReadOnlyCollection<Endpoint> endpoints,
        ConnectorRouteInventory inventory)
    {
        var classified = endpoints
            .Where(endpoint => endpoint is RouteEndpoint route
                && (route.RoutePattern.RawText?.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true
                    || route.RoutePattern.RawText?.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) == true))
            .SelectMany(endpoint =>
            {
                var metadata = endpoint.Metadata.GetOrderedMetadata<ConnectorClassifiedEndpointMetadata>();
                return metadata.Count > 0
                    ? metadata
                    : throw new InvalidOperationException($"Unclassified connector endpoint '{endpoint.DisplayName}' is exposed.");
            })
            .Select(metadata => ConnectorRouteKey.FromInventory(metadata.Method, metadata.Path))
            .Distinct()
            .ToHashSet();
        var approved = inventory.Operations
            .Select(operation => ConnectorRouteKey.FromInventory(operation.Method, operation.Path))
            .ToHashSet();
        if (!classified.SetEquals(approved))
        {
            var missing = approved.Except(classified).Select(key => $"{key.Method} {key.Path}");
            var extra = classified.Except(approved).Select(key => $"{key.Method} {key.Path}");
            throw new InvalidOperationException(
                $"Connector route surface differs from the shipped inventory. Missing: {string.Join(", ", missing)}. Extra: {string.Join(", ", extra)}.");
        }
    }

    private static InvalidOperationException Missing(ConnectorRouteOperation operation)
        => new($"Approved connector operation {operation.Method} {operation.Path} has no mapped handler.");

    private sealed class StaticEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }
}
