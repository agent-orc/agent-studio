using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AgentStudio.TestSupport;

/// <summary>
/// Serves the bare repositories under one directory over Git's smart HTTP
/// protocol on a loopback port, by running <c>git http-backend</c> as a CGI
/// program per request. Harnesses use it where the product only accepts an
/// http(s) repository URL (the backend project registry does) and a
/// <c>url.insteadOf</c> rewrite would change what <c>git remote get-url</c>
/// reports, which the delivery preflight compares against the registration.
/// Fetch and push both work; every repository is exported and accepts pushes
/// without authentication. Loopback only, test fixtures only.
/// </summary>
public sealed class GitSmartHttpServer : IDisposable
{
    private readonly string _projectRoot;
    private readonly LoopbackHttpServer _listener;

    public GitSmartHttpServer(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _listener = new LoopbackHttpServer(ServeAsync);
        BaseUrl = _listener.BaseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>The clone URL of a bare repository directly under the project root.</summary>
    public string UrlFor(string repositoryDirectoryName) => $"{BaseUrl}/{repositoryDirectoryName}";

    private async Task ServeAsync(HttpContext context)
    {
        try
        {
            await RunBackendAsync(context);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            context.Response.StatusCode = 500;
        }
    }

    private async Task RunBackendAsync(HttpContext context)
    {
        var request = context.Request;
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("http-backend");
        start.Environment["GIT_PROJECT_ROOT"] = _projectRoot;
        start.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
        start.Environment["REQUEST_METHOD"] = request.Method;
        start.Environment["PATH_INFO"] = Uri.UnescapeDataString(request.Path.Value ?? string.Empty);
        start.Environment["QUERY_STRING"] = request.QueryString.Value?.TrimStart('?') ?? string.Empty;
        start.Environment["REMOTE_ADDR"] = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        // Push over smart HTTP otherwise requires an authenticated REMOTE_USER.
        start.Environment["REMOTE_USER"] = "scenario";
        if (request.ContentType is { Length: > 0 } contentType)
            start.Environment["CONTENT_TYPE"] = contentType;
        if (request.ContentLength is { } contentLength)
            start.Environment["CONTENT_LENGTH"] = contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (request.Headers["Content-Encoding"] is { Count: > 0 } encoding)
            start.Environment["HTTP_CONTENT_ENCODING"] = encoding.ToString();
        if (request.Headers["Git-Protocol"] is { Count: > 0 } protocol)
            start.Environment["GIT_PROTOCOL"] = protocol.ToString();

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git http-backend.");
        var stderr = process.StandardError.ReadToEndAsync();
        var body = Task.Run(async () =>
        {
            await request.Body.CopyToAsync(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        });

        var output = process.StandardOutput.BaseStream;
        var headers = await ReadCgiHeadersAsync(output);
        var response = context.Response;
        response.StatusCode = 200;
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Status", StringComparison.OrdinalIgnoreCase))
                response.StatusCode = int.Parse(value.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            else if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                response.ContentType = value;
            else if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                response.ContentLength = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else
                response.Headers[name] = value;
        }
        await output.CopyToAsync(response.Body);
        await body;
        await process.WaitForExitAsync();
        await stderr;
    }

    /// <summary>Reads CGI response headers up to the blank line, leaving the stream at the body.</summary>
    private static async Task<List<(string Name, string Value)>> ReadCgiHeadersAsync(Stream output)
    {
        var headers = new List<(string, string)>();
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (await output.ReadAsync(buffer) == 1)
        {
            var character = (char)buffer[0];
            if (character == '\r') continue;
            if (character != '\n')
            {
                line.Append(character);
                continue;
            }
            if (line.Length == 0) break;
            var text = line.ToString();
            var separator = text.IndexOf(':');
            if (separator > 0)
                headers.Add((text[..separator].Trim(), text[(separator + 1)..].Trim()));
            line.Clear();
        }
        return headers;
    }

    public void Dispose()
    {
        _listener.Dispose();
    }
}
