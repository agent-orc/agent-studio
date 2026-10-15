
namespace AgentStudio.Clients;

/// <summary>
/// Registration boundary for the API. Reads <c>X-Client-Id</c> on every
/// inbound request, looks it up against <see cref="ClientIdentityStore"/>,
/// and:
///
/// - rejects mutations from unknown ids with <c>401 client-unknown</c>;
/// - stamps <c>lastSeenAt</c> on the identity for every authenticated read or write;
/// - leaves reads open for now (the user framing: "reads can stay open for now,
///   but every read records the requesting clientId in an access log").
///
/// Carve-outs: the <c>/api/clients/register</c> endpoint and
/// <c>GET /api/clients</c> intentionally accept anonymous traffic so the
/// first caller can register an identity before signing in. Health checks,
/// SignalR negotiation, and dev/diagnostic endpoints stay open.
///
/// This is not a security model. It is a registration sign-in. The follow-up
/// task will introduce signed tokens once the multi-instance protocol stabilises.
/// </summary>
public class ClientIdentityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ClientIdentityStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientIdentityMiddleware> _logger;

    private static readonly string[] OpenPathPrefixes =
    {
        "/api/clients/register",
        "/hubs/",
        "/api/health",
        "/healthz",
        "/swagger",
        "/_framework",
        "/_vs"
    };

    public ClientIdentityMiddleware(
        RequestDelegate next,
        ClientIdentityStore store,
        IConfiguration configuration,
        ILogger<ClientIdentityMiddleware> logger)
    {
        _next = next;
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    internal ClientIdentityMiddleware(
        RequestDelegate next,
        ClientIdentityStore store,
        ILogger<ClientIdentityMiddleware> logger)
        : this(next, store, new ConfigurationBuilder().Build(), logger)
    {
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        // The proxied v1 plane owns its writes, but the local Studio role still
        // needs the registered default-client attribution on the request.
        var fencedAttribution = OwnsFencedAttribution(path);
        if (fencedAttribution && SecurityProfiles.IsLocal(_configuration)
            && path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.Request.Headers["X-Client-Id"].FirstOrDefault(),
                DefaultClientIdentity.Id, StringComparison.Ordinal)
            && _store.IsRegistered(DefaultClientIdentity.Id))
            context.Items["ClientId"] = DefaultClientIdentity.Id;

        // Skip the boundary for non-/api traffic, registration, hubs, health.
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || OpenPathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || fencedAttribution)
        {
            await _next(context);
            return;
        }

        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        var isWrite = !HttpMethods.IsGet(method)
                      && !HttpMethods.IsHead(method)
                      && !HttpMethods.IsOptions(method);

        if (string.IsNullOrWhiteSpace(clientId))
        {
            if (isWrite)
            {
                await Reject(context, "client-unknown", "X-Client-Id header is required for mutations");
                return;
            }
            // Anonymous reads stay open. Record the access for visibility.
            _logger.LogDebug("Anonymous read {Method} {Path}", method, path);
            await _next(context);
            return;
        }

        if (!_store.IsRegistered(clientId))
        {
            var diagnostic = _store.FindDiagnostic(clientId);
            if (isWrite)
            {
                await Reject(
                    context,
                    diagnostic is null ? "client-unknown" : "identity-file-corrupt",
                    diagnostic?.Message ?? $"X-Client-Id '{clientId}' is not registered",
                    diagnostic?.RestoreHint);
                return;
            }
            if (diagnostic is null)
                _logger.LogWarning("Unknown clientId '{ClientId}' on read {Method} {Path}", clientId, method, path);
            await _next(context);
            return;
        }

        // Known identity: stamp lastSeenAt so the GET listing has fresh data.
        _store.RecordSeen(clientId);
        context.Items["ClientId"] = clientId;
        if (!isWrite)
        {
            await _next(context);
            return;
        }

        // AGT-3088: the task index attributes this request's mutations to the
        // client, so only that client's board reads wait for them.
        using (AmbientClient.Enter(clientId))
            await _next(context);
    }

    private bool OwnsFencedAttribution(string path)
    {
        // The public-demo replay ingest carries its own proof: a pre-signed
        // frame, a pinned trace digest, a monotonic epoch, and, in the networked
        // profile, an exclusive credential scope. A Studio client identity would
        // add nothing and cannot be registered from the replay image anyway.
        if (path.StartsWith("/api/runner/replay", StringComparison.OrdinalIgnoreCase))
            return true;

        if (TaskServerPlaneProxy.IsConfigured(_configuration))
            return path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase);

        // AGT-2325's interim Runner and Review adapters own their fenced
        // attribution. Legacy monolith management remains behind this
        // middleware until the standalone proxy replaces the whole v1 plane.
        return path.StartsWith("/api/v1/protocol", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/api/v1/orchestration", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/api/v1/runners", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/api/v1/runs", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/api/v1/reviews", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/api/v1/gates", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Reject(HttpContext context, string code, string message, string? hint = null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = code,
            message,
            hint = hint ?? "Register an identity at POST /api/clients/register, then send the returned id as X-Client-Id."
        });
    }
}

public static class ClientIdentityMiddlewareExtensions
{
    public static IApplicationBuilder UseClientIdentity(this IApplicationBuilder app)
        => app.UseMiddleware<ClientIdentityMiddleware>();
}
