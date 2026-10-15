using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;

namespace AgentStudio.Setup;

// The native installation serves the same route split as deploy/compose/Caddyfile.
// This executable is shipped in the verified Studio archive and run by systemd.
internal static class NativeWebHost
{
    internal static string? UpstreamFor(string path, string api, string bff)
        => path == "/api/v1" || path.StartsWith("/api/v1/", StringComparison.Ordinal)
           || path == "/hubs" || path.StartsWith("/hubs/", StringComparison.Ordinal)
            ? bff
            : path == "/api" || path.StartsWith("/api/", StringComparison.Ordinal)
              || path is "/healthz" or "/readyz"
                ? api
                : null;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8 || args[0] != "--browser" || args[2] != "--listen"
            || args[4] != "--api" || args[6] != "--bff")
            throw new ArgumentException(
                "serve-native requires --browser PATH --listen URL --api URL --bff URL.");
        var browser = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(browser, "index.html")))
            throw new FileNotFoundException("Studio browser bundle is missing index.html.");
        var listen = RequireLoopback(args[3]);
        var api = RequireLoopback(args[5]);
        var bff = RequireLoopback(args[7]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(listen);
        var app = builder.Build();
        app.UseWebSockets();
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        });
        var contentTypes = new FileExtensionContentTypeProvider();
        app.Run(async context =>
        {
            var path = context.Request.Path.Value ?? "/";
            var upstream = UpstreamFor(path, api, bff);
            if (upstream is null)
            {
                var relative = path.TrimStart('/');
                var file = Path.GetFullPath(Path.Combine(browser, relative));
                if (!file.StartsWith(browser + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !File.Exists(file))
                    file = Path.Combine(browser, "index.html");
                var provider = new PhysicalFileProvider(browser);
                var info = provider.GetFileInfo(Path.GetRelativePath(browser, file));
                context.Response.ContentType = contentTypes.TryGetContentType(file, out var contentType)
                    ? contentType : "application/octet-stream";
                context.Response.ContentLength = info.Length;
                await context.Response.SendFileAsync(info);
                provider.Dispose();
                return;
            }
            if (context.WebSockets.IsWebSocketRequest)
            {
                var target = new UriBuilder(upstream)
                {
                    Scheme = upstream.StartsWith("https:", StringComparison.Ordinal) ? "wss" : "ws",
                    Path = path,
                    Query = context.Request.QueryString.Value?.TrimStart('?') ?? string.Empty,
                }.Uri;
                using var client = new ClientWebSocket();
                foreach (var header in context.Request.Headers)
                    if (header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                        || header.Key is "Cookie" or "Origin")
                        client.Options.SetRequestHeader(header.Key, header.Value.ToString());
                await client.ConnectAsync(target, context.RequestAborted);
                using var browserSocket = await context.WebSockets.AcceptWebSocketAsync(client.SubProtocol);
                await Task.WhenAny(
                    CopySocketAsync(browserSocket, client, context.RequestAborted),
                    CopySocketAsync(client, browserSocket, context.RequestAborted));
                return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                upstream.TrimEnd('/') + path + context.Request.QueryString);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.RunAsync();
        return 0;
    }

    private static string RequireLoopback(string value)
    {
        var uri = SetupValidation.RequireServerUrl(value, allowLoopbackHttp: true);
        if (!uri.IsLoopback || uri.Scheme != "http")
            throw new ArgumentException("The native Studio web listener and upstreams must use loopback HTTP.");
        return uri.ToString().TrimEnd('/');
    }

    private static async Task CopySocketAsync(WebSocket source, WebSocket destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,
                    cancellationToken);
                return;
            }
            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType,
                result.EndOfMessage, cancellationToken);
        }
    }
}
