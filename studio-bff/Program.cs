using System.Net.Http.Headers;
using AgentStudio.Bff;
using AgentStudio.TaskServer.Contracts;

if (args is ["--version"] or ["-V"])
{
    Console.WriteLine(StudioBffVersion.Display);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var taskServerUrl = builder.Configuration["TaskServer:BaseUrl"]
    ?? throw new InvalidOperationException("TaskServer:BaseUrl is required.");
var allowedOrigins = (builder.Configuration["Studio:AllowedOrigins"] ?? "http://127.0.0.1:4011,http://localhost:4011")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (allowedOrigins.Length == 0 || allowedOrigins.Any(origin =>
        !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        || !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.Ordinal)
        || (uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))))
    throw new InvalidOperationException("Studio:AllowedOrigins must contain exact HTTPS origins or local HTTP origins.");
builder.Services.AddHttpClient("task-server", client =>
{
    client.BaseAddress = new Uri(taskServerUrl);
    var bearerToken = ReadTaskServerToken(builder.Configuration);
    if (!string.IsNullOrWhiteSpace(bearerToken))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
    client.DefaultRequestHeaders.Add(TaskServerProtocol.ClientVersionHeaderName,
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown");
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var expectedFingerprint = builder.Configuration["TaskServer:TlsServerCertificateSha256"]?.Trim();
    if (string.IsNullOrWhiteSpace(expectedFingerprint))
        return new HttpClientHandler();
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            certificate is not null
            && (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == 0
            && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow
            && certificate.NotAfter.ToUniversalTime() >= DateTime.UtcNow
            && string.Equals(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.RawData)),
                expectedFingerprint,
                StringComparison.OrdinalIgnoreCase),
    };
});

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "live", role = "studio-bff" }));
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1")
        || context.Request.Path.StartsWithSegments("/hubs"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if ((!string.IsNullOrEmpty(origin) && !allowedOrigins.Contains(origin, StringComparer.Ordinal))
            || (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
                && string.IsNullOrEmpty(origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { code = "studio-origin-rejected" });
            return;
        }
    }
    await next(context);
});
RequestDelegate proxyToTaskServer = async context =>
{
    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("task-server");
    var target = context.Request.Path + context.Request.QueryString;
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        request.Content = new StreamContent(context.Request.Body);
        if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType))
            request.Content.Headers.ContentType = mediaType;
    }
    foreach (var header in new[] { "X-Actor-Id", "X-Client-Id", "Idempotency-Key", "If-Match" })
        if (context.Request.Headers.TryGetValue(header, out var value))
            request.Headers.TryAddWithoutValidation(header, value.ToArray());

    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;
    if (response.Content.Headers.ContentType is not null)
        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
};
app.Map("/api/v1/{**path}", proxyToTaskServer);
app.Map("/hubs/{**path}", proxyToTaskServer);

await app.RunAsync();

static string? ReadTaskServerToken(IConfiguration configuration)
{
    var legacy = configuration["TaskServer:BearerToken"]?.Trim();
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
    if (!string.IsNullOrWhiteSpace(direct)
        && !string.IsNullOrWhiteSpace(legacy))
    {
        throw new InvalidOperationException(
            "Configure TaskServer:AuthToken or the legacy TaskServer:BearerToken, not both.");
    }
    return string.IsNullOrWhiteSpace(direct) ? legacy : direct;
}

public partial class Program;
