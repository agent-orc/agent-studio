using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

public sealed record IncidentWebhookView(bool Configured, string Destination);

public sealed record IncidentWebhookRequest(string? Url, string? StudioBaseUrl);

public sealed record IncidentWebhookPayload(string Event, string Project, string Environment,
    string EnvironmentName, string Signal, DateTimeOffset StartedAt, string Link);

/// <summary>
/// Project-owned out-of-band channel. The URL may contain a receiver token, so reads and
/// logs expose only its host. Delivery receipts live on the environment incident.
/// </summary>
public sealed class IncidentWebhookService(IConfiguration configuration, IHttpClientFactory clients,
    ILogger<IncidentWebhookService> logger)
{
    public const string HttpClientName = "incident-webhooks";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private Dictionary<string, Endpoint>? _endpoints;

    private sealed record Endpoint(string Url, string StudioBaseUrl);

    public IncidentWebhookView Get(string project)
    {
        lock (_gate)
        {
            var endpoint = Load().GetValueOrDefault(project);
            return endpoint is null ? new(false, "") : new(true, new Uri(endpoint.Url).Host);
        }
    }

    public IncidentWebhookView Set(string project, string? url, string? studioBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Project is required.", nameof(project));
        Endpoint? endpoint = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            var destination = Validate(url, allowLocalHttp: false);
            var baseUrl = Validate(studioBaseUrl, allowLocalHttp: true);
            if (baseUrl.AbsolutePath != "/" || baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
                throw new ArgumentException("Studio link must be an origin.", nameof(studioBaseUrl));
            endpoint = new(destination.ToString(), baseUrl.GetLeftPart(UriPartial.Authority));
        }
        lock (_gate)
        {
            var map = Load();
            if (endpoint is null) map.Remove(project); else map[project] = endpoint;
            Persist(map);
            return endpoint is null ? new(false, "") : new(true, new Uri(endpoint.Url).Host);
        }
    }

    public async Task<bool> DeliverAsync(string project, string change, string environmentId,
        string environmentName, string signal, DateTimeOffset startedAt, string? cardId,
        CancellationToken cancellationToken = default)
    {
        Endpoint? endpoint;
        lock (_gate) endpoint = Load().GetValueOrDefault(project);
        if (endpoint is null) return false;
        var slug = Regex.Replace(project.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var link = endpoint.StudioBaseUrl + "/#/projects/" + slug + "/operations";
        var payload = new IncidentWebhookPayload(change, project, environmentId, environmentName, signal,
            startedAt, link);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.ConnectionClose = true;
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, deadline.Token);
            if (response.IsSuccessStatusCode) return true;
            logger.LogWarning("incident webhook returned status {Status} project={Project} event={Event}",
                (int)response.StatusCode, project, change);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("incident webhook delivery failed project={Project} event={Event}: {Error}",
                project, change, ex.GetType().Name);
        }
        return false;
    }

    private Dictionary<string, Endpoint> Load()
    {
        if (_endpoints is not null) return _endpoints;
        var path = Path();
        _endpoints = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, Endpoint>>(File.ReadAllText(path), Json)
                ?? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
        _endpoints = new(_endpoints, StringComparer.OrdinalIgnoreCase);
        return _endpoints;
    }

    private void Persist(Dictionary<string, Endpoint> map)
    {
        var path = Path();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(map, Json));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }

    private string Path() => System.IO.Path.Combine(configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required."), "incident-webhooks.json");

    private static Uri Validate(string? value, bool allowLocalHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(allowLocalHttp && uri.Scheme == Uri.UriSchemeHttp
                && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("A valid HTTPS URL without user info or fragment is required.");
        return uri;
    }
}
