using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Shared;

namespace AgentStudio.Security;

public sealed record HostOperationRequest(string IdempotencyKey, JsonElement Input);

public static class HostOperationEndpoints
{
    public static void MapHostOperationEndpoints(this WebApplication app)
    {
        Map(app, "inspect", OperationCatalogue.HostInspect);
        Map(app, "restart", OperationCatalogue.ServiceRestart);
        Map(app, "drain", OperationCatalogue.HostDrain);
        Map(app, "resume", OperationCatalogue.HostResume);
        Map(app, "logs", OperationCatalogue.HostLogsCollect);
        Map(app, "configuration", OperationCatalogue.HostConfigurationApply);
        app.MapGet("/api/hosts/{id}/operations/{attemptId}", async (string id, string attemptId,
            HttpContext context, IConfiguration config, IHttpClientFactory http, CancellationToken ct) =>
        {
            if (OperatorActorScope.Current is null) return Results.Json(new { error = "operator-session-required" }, statusCode: 403);
            var agentId = AgentId(config, id);
            if (!Guid.TryParseExact(attemptId, "N", out _) || agentId is null)
                return Results.BadRequest(new { error = "invalid-host-operation" });
            try
            {
                using var client = Client(config, http);
                using var response = await client.GetAsync("api/operations/v1/attempts/" + attemptId, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    var attempt = JsonSerializer.Deserialize<OperationAttempt>(body, OperationsProtocol.Json);
                    if (attempt?.AgentId != agentId || attempt.Command.Actor != OperatorActorScope.Current.Actor)
                        return Results.NotFound(new { error = "host-operation-not-found" });
                }
                return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or JsonException)
            { return Results.Json(new { error = "operations-server-unavailable" }, statusCode: 503); }
        }).RequirePermission(IamPermissions.WorkspaceRead);
    }

    private static void Map(WebApplication app, string route, OperationDefinition definition)
    {
        app.MapPost("/api/hosts/{id}/" + route, async (string id, HostOperationRequest request,
            HttpContext context, IConfiguration config, IHttpClientFactory http, TimeProvider clock,
            ControlPlaneAuditLog audit, CancellationToken ct) =>
        {
            if (OperatorActorScope.Current is null
                || context.Items[AccessSecurityMiddleware.OperatorPolicyItem] is not OperatorPolicyDecision { Admitted: true })
                return Results.Json(new { error = "operator-policy-required" }, statusCode: 403);
            var agentId = AgentId(config, id);
            if (agentId is null || request.IdempotencyKey is not { Length: >= 8 and <= 128 }
                || request.IdempotencyKey.Any(char.IsControl) || request.Input.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "invalid-host-operation" });
            var deadline = clock.GetUtcNow().AddSeconds(Math.Min(240, definition.TimeoutSeconds + 30));
            var command = new OperationCommand(request.IdempotencyKey, OperatorActorScope.Current.Actor, agentId,
                definition.Id, definition.Version, request.Input, OperationsProtocol.Digest(request.Input), deadline,
                OperationsProtocol.Digest(request.IdempotencyKey)[..32]);
            try
            {
                // The central chain is written before dispatch. If its writer
                // refuses the entry, no host operation is submitted.
                audit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator, command.Actor,
                    definition.Id, "dispatch-requested", id, ParameterDigest: command.InputDigest,
                    Details: new { command.IdempotencyKey, command.AgentId, command.CorrelationId }));
                using var client = Client(config, http);
                using var response = await client.PostAsJsonAsync("api/operations/v1/commands", command, OperationsProtocol.Json, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException)
            { return Results.Json(new { error = "operations-server-unavailable" }, statusCode: 503); }
        }).RequirePermission(definition == OperationCatalogue.HostInspect || definition == OperationCatalogue.HostLogsCollect
            ? IamPermissions.WorkspaceRead : IamPermissions.WorkspaceOperate);
    }

    private static string? AgentId(IConfiguration config, string hostId)
        => hostId is { Length: >= 1 and <= 128 }
            && hostId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? config["Operations:Agents:" + hostId] : null;

    private static HttpClient Client(IConfiguration config, IHttpClientFactory http)
    {
        var url = config["Operations:Url"];
        var tokenFile = config["Operations:ServiceTokenFile"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || string.IsNullOrWhiteSpace(tokenFile))
            throw new InvalidOperationException("Operations Server is not configured.");
        var client = http.CreateClient("operations-server");
        client.BaseAddress = new Uri(endpoint.ToString().TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(tokenFile).Trim());
        client.DefaultRequestHeaders.Add(OperationsProtocol.Header, OperationsProtocol.Version.ToString());
        return client;
    }
}
