using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Connector;

public sealed record ConnectorRouteOperation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("targetRoute")] string TargetRoute);

public sealed class ConnectorRouteInventory
{
    public const string ResourceName = "AgentStudio.Connector.Routes";
    public const string ExpectedInventorySha256 = "CE5A15043F8A45460C486E9AF9A6A8F1772B4FEC389FDD550DDB0F436C584EBE";
    public const string DevSeatClassification = "dev-seat";
    public const string TaskServerClassification = "task-server";

    /// <summary>
    /// A frontend operation whose Angular call site was removed (AGT-2758).
    /// Kept as a row for the audit trail but never mapped or forwarded: it
    /// carries no route and is excluded from both <see cref="DevSeatOperations"/>
    /// and <see cref="TaskServerOperations"/>.
    /// </summary>
    public const string RetiredClassification = "retired";

    private readonly Dictionary<ConnectorRouteKey, ConnectorRouteOperation> _routes;

    private ConnectorRouteInventory(
        IReadOnlyList<ConnectorRouteOperation> operations,
        string sourceChecksum,
        string routeChecksum)
    {
        Operations = operations;
        SourceChecksum = sourceChecksum;
        RouteChecksum = routeChecksum;
        _routes = operations.ToDictionary(
            operation => ConnectorRouteKey.FromInventory(operation.Method, operation.Path));
    }

    public IReadOnlyList<ConnectorRouteOperation> Operations { get; }
    public string SourceChecksum { get; }
    public string RouteChecksum { get; }
    public IReadOnlyList<ConnectorRouteOperation> DevSeatOperations
        => Operations.Where(operation => operation.Classification == DevSeatClassification).ToArray();
    public IReadOnlyList<ConnectorRouteOperation> TaskServerOperations
        => Operations.Where(operation => operation.Classification == TaskServerClassification).ToArray();

    public bool TryGet(string method, string path, out ConnectorRouteOperation operation)
        => _routes.TryGetValue(ConnectorRouteKey.FromEndpoint(method, path), out operation!);

    public static ConnectorRouteInventory Load(Assembly? assembly = null)
    {
        assembly ??= typeof(ConnectorRouteInventory).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded connector route inventory '{ResourceName}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var sourceChecksum = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(sourceChecksum, ExpectedInventorySha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Connector route inventory checksum mismatch. Expected {ExpectedInventorySha256}, received {sourceChecksum}.");

        var document = JsonSerializer.Deserialize<InventoryDocument>(bytes)
            ?? throw new InvalidOperationException("Connector route inventory is empty.");
        var operations = document.FrontendRoutes ?? [];
        // 366/105/260/1 reflects the AGT-2758 P3 resolution: 7 routes
        // reclassified dev-seat (local checkout/CLI-probe operations the
        // original heuristic mis-classified task-server), 1 retired (dead
        // Angular call site removed), and 1 new dev-seat route split off the
        // former mixed /api/search.
        if (operations.Count != 366
            || operations.Count(operation => operation.Classification == DevSeatClassification) != 105
            || operations.Count(operation => operation.Classification == TaskServerClassification) != 260
            || operations.Count(operation => operation.Classification == RetiredClassification) != 1)
            throw new InvalidOperationException("Connector route inventory does not contain the approved 105/260/1 route split.");
        if (operations.Any(operation => operation.Classification is not (
                DevSeatClassification or TaskServerClassification or RetiredClassification)))
            throw new InvalidOperationException("Connector route inventory contains an unclassified operation.");

        var keys = operations.Select(operation => ConnectorRouteKey.FromInventory(operation.Method, operation.Path)).ToArray();
        if (keys.Distinct().Count() != keys.Length)
            throw new InvalidOperationException("Connector route inventory contains duplicate method and path operations.");

        var routeChecksum = ComputeRouteChecksum(operations);
        return new ConnectorRouteInventory(operations, sourceChecksum, routeChecksum);
    }

    public static string ComputeRouteChecksum(IEnumerable<ConnectorRouteOperation> operations)
    {
        var canonical = string.Join('\n', operations
            .Select(operation => $"{ConnectorRouteKey.NormalizeMethod(operation.Method)} {ConnectorRouteKey.NormalizePath(operation.Path)} {operation.Classification} {ConnectorRouteKey.NormalizePath(operation.TargetRoute)}")
            .Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record InventoryDocument(
        [property: JsonPropertyName("frontendRoutes")] List<ConnectorRouteOperation>? FrontendRoutes);
}

public readonly record struct ConnectorRouteKey(string Method, string Path)
{
    public static ConnectorRouteKey FromInventory(string method, string path)
        => new(NormalizeMethod(method), NormalizePath(path));

    public static ConnectorRouteKey FromEndpoint(string method, string path)
        => new(NormalizeMethod(method), NormalizePath(path));

    public static string NormalizeMethod(string method)
        => method.ToUpperInvariant() switch
        {
            "SSE" => HttpMethods.Get,
            "WS" => "WS",
            var normalized => normalized,
        };

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var segments = path.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.StartsWith('{') && segment.EndsWith('}')
                ? NormalizeParameter(segment)
                : segment.ToLowerInvariant());
        return "/" + string.Join('/', segments);
    }

    public static string NormalizePhysicalPath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("{**", StringComparison.Ordinal))
            {
                segments.Add("{**}");
                break;
            }
            segments.Add(segment.StartsWith('{') ? "{}" : segment);
        }
        return "/" + string.Join('/', segments);
    }

    public static string ToAspNetPattern(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!segment.StartsWith('{') || !segment.EndsWith('}'))
            {
                segments.Add(segment);
                continue;
            }
            var value = segment[1..^1];
            if (value.EndsWith('*'))
            {
                segments.Add("{**" + value[..^1] + "}");
                break;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    private static string NormalizeParameter(string segment)
    {
        var value = segment[1..^1];
        var catchAll = value.StartsWith("**", StringComparison.Ordinal) || value.EndsWith('*');
        value = value.TrimStart('*').TrimEnd('*');
        var constraint = value.IndexOf(':');
        if (constraint >= 0) value = value[..constraint];
        return catchAll ? $"{{**{value}}}" : $"{{{value}}}";
    }
}
