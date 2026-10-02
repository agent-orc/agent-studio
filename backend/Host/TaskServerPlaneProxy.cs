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
            var token = ReadServiceToken(configuration);
            if (token is not null)
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
        });
    }

    public static bool MapTaskServerPlaneProxy(this WebApplication app)
    {
        if (!IsConfigured(app.Configuration)) return false;

        if (SecurityProfiles.IsPublicDemo(app.Configuration))
        {
            app.MapMethods(
                "/api/v1/{**path}",
                ["GET", "HEAD", "OPTIONS"],
                ForwardAsync);
            var unsafeProxy = app.MapMethods(
                "/api/v1/{**path}",
                ["POST", "PUT", "PATCH", "DELETE"],
                ForwardAsync);
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
            ForwardAsync);
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
            if (HopByHopHeaders.Contains(name)
                || string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(name, values.ToArray())
                && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
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
}
