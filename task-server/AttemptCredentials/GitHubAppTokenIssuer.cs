using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.AttemptCredentials;

public sealed record GitHubAppRoot(long AppId, long InstallationId, long RepositoryId, string RepositoryUrl, string PrivateKeyPem);
public sealed record IssuedRepositoryToken(string Value, DateTime ExpiresAtUtc, long RepositoryId);

public interface IRepositoryTokenIssuer
{
    Task<IssuedRepositoryToken> IssueReadAsync(string repositoryKey, string repositoryUrl, CancellationToken ct);
    Task<bool> RevokeAsync(string token, CancellationToken ct);
}

/// <summary>Requests a single-repository GitHub App installation token with contents:read.</summary>
public sealed class GitHubAppTokenIssuer(HttpClient http, IAttemptRootSecretStore roots) : IRepositoryTokenIssuer
{
    public async Task<IssuedRepositoryToken> IssueReadAsync(string repositoryKey, string repositoryUrl, CancellationToken ct)
    {
        var rootName = "github-app-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryKey))).ToLowerInvariant();
        var rootJson = await roots.ReadAsync(rootName, ct)
            ?? throw new InvalidOperationException("GitHub App root is not provisioned for this repository.");
        var root = JsonSerializer.Deserialize<GitHubAppRoot>(rootJson)
            ?? throw new InvalidDataException("GitHub App root is invalid.");
        if (root.RepositoryId <= 0 || root.AppId <= 0 || root.InstallationId <= 0
            || !string.Equals(root.RepositoryUrl, repositoryUrl, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub App root does not match the requested repository.");
        var repositoryId = root.RepositoryId;
        using var key = RSA.Create();
        key.ImportFromPem(root.PrivateKeyPem);
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-30).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = root.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        var signingInput = header + "." + body;
        var signature = Base64Url(key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.github.com/app/installations/{root.InstallationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signingInput + "." + signature);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("agent-studio-attempt-broker");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            repository_ids = new[] { repositoryId },
            permissions = new { contents = "read" },
        }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub App token issuance failed with HTTP {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var json = document.RootElement;
        var token = json.GetProperty("token").GetString();
        var expiry = json.GetProperty("expires_at").GetDateTime().ToUniversalTime();
        var permissions = json.GetProperty("permissions");
        if (string.IsNullOrWhiteSpace(token) || expiry <= DateTime.UtcNow
            || permissions.EnumerateObject().Any(p => p.Name is not ("contents" or "metadata") || p.Value.GetString() != "read")
            || !permissions.TryGetProperty("contents", out _))
            throw new InvalidDataException("GitHub App token scope or expiry did not match the read request.");
        if (json.TryGetProperty("repositories", out var repositories)
            && (repositories.GetArrayLength() != 1 || repositories[0].GetProperty("id").GetInt64() != repositoryId))
            throw new InvalidDataException("GitHub App token repository scope did not match the request.");
        return new IssuedRepositoryToken(token, expiry, repositoryId);
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://api.github.com/installation/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("agent-studio-attempt-broker");
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
