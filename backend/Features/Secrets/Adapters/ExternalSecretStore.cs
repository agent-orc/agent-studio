using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

public sealed class SecretAdapterException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Stores values remotely and only metadata with an encrypted dummy value locally.</summary>
public sealed class ExternalSecretStore : ISecretStore
{
    private readonly SecretAdapterBinding _binding;
    private readonly BuiltInSecretStore _metadata;
    private readonly BuiltInSecretStore _credentials;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedValue> _cache = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record CachedValue(byte[] Bytes, DateTimeOffset Expires);

    public ExternalSecretStore(SecretAdapterBinding binding, BuiltInSecretStore metadata, BuiltInSecretStore credentials,
        HttpClient http, TimeProvider clock)
    {
        SecretAdapterSettings.Validate(binding);
        _binding = binding;
        _metadata = metadata;
        _credentials = credentials;
        _http = http;
        _clock = clock;
    }

    public string StoreId => _binding.Kind;
    private bool Vault => _binding.Kind == "vault";
    private string Base => _binding.Endpoint.TrimEnd('/');
    private string Prefix => Vault ? $"{Base}/v1/{_binding.Container}/" : $"{Base}/v1/vaults/{_binding.Container}/items";

    public async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        // A missing token or unreachable service must never expose stale metadata as a healthy scope.
        await ProbeAsync(ct);
        var listing = await _metadata.ListAsync(ct);
        return listing with { Records = listing.Records.Select(item => item with { AdapterBinding = _binding }).ToList() };
    }

    public async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
    {
        await ProbeAsync(ct);
        return await _metadata.FindAsync(scope, name, ct) is { } record ? record with { AdapterBinding = _binding } : null;
    }

    public async Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = await _metadata.FindAsync(scope, name, ct);
            var planned = transition(before);
            var version = planned.Versions.Max(item => item.Version);
            if (Vault)
            {
                using var response = await SendAsync(HttpMethod.Post, VaultPath("data", scope, name),
                    new { options = new { cas = before?.Versions.Max(item => item.Version) ?? 0 }, data = new { value = Encoding.UTF8.GetString(value.Span) } }, ct);
                using var document = await ReadJsonAsync(response, ct);
                var actual = document.RootElement.GetProperty("data").GetProperty("version").GetInt32();
                if (actual != version) throw new SecretAdapterException("version-conflict", "The remote secret version changed. Refresh and retry.");
            }
            else
            {
                var title = Title(scope, name, version);
                using var response = await SendAsync(HttpMethod.Post, Prefix,
                    new { title, category = "SECURE_NOTE", vault = new { id = _binding.Container },
                        fields = new[] { new { label = "value", type = "CONCEALED", value = Encoding.UTF8.GetString(value.Span) } } }, ct);
            }
            // The dummy never contains the remote value. A metadata failure leaves an unreachable remote orphan.
            var record = await _metadata.AppendVersionAsync(scope, name, Encoding.UTF8.GetBytes("external-metadata"), transition, ct);
            foreach (var revoked in record.Versions.Where(item => item.RevokedAt is not null && item.Version != version))
                await DeleteVersionAsync(scope, name, revoked.Version, ct);
            Invalidate(scope, name);
            return record with { AdapterBinding = _binding };
        }
        finally { _gate.Release(); }
    }

    public async Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = await _metadata.FindAsync(scope, name, ct)
                ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
            var next = transition(before);
            foreach (var revoked in next.Versions.Where(item => item.RevokedAt is not null &&
                         before.Versions.First(old => old.Version == item.Version).RevokedAt is null))
                await DeleteVersionAsync(scope, name, revoked.Version, ct);
            var record = await _metadata.UpdateAsync(scope, name, _ => next, ct);
            if (next.Versions.Any(item => item.RevokedAt is not null &&
                before.Versions.First(old => old.Version == item.Version).RevokedAt is null)) Invalidate(scope, name);
            return record with { AdapterBinding = _binding };
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        var key = new SecretReference(scope, name, version).ToString();
        // Metadata is checked on every issue, including cache hits, so local revocation takes effect immediately.
        var record = await _metadata.FindAsync(scope, name, ct)
            ?? throw new SecretNotFoundException(key);
        SecretVersionPolicy.Select(record, version);
        foreach (var stale in _cache.Where(item => item.Value.Expires <= _clock.GetUtcNow()))
            _cache.TryRemove(stale.Key, out _);
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > _clock.GetUtcNow())
            return (byte[])cached.Bytes.Clone();
        string value;
        if (Vault)
        {
            using var response = await SendAsync(HttpMethod.Get, VaultPath("data", scope, name) + "?version=" + version, null, ct);
            using var document = await ReadJsonAsync(response, ct);
            var data = document.RootElement.GetProperty("data");
            if (data.GetProperty("metadata").GetProperty("version").GetInt32() != version)
                throw new SecretAdapterException("version-conflict", "The remote secret version does not match the requested version.");
            value = data.GetProperty("data").GetProperty("value").GetString()
                ?? throw new SecretAdapterException("invalid-response", "The remote secret has no value field.");
        }
        else
        {
            var id = await FindItemIdAsync(scope, name, version, ct)
                ?? throw new SecretNotFoundException(key);
            using var response = await SendAsync(HttpMethod.Get, Prefix + "/" + Uri.EscapeDataString(id), null, ct);
            using var document = await ReadJsonAsync(response, ct);
            value = document.RootElement.GetProperty("fields").EnumerateArray()
                .FirstOrDefault(field => field.TryGetProperty("label", out var label) && label.GetString() == "value")
                .GetProperty("value").GetString()
                ?? throw new SecretAdapterException("invalid-response", "The remote item has no value field.");
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        if (_binding.CacheSeconds > 0)
            _cache[key] = new CachedValue((byte[])bytes.Clone(), _clock.GetUtcNow().AddSeconds(_binding.CacheSeconds));
        return bytes;
    }

    public async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var record = await _metadata.FindAsync(scope, name, ct);
            if (record is null) return false;
            foreach (var version in record.Versions)
                await DeleteVersionAsync(scope, name, version.Version, ct);
            if (Vault)
                using (await SendAsync(HttpMethod.Delete, VaultPath("metadata", scope, name), null, ct)) { }
            Invalidate(scope, name);
            return await _metadata.DeleteAsync(scope, name, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task ProbeAsync(CancellationToken ct)
    {
        var path = Vault ? Prefix + "config" :
            $"{Base}/v1/vaults/{_binding.Container}";
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
    }

    private async Task DeleteVersionAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        if (Vault)
        {
            using var response = await SendAsync(HttpMethod.Put, VaultPath("destroy", scope, name), new { versions = new[] { version } }, ct);
        }
        else if (await FindItemIdAsync(scope, name, version, ct) is { } id)
        {
            using var response = await SendAsync(HttpMethod.Delete, Prefix + "/" + Uri.EscapeDataString(id), null, ct);
        }
    }

    private async Task<string?> FindItemIdAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString("title eq \"" + Title(scope, name, version) + "\"");
        using var response = await SendAsync(HttpMethod.Get, Prefix + "?filter=" + filter, null, ct);
        using var document = await ReadJsonAsync(response, ct);
        var matches = document.RootElement.EnumerateArray().Where(item =>
            item.GetProperty("title").GetString() == Title(scope, name, version)).ToList();
        if (matches.Count > 1) throw new SecretAdapterException("duplicate-item", "The remote vault has duplicate secret items.");
        return matches.Count == 0 ? null : matches[0].GetProperty("id").GetString();
    }

    private static string Title(SecretScope scope, string name, int version)
        => $"agent-studio/{scope.Key}/{name}/v{version}";

    private string VaultPath(string operation, SecretScope scope, string name)
        => $"{Prefix}{operation}/{string.Join('/', scope.Key.Split('/').Select(Uri.EscapeDataString))}/{Uri.EscapeDataString(name)}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        var token = await TokenAsync(ct);
        if (Vault) request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            if (Vault && method == HttpMethod.Post && uri.Contains("/data/", StringComparison.Ordinal) &&
                status == HttpStatusCode.BadRequest)
                throw new SecretAdapterException("version-conflict", "The remote secret version changed. Refresh and retry.");
            throw new SecretAdapterException(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "adapter-auth" : "adapter-unavailable",
                status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "The adapter rejected its credential." :
                $"The adapter returned HTTP {(int)status}.");
        }
        catch (HttpRequestException)
        {
            throw new SecretAdapterException("adapter-unreachable", "The secret adapter cannot be reached.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SecretAdapterException("adapter-timeout", "The secret adapter timed out.");
        }
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_binding.Credential.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(_binding.Credential[4..]) is { Length: > 0 } token
                ? token : throw new SecretAdapterException("adapter-credential-missing", "The adapter credential environment variable is missing.");
        var reference = SecretReference.Parse(_binding.Credential);
        var record = await _credentials.FindAsync(reference.Scope, reference.Name, ct)
            ?? throw new SecretAdapterException("adapter-credential-missing", "The adapter credential secret is missing.");
        var version = SecretVersionPolicy.Select(record, reference.Version);
        var bytes = await _credentials.ReadValueAsync(reference.Scope, reference.Name, version.Version, ct);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { Array.Clear(bytes); }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch (JsonException) { throw new SecretAdapterException("invalid-response", "The secret adapter returned invalid JSON."); }
    }

    private void Invalidate(SecretScope scope, string name)
    {
        foreach (var key in _cache.Keys.Where(key => key.StartsWith(new SecretReference(scope, name).ToString() + "@", StringComparison.Ordinal)))
            _cache.TryRemove(key, out _);
    }
}
