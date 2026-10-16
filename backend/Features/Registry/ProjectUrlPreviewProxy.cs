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
        ProjectRegistry projects, IHttpClientFactory clients)
    {
        var project = projects.FindById(projId);
        if (project is null) return Results.NotFound();
        var target = Target(project, urlId, path, context.Request.QueryString.Value);
        if (target is null) return Results.NotFound();
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
                return Results.Redirect($"/api/projects/{Uri.EscapeDataString(projId)}/urls/{Uri.EscapeDataString(urlId)}/preview/{suffix}{resolved.Query}", permanent: response.StatusCode == HttpStatusCode.MovedPermanently);
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var prefix = $"/api/projects/{Uri.EscapeDataString(projId)}/urls/{Uri.EscapeDataString(urlId)}/preview/";
            if (context.Response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
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
                    var html = Encoding.UTF8.GetString(buffer.ToArray());
                    await context.Response.WriteAsync(RewriteHtml(html, prefix), context.RequestAborted);
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
