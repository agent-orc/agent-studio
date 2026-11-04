using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Access;
using AgentStudio.Operations.Server.Features.Dispatch;

namespace AgentStudio.Operations.Server;

public static class OperationsServerHost
{
    public static void AddOperationsServer(this IServiceCollection services, IConfiguration configuration)
    {
        var principals = configuration["Operations:PrincipalsFile"]
            ?? throw new InvalidOperationException("Operations:PrincipalsFile is required.");
        var directory = configuration["Operations:DataDirectory"]
            ?? throw new InvalidOperationException("Operations:DataDirectory is required.");
        // The kill switch's revocation list lives with the server's data, so the principal file can stay read-only.
        var revocations = configuration["Operations:RevocationsFile"] ?? Path.Combine(directory, OperationsAccess.RevocationsFileName);
        services.AddSingleton(new OperationsAccess(principals, revocations));
        services.AddSingleton(new OperationsStore(directory));
        services.AddSingleton(new HostRevocationStore(directory));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddHttpClient<IOperationPermitAuthority, OperationPermitAuthority>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(3);
            client.MaxResponseContentBufferSize = 16384;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddTransient<OperationsCoordinator>();
    }

    public static void MapOperationsServer(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/healthz") { await next(context); return; }
            var request = context.Request;
            // A browser request is rejected even when a stolen bearer is added.
            if (request.Headers.ContainsKey("Origin") || request.Headers.ContainsKey("Cookie")
                || request.Headers.ContainsKey("Sec-Fetch-Site") || request.Headers.ContainsKey("Forwarded")
                || request.Headers.Keys.Any(key => key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
            {
                await Deny(context, 401, "service-principal-required"); return;
            }
            if (!request.IsHttps && context.Connection.RemoteIpAddress is { } address
                && !System.Net.IPAddress.IsLoopback(address)
                && !app.Configuration.GetValue<bool>("Operations:AllowPrivateHttp"))
            {
                await Deny(context, 403, "tls-required"); return;
            }
            var authorization = request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 8192)
            {
                await Deny(context, 401, "authentication-required"); return;
            }
            OperationsPrincipal? principal;
            try { principal = context.RequestServices.GetRequiredService<OperationsAccess>().Authenticate(authorization[7..]); }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                await Deny(context, 503, "principal-store-unavailable"); return;
            }
            if (principal is null) { await Deny(context, 401, "authentication-required"); return; }
            if (request.Headers[OperationsProtocol.Header] != OperationsProtocol.Version.ToString())
            {
                await Deny(context, 426, "protocol-incompatible"); return;
            }
            context.Items[typeof(OperationsPrincipal)] = principal;
            try { await next(context); }
            catch (OperationRejected rejected) { await Deny(context, rejected.Status, rejected.Code); }
        });
        app.MapGet("/healthz", () => Results.Ok(new { status = "live", role = "operations-server" }));
        app.MapGet("/api/operations/v1/catalogue", (HttpContext context) =>
        {
            var denial = OperationsAccessPolicy.Denial(Principal(context), "service", "operations.read");
            return denial is null ? Results.Ok(OperationCatalogue.All) : Results.Json(new OperationError(denial), statusCode: 403);
        });
        app.MapPost("/" + OperatorPrincipalRevocation.Path, async (OperatorPrincipalRevocationRequest request, HttpContext context,
            OperationsAccess access, OperationsCoordinator operations) =>
        {
            var denial = OperationsAccessPolicy.RevocationDenial(Principal(context));
            if (denial is not null) return Results.Json(new OperationError(denial), statusCode: 403);
            if (request.OperatorAgentIds is null || request.OperatorAgentIds.Length > 1000
                || (!request.AllOperators && request.OperatorAgentIds.Length == 0)
                || request.OperatorAgentIds.Any(string.IsNullOrWhiteSpace))
                return Results.Json(new OperationError("invalid-revocation"), statusCode: 400);
            var revoked = access.RevokeOperators(request.OperatorAgentIds, request.AllOperators);
            var cancelled = await operations.CancelForPrincipalsAsync(revoked);
            return Results.Ok(new OperatorPrincipalRevocationResponse(revoked, cancelled));
        });
        app.MapPost("/api/operations/v1/agents/register", (AgentCapability capability, HttpContext context, OperationsCoordinator operations) =>
            operations.RegisterAsync(Principal(context), capability));
        app.MapPost("/api/operations/v1/agents/{agentId}/poll", (string agentId, AgentPollRequest request, HttpContext context, OperationsCoordinator operations, CancellationToken ct) =>
            operations.PollAsync(Principal(context), agentId, request.BootId, ct));
        app.MapPost("/api/operations/v1/agents/{agentId}/journal", (string agentId, HostJournalEntry entry,
            HttpContext context, OperationsStore store, ILogger<OperationsStore> logger) =>
        {
            var denial = OperationsAccessPolicy.Denial(Principal(context), "agent", "agents.connect", agentId);
            if (denial is not null) throw new OperationRejected(denial, 403);
            if (entry.AgentId != agentId || entry.Outcome is not ("execution-started" or "succeeded" or "failed" or "cancelled")
                || !Guid.TryParseExact(entry.AttemptId, "N", out _))
                throw new OperationRejected("invalid-host-journal", 400);
            try { store.ReceiveJournal(entry); }
            catch (OperationRejected rejected) when (rejected.Code == "host-journal-without-central-audit")
            {
                logger.LogError("host-journal-without-central-audit attempt={AttemptId} agent={AgentId}", entry.AttemptId, agentId);
                throw;
            }
            return Results.NoContent();
        });
        app.MapGet("/api/operations/v1/agents/{agentId}/revocations", (string agentId, HttpContext context,
            HostRevocationStore store) =>
        {
            var denial = OperationsAccessPolicy.Denial(Principal(context), "agent", "agents.connect", agentId);
            if (denial is not null) throw new OperationRejected(denial, 403);
            return store.Read() is { } list ? Results.Ok(list) : Results.NoContent();
        });
        app.MapPut("/api/operations/v1/certificates/revocations", (HostRevocationList list, HttpContext context,
            HostRevocationStore store) =>
        {
            var denial = OperationsAccessPolicy.Denial(Principal(context), "service", "certificates.revoke");
            if (denial is not null) throw new OperationRejected(denial, 403);
            store.Publish(list);
            return Results.NoContent();
        });
        app.MapPost("/api/operations/v1/commands", (OperationCommand command, HttpContext context, OperationsCoordinator operations, CancellationToken ct) =>
            operations.SubmitAsync(Principal(context), command, ct));
        app.MapGet("/api/operations/v1/attempts/{id}", (string id, HttpContext context, OperationsCoordinator operations) =>
            operations.ReadAsync(Principal(context), id));
        app.MapGet("/api/operations/v1/attempts/by-key/{key}", (string key, HttpContext context, OperationsCoordinator operations) =>
            operations.ReadByKeyAsync(Principal(context), key));
        app.MapPost("/api/operations/v1/attempts/{id}/cancel", (string id, HttpContext context, OperationsCoordinator operations) =>
            operations.ReadAsync(Principal(context), id, cancel: true));
        app.MapPost("/api/operations/v1/attempts/{id}/renew", (string id, AttemptLeaseRequest request, HttpContext context, OperationsCoordinator operations, CancellationToken ct) =>
            operations.RenewAsync(Principal(context), id, request, ct));
        app.MapPost("/api/operations/v1/attempts/{id}/result", (string id, AttemptReportRequest request, HttpContext context, OperationsCoordinator operations, CancellationToken ct) =>
            operations.ReportAsync(Principal(context), id, request, ct));
    }

    private static OperationsPrincipal Principal(HttpContext context) => (OperationsPrincipal)context.Items[typeof(OperationsPrincipal)]!;
    private static async Task Deny(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new OperationError(code));
    }
}
