using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>HashiCorp Vault and OpenBao share the KV v2 HTTP wire contract.</summary>
public sealed class VaultKv2Backend(SecretAdapterConfig config, HttpClient client,
    Func<string, string?>? readCredential = null) : IExternalSecretBackend
{
    public string Kind => config.Kind;

    public async Task ProbeAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "auth/token/lookup-self", null, ct, mountPath: false);
        RequireSuccess(response);
    }

    public async Task<int> WriteAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value, int previousVersion, CancellationToken ct)
    {
        var payload = new { options = new { cas = previousVersion }, data = new { value = System.Text.Encoding.UTF8.GetString(value.Span) } };
        using var response = await SendAsync(HttpMethod.Post, "data/" + Key(scope, name), payload, ct);
        RequireSuccess(response);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var version = body.RootElement.GetProperty("data").GetProperty("version").GetInt32();
        if (version != previousVersion + 1)
            throw new SecretAdapterException("adapter-version-conflict", "KV v2 returned an unexpected version.");
        return version;
    }

    public async Task<byte[]> ReadAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "data/" + Key(scope, name) + "?version=" + version, null, ct);
        RequireSuccess(response);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var data = body.RootElement.GetProperty("data");
        if (data.GetProperty("metadata").GetProperty("version").GetInt32() != version)
            throw new SecretAdapterException("adapter-version-conflict", "KV v2 returned another version.");
        return System.Text.Encoding.UTF8.GetBytes(data.GetProperty("data").GetProperty("value").GetString()
            ?? throw new SecretAdapterException("adapter-value-missing", "KV v2 returned no value."));
    }

    public async Task RevokeAsync(SecretScope scope, string name, IReadOnlyCollection<int> versions, CancellationToken ct)
    {
        if (versions.Count == 0) return;
        using var response = await SendAsync(HttpMethod.Post, "destroy/" + Key(scope, name), new { versions }, ct);
        RequireSuccess(response);
    }

    public async Task DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, "metadata/" + Key(scope, name), null, ct);
        RequireSuccess(response);
    }

    private string Key(SecretScope scope, string name)
        => "agent-studio/" + string.Join('/', scope.Key.Split('/').Select(Uri.EscapeDataString)) + "/" + Uri.EscapeDataString(name);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string suffix, object? payload, CancellationToken ct,
        bool mountPath = true)
    {
        using var request = new HttpRequestMessage(method,
            config.Address!.TrimEnd('/') + "/v1/" + (mountPath ? config.Mount!.Trim('/') + "/" : "") + suffix);
        request.Headers.TryAddWithoutValidation("X-Vault-Token", SecretAdapterCredentials.Require(config.CredentialEnvironment, readCredential));
        if (payload is not null) request.Content = JsonContent.Create(payload);
        try { return await client.SendAsync(request, ct); }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new SecretAdapterException("adapter-unreachable", "The Vault or OpenBao service cannot be reached.");
        }
    }

    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new SecretAdapterException("adapter-not-found", "The KV v2 path or version was not found."),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new SecretAdapterException("adapter-auth-failed", "The Vault or OpenBao credential was rejected."),
            HttpStatusCode.BadRequest => new SecretAdapterException("adapter-write-conflict", "KV v2 refused the write; check its version and policy."),
            _ => new SecretAdapterException("adapter-unavailable", "Vault or OpenBao returned HTTP " + (int)response.StatusCode + ".")
        };
    }
}
