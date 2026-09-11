using System.Buffers;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Connector;

public sealed record ConnectorClassifiedEndpointMetadata(string Method, string Path, string Classification);
public sealed record ConnectorControlEndpointMetadata;

public sealed class ConnectorProxy(
    ConnectorUpstreamManager upstream,
    IConnectorUpstreamTransport transport)
{
    private static readonly HashSet<string> HopByHopHeaders = new(
        [
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ForwardedRequestHeaders = new(
        ["Accept", "Accept-Language", "Content-Language", "Content-Type", "Idempotency-Key", "If-Match", "If-None-Match", "Range", "X-Client-Id"],
        StringComparer.OrdinalIgnoreCase);

    public async Task ForwardHttpAsync(HttpContext context, ConnectorRouteOperation operation)
    {
        var snapshot = upstream.Capture();
        if (snapshot.Credential is null)
        {
            await UnavailableAsync(context, "connector-credential-unavailable");
            return;
        }

        var targetPath = ExpandTarget(operation, context.Request.RouteValues, context.Request.Query);
        var target = new Uri(snapshot.BaseUri, targetPath.TrimStart('/') + context.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);
        CopyRequestHeaders(context.Request, request);
        ConnectorUpstreamTransport.AddConnectorHeaders(request.Headers, snapshot);

        try
        {
            using var response = await transport.SendAsync(snapshot, request, context.RequestAborted);
            await CopyResponseAsync(context, response);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or OperationCanceledException
                                          or InvalidOperationException)
        {
            if (!context.Response.HasStarted)
                await UnavailableAsync(context, "task-server-unavailable");
        }
    }

    public Task ForwardHubAsync(HttpContext context, ConnectorRouteOperation operation)
        => context.WebSockets.IsWebSocketRequest
            ? ForwardWebSocketAsync(context, operation)
            : ForwardHubHttpAsync(context, operation);

    /// <summary>
    /// Reserved <c>{projectId}</c> segment value for a legacy frontend call
    /// that names only a task, such as <c>/api/tasks/{taskId}/move</c>. The
    /// target route in the approved inventory is project-scoped, but the
    /// connector is a mechanical path translator with no task-to-project
    /// lookup of its own, so it cannot fabricate a real project id. The Task
    /// Server treats this exact literal as "resolve by task id alone" (see
    /// <c>TaskServerStore.UnscopedProjectToken</c>) rather than as a project
    /// that does not exist.
    /// </summary>
    internal const string UnscopedProjectToken = "-";

    internal static string ExpandTarget(
        ConnectorRouteOperation operation,
        RouteValueDictionary routeValues,
        IQueryCollection? query = null)
    {
        var sourceSegments = operation.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var targetSegments = operation.TargetRoute.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sourceParameterByPosition = sourceSegments
            .Select((segment, index) => (segment, index))
            .Where(item => item.segment.StartsWith('{') && item.segment.EndsWith('}'))
            .ToDictionary(item => item.index, item => ParameterName(item.segment));

        for (var index = 0; index < targetSegments.Length; index++)
        {
            var segment = targetSegments[index];
            if (!segment.StartsWith('{') || !segment.EndsWith('}')) continue;
            var targetName = ParameterName(segment);
            object? value = null;
            if (!routeValues.TryGetValue(targetName, out value)
                && sourceParameterByPosition.TryGetValue(index, out var sourceName))
                routeValues.TryGetValue(sourceName, out value);
            if (value is null && query is not null)
            {
                if (query.TryGetValue(targetName, out var queryValue))
                    value = queryValue.FirstOrDefault();
                else if (targetName == "projectId" && query.TryGetValue("project", out queryValue))
                    value = queryValue.FirstOrDefault();
            }
            if (value is null && targetName == "projectId")
                value = UnscopedProjectToken;
            if (value is null)
                throw new InvalidOperationException($"Route value '{targetName}' is unavailable.");
            var encoded = string.Join('/', value.ToString()!
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
            targetSegments[index] = encoded;
        }
        return "/" + string.Join('/', targetSegments);
    }

    private async Task ForwardHubHttpAsync(HttpContext context, ConnectorRouteOperation operation)
    {
        var suffix = context.Request.RouteValues.TryGetValue("path", out var raw) ? raw?.ToString() : null;
        var hubOperation = operation with
        {
            TargetRoute = operation.TargetRoute.TrimEnd('/')
                + (string.IsNullOrWhiteSpace(suffix) ? string.Empty : "/" + suffix),
        };
        await ForwardHttpAsync(context, hubOperation);
    }

    private async Task ForwardWebSocketAsync(HttpContext context, ConnectorRouteOperation operation)
    {
        var snapshot = upstream.Capture();
        if (snapshot.Credential is null)
        {
            await UnavailableAsync(context, "connector-credential-unavailable");
            return;
        }

        var suffix = context.Request.RouteValues.TryGetValue("path", out var raw) ? raw?.ToString() : null;
        var upstreamPath = operation.TargetRoute.TrimEnd('/')
            + (string.IsNullOrWhiteSpace(suffix) ? string.Empty : "/" + suffix)
            + context.Request.QueryString;
        var uriBuilder = new UriBuilder(new Uri(snapshot.BaseUri, upstreamPath.TrimStart('/')))
        {
            Scheme = snapshot.BaseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        var protocols = context.Request.Headers.SecWebSocketProtocol
            .SelectMany(value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();

        try
        {
            using var upstreamSocket = await transport.ConnectWebSocketAsync(
                snapshot,
                uriBuilder.Uri,
                protocols,
                clientId,
                context.RequestAborted);
            using var browserSocket = await context.WebSockets.AcceptWebSocketAsync(
                new WebSocketAcceptContext { SubProtocol = upstreamSocket.SubProtocol });
            using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var outbound = PumpAsync(browserSocket, upstreamSocket, pumpCancellation.Token);
            var inbound = PumpAsync(upstreamSocket, browserSocket, pumpCancellation.Token);
            await Task.WhenAny(outbound, inbound);
            pumpCancellation.Cancel();
            await Task.WhenAll(IgnoreCancellation(outbound), IgnoreCancellation(inbound));
        }
        catch (Exception exception) when (exception is WebSocketException
                                          or HttpRequestException
                                          or OperationCanceledException
                                          or InvalidOperationException)
        {
            if (!context.Response.HasStarted)
                await UnavailableAsync(context, "task-server-hub-unavailable");
        }
    }

    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && source.State is WebSocketState.Open or WebSocketState.CloseReceived
                   && destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var message = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    if (destination.State == WebSocketState.Open)
                        await destination.CloseOutputAsync(
                            message.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            message.CloseStatusDescription,
                            cancellationToken);
                    return;
                }
                await destination.SendAsync(
                    buffer.AsMemory(0, message.Count),
                    message.MessageType,
                    message.EndOfMessage,
                    cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException exception)
        {
            SilentCatch.Note(exception, "Connector WebSocket pump cancellation is expected during paired shutdown.");
        }
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (var (name, values) in source.Headers)
        {
            if (!ForwardedRequestHeaders.Contains(name)
                || HopByHopHeaders.Contains(name)
                || string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Forwarded", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, TaskServerProtocol.HeaderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, TaskServerProtocol.ClientVersionHeaderName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!destination.Headers.TryAddWithoutValidation(name, values.ToArray()) && destination.Content is not null)
                destination.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }

    private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers)
        {
            if (!ShouldStripResponseHeader(name)) context.Response.Headers[name] = values.ToArray();
        }
        foreach (var (name, values) in response.Content.Headers)
        {
            if (!ShouldStripResponseHeader(name)) context.Response.Headers[name] = values.ToArray();
        }
        context.Response.Headers.Remove("Content-Length");
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool ShouldStripResponseHeader(string name)
        => HopByHopHeaders.Contains(name)
           || string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "WWW-Authenticate", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase);

    private static async Task UnavailableAsync(HttpContext context, string code)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { code });
    }

    private static string ParameterName(string segment)
    {
        var value = segment[1..^1].TrimStart('*');
        if (value.EndsWith('*')) value = value[..^1];
        var constraint = value.IndexOf(':');
        return constraint < 0 ? value : value[..constraint];
    }
}
