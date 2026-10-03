using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentRunner;

/// <summary>Issuance data is supplied by the protected source, not guessed from token bytes.</summary>
public interface IGitHubCredentialInspectorSession
{
    Task<(string Bearer, string? IssuerSubtype, DateTimeOffset? IssuerExpiresAt)?>
        OpenAsync(string credentialId, CancellationToken ct);
}

/// <summary>Discovers one exact repository grant without exporting a provisioning bearer.</summary>
public sealed class GitHubCredentialDiscovery : IDisposable
{
    private readonly IGitHubCredentialInspectorSession _session;
    private readonly HttpClient _githubApi;

    public GitHubCredentialDiscovery(IGitHubCredentialInspectorSession session,
        HttpMessageHandler? handler = null)
    {
        _session = session;
        _githubApi = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) {
            BaseAddress = new Uri("https://api.github.com/")
        };
    }

    public void Dispose() => _githubApi.Dispose();

    public async Task<GitHubHttpsCredentialMetadata> DiscoverAsync(
        string credentialId, string purpose, string origin, bool requiresPush,
        CancellationToken ct)
    {
        RepositoryAccessRenewal.ValidateBinding(new(purpose, origin, requiresPush));
        if (!origin.StartsWith("https://github.com/", StringComparison.Ordinal)
            || _githubApi.BaseAddress?.AbsoluteUri != "https://api.github.com/")
            throw new ArgumentException("Grant discovery requires the exact GitHub HTTPS origin and API endpoint.");
        var uri = new Uri(origin);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var repo = segments[1].EndsWith(".git", StringComparison.Ordinal)
            ? segments[1][..^4] : segments[1];
        var opened = await _session.OpenAsync(credentialId, ct);
        if (opened is null)
            return new(credentialId, "unknown", purpose, origin, requiresPush,
                null, null, null, "unknown");
        var subtype = opened.Value.IssuerSubtype is
            "fine-grained-pat" or "classic-pat" or "oauth-app"
                ? opened.Value.IssuerSubtype : "unknown";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{segments[0]}/{repo}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opened.Value.Bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _githubApi.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.RedirectKeepVerb
            or System.Net.HttpStatusCode.RedirectMethod)
            throw new InvalidOperationException("GitHub grant discovery refused an API redirect.");
        bool? fetch = null;
        bool? push = null;
        if (response.IsSuccessStatusCode)
        {
            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (body.RootElement.TryGetProperty("permissions", out var permissions))
            {
                fetch = permissions.TryGetProperty("pull", out var pull) && pull.GetBoolean();
                push = permissions.TryGetProperty("push", out var write) && write.GetBoolean();
            }
        }
        return new(credentialId, subtype, purpose, origin, requiresPush,
            fetch, push, opened.Value.IssuerExpiresAt,
            opened.Value.IssuerExpiresAt is null ? "unknown" : "issuer");
    }
}
