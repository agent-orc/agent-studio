using System.Net.Http.Headers;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Host;

/// <summary>
/// Transitional same-origin proxy for the versioned Task Server plane. When a
/// standalone base URL is configured, OrchestratorApi does not map any local
/// /api/v1 owner endpoints.
/// </summary>
public static class TaskServerPlaneProxy
{
    public const string ClientName = "standalone-task-server";

    private static readonly HashSet<string> HopByHopHeaders = new(
        [
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsConfigured(IConfiguration configuration)
        => TryGetBaseUri(configuration, out _);

    public static void AddTaskServerPlaneProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!TryGetBaseUri(configuration, out var baseUri))
        {
            services.AddHttpClient(ClientName);
            return;
        }

        services.AddHttpClient(ClientName, client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(100);
            client.DefaultRequestHeaders.Add(
                TaskServerProtocol.HeaderName,
                TaskServerProtocol.Current.ToString());
            client.DefaultRequestHeaders.Add(
                TaskServerProtocol.ClientVersionHeaderName,
                typeof(TaskServerPlaneProxy).Assembly.GetName().Version?.ToString(3)
                ?? "unknown");
            var consumerId = configuration["TaskServer:ConsumerId"]?.Trim();
            if (!string.IsNullOrWhiteSpace(consumerId))
                client.DefaultRequestHeaders.Add("X-Principal-Consumer-Id", consumerId);
            var token = ReadServiceToken(configuration);
            if (token is not null && string.IsNullOrWhiteSpace(configuration["TaskServer:AuthTokenFile"]))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
        }).AddHttpMessageHandler(() => new ReloadingProxyCredentialHandler(configuration));
    }

    public static bool MapTaskServerPlaneProxy(this WebApplication app)
    {
        if (!IsConfigured(app.Configuration)) return false;

        if (SecurityProfiles.IsPublicDemo(app.Configuration))
        {
            app.MapMethods(
                "/api/v1/{**path}",
                ["GET", "HEAD", "OPTIONS"],
                ForwardAsync)
                .WithMetadata(TaskServerPlaneProxyEndpoint.Instance);
            var unsafeProxy = app.MapMethods(
                "/api/v1/{**path}",
                ["POST", "PUT", "PATCH", "DELETE"],
                ForwardAsync)
                .WithMetadata(TaskServerPlaneProxyEndpoint.Instance);
            unsafeProxy.Add(endpoint =>
            {
                endpoint.Metadata.Add(new ExecutionRouteMetadata(ExecutionAdmissionPath.PostStep));
                endpoint.Metadata.Add(new PublicDemoExecutionExpectationMetadata(
                    ExecutionAdmissionPath.PostStep,
                    ExecutionAdmissionPolicy.ExecutionDisabledCode));
            });
            return true;
        }

        app.MapMethods(
            "/api/v1/{**path}",
            ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
            ForwardAsync)
            .WithMetadata(TaskServerPlaneProxyEndpoint.Instance);
        return true;
    }

    internal static bool TryGetBaseUri(
        IConfiguration configuration,
        out Uri? baseUri)
    {
        var configured = configuration["TaskServer:BaseUrl"]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            baseUri = null;
            return false;
        }
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "TaskServer:BaseUrl must be an absolute HTTP or HTTPS URL when set.");
        }
        baseUri = new Uri(parsed.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    private static async Task ForwardAsync(HttpContext context)
    {
        var client = context.RequestServices
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(ClientName);
        var target = context.Request.Path + context.Request.QueryString;
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            target);

        if (context.Request.ContentLength > 0
            || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var (name, values) in context.Request.Headers)
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/management/device-logins")
                && string.Equals(name, "X-Actor-Id", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ForwardsRequestHeader(name))
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(name, values.ToArray())
                && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        if (ActingRole(context, context.RequestServices.GetRequiredService<IConfiguration>()) is { } actingRole)
            request.Headers.TryAddWithoutValidation(CredentialViewProtocol.ActingRoleHeader, actingRole);
        if (context.Request.Path.StartsWithSegments("/api/v1/management/device-logins"))
        {
            var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? human.User.Id : DefaultClientIdentity.Id;
            request.Headers.TryAddWithoutValidation("X-Actor-Id", actor);
        }

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers)
                if (!HopByHopHeaders.Contains(name))
                    context.Response.Headers[name] = values.ToArray();
            foreach (var (name, values) in response.Content.Headers)
                if (!HopByHopHeaders.Contains(name)
                    && !string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[name] = values.ToArray();
            await response.Content.CopyToAsync(
                context.Response.Body,
                context.RequestAborted);
        }
        catch (HttpRequestException exception)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "task-server-unavailable",
                message = $"The standalone Task Server could not be reached: {exception.Message}",
            });
        }
    }

    internal static bool ForwardsRequestHeader(string name)
        => !HopByHopHeaders.Contains(name)
           && !string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(name, CredentialViewProtocol.ActingRoleHeader, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Studio role this edge authenticated, forwarded so the Task Server can scope
    /// credential metadata (I8). The networked profile uses the signed-in human; the local
    /// profile mirrors the loopback local-default operator of the local management routes.
    /// </summary>
    internal static string? ActingRole(HttpContext context, IConfiguration configuration)
    {
        if (SecurityProfiles.IsNetworked(configuration))
            return context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                   && StudioRoles.IsValid(human.User.Role)
                ? human.User.Role
                : null;
        if (!SecurityProfiles.IsLocal(configuration)) return null;
        var remote = context.Connection.RemoteIpAddress;
        var loopback = remote is null || System.Net.IPAddress.IsLoopback(remote);
        return loopback
               && string.Equals(context.Request.Headers["X-Client-Id"].FirstOrDefault(), DefaultClientIdentity.Id, StringComparison.Ordinal)
               && string.Equals(context.Items["ClientId"]?.ToString(), DefaultClientIdentity.Id, StringComparison.Ordinal)
            ? StudioRoles.Operator
            : null;
    }

    private static string? ReadServiceToken(IConfiguration configuration)
    {
        var direct = configuration["TaskServer:AuthToken"]?.Trim();
        var file = configuration["TaskServer:AuthTokenFile"]?.Trim();
        if (!string.IsNullOrWhiteSpace(direct) && !string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException(
                "Configure only one of TaskServer:AuthToken or TaskServer:AuthTokenFile.");
        if (!string.IsNullOrWhiteSpace(file))
        {
            var resolved = Path.GetFullPath(file);
            if (!File.Exists(resolved))
                throw new InvalidOperationException(
                    $"TaskServer:AuthTokenFile does not exist: {resolved}");
            direct = File.ReadAllText(resolved).Trim();
        }
        return string.IsNullOrWhiteSpace(direct) ? null : direct;
    }

    private sealed class ReloadingProxyCredentialHandler(IConfiguration configuration) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(configuration["TaskServer:AuthTokenFile"]))
            {
                var token = ReadServiceToken(configuration)
                    ?? throw new InvalidOperationException("The Task Server service credential is empty.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (PrincipalConsumerProofFile.ReadForBearerFile(configuration["TaskServer:AuthTokenFile"]!) is { } proof)
                {
                    request.Headers.Remove("X-Principal-Consumer-Id");
                    request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Id", proof.ConsumerId);
                    request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Proof", proof.Value);
                }
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// Endpoint metadata marking a route that forwards to the standalone Task
/// Server. The networked access middleware leaves bearer authentication to the
/// upstream only when routing selected an endpoint carrying this marker.
/// </summary>
public sealed class TaskServerPlaneProxyEndpoint
{
    public static readonly TaskServerPlaneProxyEndpoint Instance = new();

    private TaskServerPlaneProxyEndpoint() { }
}
