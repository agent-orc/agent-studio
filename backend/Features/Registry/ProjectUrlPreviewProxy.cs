using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Registry;

/// <summary>Read-only edge for a registered project URL. The caller cannot supply an upstream host.</summary>
public static class ProjectUrlPreviewProxy
{
    private const int RewriteLimitBytes = 2 * 1024 * 1024;
    private static readonly Regex BaseTag = new(@"<base\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RootAttribute = new(@"(?<head>\b(?:src|href|action)\s*=\s*['""])(?<path>/(?!/)[^'""]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RootModuleSpecifier = new(@"(?<head>\bfrom\s*['""]|\bimport\s*(?:\(\s*)?['""])(?<path>/(?!/)[^'""]*)", RegexOptions.Compiled);

    public static string RewriteHtml(string html, string prefix)
    {
        html = RootAttribute.Replace(html, match => match.Groups["head"].Value
            + prefix + match.Groups["path"].Value.TrimStart('/'));
        if (BaseTag.IsMatch(html))
            html = BaseTag.Replace(html, $"<base href=\"{prefix}\">");
        else
        {
            var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            if (head >= 0) html = html.Insert(head + 6, $"<base href=\"{prefix}\">");
        }
        return html;
    }

    public static string RewriteModuleImports(string script, string prefix)
        => RootModuleSpecifier.Replace(script, match => match.Groups["head"].Value
            + prefix + match.Groups["path"].Value.TrimStart('/'));

    public static Uri? Target(ProjectRecord? project, string urlId, string? path, string? query)
    {
        var registered = project?.Urls.FirstOrDefault(row => row.Id == urlId);
        if (registered is null || !Uri.TryCreate(registered.Url, UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https")) return null;
        var parts = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (parts.Any(part => part is "." or ".." || part.Contains('\\') || part.Contains(':'))) return null;
        var basePath = origin.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(origin)
        {
            Path = basePath + "/" + string.Join('/', parts),
            Query = !string.IsNullOrEmpty(query) ? query.TrimStart('?')
                : parts.Length == 0 ? origin.Query.TrimStart('?') : "",
        };
        return builder.Uri;
    }

    public static async Task<IResult> HandleAsync(
        string projId, string urlId, string? path, HttpContext context,
        ProjectRegistry projects, IHttpClientFactory clients, ProjectUrlPreviewAccess access)
    {
        var project = projects.FindById(projId);
        if (project is null) return Results.NotFound();
        var assetPath = path;
        var isAsset = path?.StartsWith("_asset/", StringComparison.Ordinal) == true;
        if (isAsset)
        {
            var segments = path!.Split('/', 3);
            if (segments.Length < 3 || !access.Allows(segments[1], projId, urlId))
                return Results.NotFound();
            assetPath = segments[2];
        }
        var target = Target(project, urlId, assetPath, context.Request.QueryString.Value);
        if (target is null) return Results.NotFound();
        // The response can also be opened as a top-level document. Keep its
        // origin opaque there, independently of the iframe's sandbox attribute.
        context.Response.Headers.ContentSecurityPolicy = "sandbox allow-scripts; form-action 'none'; frame-ancestors 'self'";
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("Accept", context.Request.Headers.Accept.ToString());
        try
        {
            using var response = await clients.CreateClient("project-url-preview")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                var resolved = location.IsAbsoluteUri ? location : new Uri(target, location);
                var registered = new Uri(project.Urls.First(row => row.Id == urlId).Url);
                if (resolved.Scheme != registered.Scheme || resolved.Authority != registered.Authority
                    || !resolved.AbsolutePath.StartsWith(registered.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal)
                    && resolved.AbsolutePath != registered.AbsolutePath)
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                var suffix = resolved.AbsolutePath[registered.AbsolutePath.TrimEnd('/').Length..].TrimStart('/');
                var route = isAsset
                    ? $"/api/projects/{Uri.EscapeDataString(projId)}/urls/{Uri.EscapeDataString(urlId)}/preview/_asset/{path!.Split('/', 3)[1]}/"
                    : $"/api/projects/{Uri.EscapeDataString(projId)}/urls/{Uri.EscapeDataString(urlId)}/preview/";
                return Results.Redirect($"{route}{suffix}{resolved.Query}", permanent: response.StatusCode == HttpStatusCode.MovedPermanently);
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // Only capability-bearing asset responses opt in to anonymous
            // opaque-origin module reads. Never opt Studio API responses in.
            if (isAsset && context.Request.Headers.Origin == "null")
            {
                context.Response.Headers.AccessControlAllowOrigin = "null";
                context.Response.Headers.Vary = "Origin";
            }
            var token = isAsset ? path!.Split('/', 3)[1] : access.Issue(projId, urlId);
            var prefix = $"/api/projects/{Uri.EscapeDataString(projId)}/urls/{Uri.EscapeDataString(urlId)}/preview/_asset/{token}/";
            var htmlContent = context.Response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
            var scriptContent = context.Response.ContentType.StartsWith("text/javascript", StringComparison.OrdinalIgnoreCase)
                || context.Response.ContentType.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase);
            if (htmlContent || scriptContent)
            {
                await using var upstream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                while (buffer.Length <= RewriteLimitBytes)
                {
                    var count = await upstream.ReadAsync(chunk, context.RequestAborted);
                    if (count == 0) break;
                    buffer.Write(chunk, 0, count);
                }
                if (buffer.Length <= RewriteLimitBytes)
                {
                    var source = Encoding.UTF8.GetString(buffer.ToArray());
                    await context.Response.WriteAsync(htmlContent
                        ? RewriteHtml(source, prefix)
                        : RewriteModuleImports(source, prefix), context.RequestAborted);
                }
                else
                {
                    buffer.Position = 0;
                    await buffer.CopyToAsync(context.Response.Body, context.RequestAborted);
                    await upstream.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
            }
            else await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            return Results.Empty;
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }
}
