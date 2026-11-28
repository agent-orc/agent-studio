using System.Diagnostics;

namespace AgentStudio.Registry;

/// <summary>
/// Host-side readiness probe for registry-owned Project URLs. The browser
/// cannot observe status codes from an opaque cross-origin response, so this
/// service is the transport source of truth for preview health.
/// </summary>
public sealed class ProjectUrlReadinessService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(2500);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProjectUrlReadinessService> _logger;
    private readonly bool _networked;
    private readonly ProjectUrlProcessService? _processes;

    public ProjectUrlReadinessService(
        IHttpClientFactory httpClientFactory,
        ILogger<ProjectUrlReadinessService> logger,
        IConfiguration? configuration = null,
        ProjectUrlProcessService? processes = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _networked = configuration is not null && !SecurityProfiles.IsLocal(configuration);
        _processes = processes;
    }

    public async Task<ProjectUrlReadinessResult> ProbeAsync(
        ProjectRecord project,
        ProjectUrlRecord url,
        string? studioOrigin,
        CancellationToken cancellationToken, bool inspectBuildStamp = false)
    {
        var started = Stopwatch.GetTimestamp();
        if (!Uri.TryCreate(url.Url, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return Finish(project, url, "offline", null, "unknown", "Project URL must use HTTP or HTTPS.", started);
        }

        var ownsProcess = _processes?.Get(project.Id, url.Id) is { } process
            && ProjectUrlProcessStates.IsActive(process.State);
        if (ProjectUrlProbePolicy.Decide(url.Url, _networked, ownsProcess) == ProjectUrlProbeVerdict.LocalOnly)
            return Finish(project, url, ProjectUrlProbePolicy.LocalOnly, null, "unknown",
                ProjectUrlProbePolicy.LocalOnlyDetail, started);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Options.Set(ProjectUrlDestinationConnector.ProjectUrlOption, (project.Id, url.Id));
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
            using var response = await _httpClientFactory.CreateClient("project-url-readiness")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var statusCode = (int)response.StatusCode;
            var (framePolicy, policyReason) = EvaluateFramePolicy(response, target, studioOrigin);
            var kind = statusCode is >= 200 and < 300
                ? framePolicy == "blocked" ? "frame-blocked" : "healthy"
                : "http-error";
            var result = Finish(project, url, kind, statusCode, framePolicy, policyReason, started);
            if (!inspectBuildStamp || !response.IsSuccessStatusCode) return result;
            try
            {
                return result with { Commit = await BuildStamp(response, timeout.Token) };
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Build stamp unavailable for project URL {UrlId}", url.Id);
                return result;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Finish(project, url, "timeout", null, "unknown", "The readiness probe timed out.", started);
        }
        catch (HttpRequestException ex)
        {
            return Finish(project, url, "offline", null, "unknown", ex.Message, started);
        }
    }

    private static async Task<string?> BuildStamp(HttpResponseMessage response, CancellationToken token)
    {
        static string? Sha(string? value) => value is { Length: >= 7 and <= 40 } && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant() : null;
        if (Sha(HeaderValues(response, "X-Build-Commit").FirstOrDefault()) is { } header) return header;
        if (response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml")) return null;
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var prefix = new byte[32 * 1024];
        var count = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken: token);
        using var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(System.Text.Encoding.UTF8.GetString(prefix, 0, count));
        return Sha(document.Head?.QuerySelector("meta[name=build-commit],meta[name=git-commit]")?.GetAttribute("content"));
    }

    private ProjectUrlReadinessResult Finish(
        ProjectRecord project,
        ProjectUrlRecord url,
        string kind,
        int? statusCode,
        string framePolicy,
        string? detail,
        long started)
    {
        var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _logger.LogInformation(
            "project-url-readiness-probe project={ProjectId} url={UrlId} result={Result} statusCode={StatusCode} framePolicy={FramePolicy} durationMs={DurationMs:F1}",
            project.Id, url.Id, kind, statusCode, framePolicy, durationMs);
        return new ProjectUrlReadinessResult(kind, statusCode, framePolicy, detail, durationMs);
    }

    private static (string Policy, string? Reason) EvaluateFramePolicy(
        HttpResponseMessage response,
        Uri target,
        string? studioOrigin)
    {
        var xFrameOptions = HeaderValues(response, "X-Frame-Options").FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xFrameOptions))
        {
            var value = xFrameOptions.Trim();
            if (value.Equals("DENY", StringComparison.OrdinalIgnoreCase))
                return ("blocked", "X-Frame-Options is DENY.");
            if (value.Equals("SAMEORIGIN", StringComparison.OrdinalIgnoreCase)
                && !OriginsMatch(target, studioOrigin))
                return ("blocked", "X-Frame-Options is SAMEORIGIN for a cross-origin preview.");
            if (value.StartsWith("ALLOW-FROM", StringComparison.OrdinalIgnoreCase))
                return ("blocked", "X-Frame-Options uses a restrictive ALLOW-FROM policy.");
        }

        foreach (var policy in HeaderValues(response, "Content-Security-Policy"))
        {
            var directive = policy.Split(';', StringSplitOptions.TrimEntries)
                .FirstOrDefault(part => part.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase));
            if (directive == null) continue;
            var sources = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Skip(1)
                .ToArray();
            if (sources.Length == 0 || sources.Any(source => source.Equals("'none'", StringComparison.OrdinalIgnoreCase)))
                return ("blocked", "Content-Security-Policy frame-ancestors blocks embedding.");
            if (!FrameAncestorsAllows(sources, studioOrigin))
                return ("blocked", "Content-Security-Policy frame-ancestors does not allow the Studio origin.");
        }

        return ("allowed", null);
    }

    private static bool FrameAncestorsAllows(IEnumerable<string> sources, string? studioOrigin)
    {
        if (sources.Any(source => source == "*")) return true;
        if (!Uri.TryCreate(studioOrigin, UriKind.Absolute, out var studio)) return false;
        return sources.Any(source =>
            source.Equals(studio.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || source.Equals(studio.Scheme + ":", StringComparison.OrdinalIgnoreCase));
    }

    private static bool OriginsMatch(Uri target, string? studioOrigin) =>
        Uri.TryCreate(studioOrigin, UriKind.Absolute, out var studio)
        && target.Scheme.Equals(studio.Scheme, StringComparison.OrdinalIgnoreCase)
        && target.Host.Equals(studio.Host, StringComparison.OrdinalIgnoreCase)
        && target.Port == studio.Port;

    private static IEnumerable<string> HeaderValues(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)) return values;
        return response.Content.Headers.TryGetValues(name, out values) ? values : [];
    }
}

public sealed record ProjectUrlReadinessResult(
    string Kind,
    int? StatusCode,
    string FramePolicy,
    string? Detail,
    double DurationMs,
    string? Commit = null);
