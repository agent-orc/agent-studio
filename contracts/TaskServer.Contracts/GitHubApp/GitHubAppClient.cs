using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts.GitHubApp;

public sealed record GitHubAppManifestConversion(
    long AppId,
    string Slug,
    string Name,
    string ClientId,
    string OwnerLogin,
    string HtmlUrl,
    IReadOnlyList<string> WritePermissions,
    GitHubAppSecrets Secrets);

public sealed record GitHubAppInfo(
    long AppId,
    string Slug,
    string Name,
    string ClientId,
    string OwnerLogin,
    string HtmlUrl,
    IReadOnlyList<string> WritePermissions);

public sealed record GitHubUserInstallation(long Id, long AppId, string Account, string AccountType);

public sealed record GitHubInstallationToken(string Value, DateTime ExpiresAtUtc)
{
    public override string ToString() => $"GitHubInstallationToken {{ expires {ExpiresAtUtc:O} }}";
}

/// <summary>
/// The GitHub calls of the App flow. Error messages carry the status and
/// GitHub's own short message, never a request or response secret.
/// </summary>
public sealed class GitHubAppClient(HttpClient http, TimeProvider clock)
{
    private const string Api = "https://api.github.com";
    private const string Web = "https://github.com";
    private const string UserAgent = "agent-studio";

    /// <summary>A signed App identity, valid for nine minutes, for the routes that require a JWT.</summary>
    public string CreateJwt(long appId, string privateKeyPem)
    {
        using var key = RSA.Create();
        try { key.ImportFromPem(privateKeyPem); }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new GitHubAppException("invalid-private-key", "The GitHub App private key is not a readable PEM key.");
        }
        var now = clock.GetUtcNow();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            // Backdated for clock drift, as GitHub recommends.
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = appId.ToString(CultureInfo.InvariantCulture),
        }));
        var input = header + "." + body;
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return input + "." + Base64Url(signature);
    }

    /// <summary>Exchanges the one-hour code of the manifest flow for the new App and its keys.</summary>
    public async Task<GitHubAppManifestConversion> ConvertManifestAsync(string code, CancellationToken ct)
    {
        using var request = ApiRequest(HttpMethod.Post, $"/app-manifests/{Uri.EscapeDataString(code)}/conversions", null);
        using var document = await SendAsync(request, "register the App", ct);
        var json = document.RootElement;
        var pem = RequiredString(json, "pem");
        var clientSecret = RequiredString(json, "client_secret");
        return new GitHubAppManifestConversion(
            json.GetProperty("id").GetInt64(),
            RequiredString(json, "slug"),
            RequiredString(json, "name"),
            RequiredString(json, "client_id"),
            json.GetProperty("owner").GetProperty("login").GetString() ?? string.Empty,
            RequiredString(json, "html_url"),
            WritePermissions(json),
            new GitHubAppSecrets(pem, clientSecret));
    }

    public async Task<GitHubAppInfo> GetAppAsync(long appId, string privateKeyPem, CancellationToken ct)
    {
        using var request = ApiRequest(HttpMethod.Get, "/app", CreateJwt(appId, privateKeyPem));
        using var document = await SendAsync(request, "read the App", ct);
        var json = document.RootElement;
        var id = json.GetProperty("id").GetInt64();
        if (id != appId)
            throw new GitHubAppException("app-mismatch", "The private key belongs to a different GitHub App.");
        return new GitHubAppInfo(
            id,
            RequiredString(json, "slug"),
            RequiredString(json, "name"),
            json.TryGetProperty("client_id", out var clientId) ? clientId.GetString() ?? string.Empty : string.Empty,
            json.GetProperty("owner").GetProperty("login").GetString() ?? string.Empty,
            RequiredString(json, "html_url"),
            WritePermissions(json));
    }

    /// <summary>The installation that reaches a repository, or null when none does.</summary>
    public async Task<long?> FindRepositoryInstallationAsync(
        long appId, string privateKeyPem, GitHubRepositoryName repository, CancellationToken ct)
    {
        using var request = ApiRequest(HttpMethod.Get,
            $"/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/installation",
            CreateJwt(appId, privateKeyPem));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadAsync(response, "find the repository's installation", ct);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>A one-hour token limited to one repository and the requested permissions; the answer is checked against both.</summary>
    public async Task<GitHubInstallationToken> CreateInstallationTokenAsync(
        long appId,
        string privateKeyPem,
        long installationId,
        GitHubRepositoryName repository,
        IReadOnlyDictionary<string, string> permissions,
        CancellationToken ct)
    {
        using var request = ApiRequest(HttpMethod.Post,
            $"/app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens",
            CreateJwt(appId, privateKeyPem));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            repositories = new[] { repository.Name },
            permissions,
        }), Encoding.UTF8, "application/json");
        using var document = await SendAsync(request, "issue a repository token", ct);
        var json = document.RootElement;
        var token = RequiredString(json, "token");
        var expires = json.GetProperty("expires_at").GetDateTime().ToUniversalTime();
        if (expires <= clock.GetUtcNow().UtcDateTime)
            throw new GitHubAppException("token-scope-mismatch", "GitHub returned an expired repository token.", 502);
        foreach (var granted in json.GetProperty("permissions").EnumerateObject())
        {
            if (!permissions.TryGetValue(granted.Name, out var requested)
                || (requested == RepositoryAccess.Read && granted.Value.GetString() != RepositoryAccess.Read))
                throw new GitHubAppException("token-scope-mismatch", "GitHub granted more than the requested permissions.", 502);
        }
        if (json.TryGetProperty("repositories", out var repositories)
            && (repositories.GetArrayLength() != 1
                || !string.Equals(repositories[0].GetProperty("name").GetString(), repository.Name, StringComparison.OrdinalIgnoreCase)))
            throw new GitHubAppException("token-scope-mismatch", "GitHub granted a different repository scope.", 502);
        return new GitHubInstallationToken(token, expires);
    }

    /// <summary>The web-flow step that proves who authorized the App. The user token leaves this class only to the two calls below.</summary>
    public async Task<string> ExchangeUserCodeAsync(
        string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Web + "/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var document = await SendAsync(request, "finish the GitHub authorization", ct);
        var json = document.RootElement;
        // This endpoint reports its failures as 200 with an error member.
        if (json.TryGetProperty("error", out var error))
            throw new GitHubAppException("authorization-failed",
                $"GitHub refused the authorization: {Short(error.GetString())}.", 502);
        return RequiredString(json, "access_token");
    }

    /// <summary>The installations the authorizing person can really reach. GitHub's setup redirect alone proves nothing.</summary>
    public async Task<IReadOnlyList<GitHubUserInstallation>> ListUserInstallationsAsync(string userToken, CancellationToken ct)
    {
        var found = new List<GitHubUserInstallation>();
        for (var page = 1; page <= 20; page++)
        {
            using var request = ApiRequest(HttpMethod.Get,
                $"/user/installations?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}", userToken);
            using var document = await SendAsync(request, "list the person's installations", ct);
            var installations = document.RootElement.GetProperty("installations");
            foreach (var item in installations.EnumerateArray())
            {
                var account = item.GetProperty("account");
                found.Add(new GitHubUserInstallation(
                    item.GetProperty("id").GetInt64(),
                    item.GetProperty("app_id").GetInt64(),
                    account.GetProperty("login").GetString() ?? string.Empty,
                    account.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty));
            }
            if (installations.GetArrayLength() < 100) break;
        }
        return found;
    }

    /// <summary>Best effort: the user token has done its job and should not outlive the flow.</summary>
    public async Task RevokeUserTokenAsync(string clientId, string clientSecret, string userToken, CancellationToken ct)
    {
        using var request = ApiRequest(HttpMethod.Delete, $"/applications/{Uri.EscapeDataString(clientId)}/token", null);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret)));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { access_token = userToken }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
    }

    public async Task<bool> RevokeInstallationTokenAsync(string token, CancellationToken ct)
    {
        using var request = ApiRequest(HttpMethod.Delete, "/installation/token", token);
        using var response = await http.SendAsync(request, ct);
        return response.StatusCode == HttpStatusCode.NoContent;
    }

    private static HttpRequestMessage ApiRequest(HttpMethod method, string path, string? bearer)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string action, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync(response, action, ct);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GitHubAppException("github-refused",
                $"GitHub could not {action}: HTTP {(int)response.StatusCode}{ErrorMessage(body)}.", 502);
        try { return JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new GitHubAppException("github-refused", $"GitHub answered with an unreadable reply to {action}.", 502);
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("message", out var message)
                ? " (" + Short(message.GetString()) + ")"
                : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static string Short(string? text)
    {
        var value = (text ?? "unknown").ReplaceLineEndings(" ");
        return value.Length <= 200 ? value : value[..200];
    }

    private static string RequiredString(JsonElement json, string name)
        => json.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new GitHubAppException("github-refused", $"GitHub's reply has no '{name}'.", 502);

    private static IReadOnlyList<string> WritePermissions(JsonElement json)
        => json.TryGetProperty("permissions", out var permissions)
            ? permissions.EnumerateObject()
                .Where(permission => permission.Value.GetString() is "write" or "admin")
                .Select(permission => permission.Name)
                .ToArray()
            : [];

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
