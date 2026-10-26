using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>HTTP boundary shared by external stores. Response bodies and credentials never enter exceptions or logs.</summary>
public abstract class HttpSecretStore(SecretBackendBinding binding, HttpClient http,
    Func<SecretBackendBinding, CancellationToken, Task<string>> credential, TimeProvider clock) : IExternalSecretStore, IDisposable
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected SecretBackendBinding Binding { get; } = binding;
    protected TimeProvider Clock { get; } = clock;
    private readonly Dictionary<(string Scope, string Name, int Version), (byte[] Value, DateTimeOffset Until)> _cache = [];
    private readonly object _cacheGate = new();
    protected readonly SemaphoreSlim WriteGate = new(1, 1);

    public abstract string StoreId { get; }
    public abstract Task HealthAsync(CancellationToken ct);
    public abstract Task<SecretListing> ListAsync(CancellationToken ct);
    public abstract Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct);
    public abstract Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct);
    public abstract Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct);
    public abstract Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct);

    public async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        var key = (scope.Key, name, version);
        lock (_cacheGate)
        {
            foreach (var expired in _cache.Where(item => item.Value.Until <= Clock.GetUtcNow()).Select(item => item.Key).ToList())
            {
                Array.Clear(_cache[expired].Value);
                _cache.Remove(expired);
            }
            if (_cache.TryGetValue(key, out var cached) && cached.Until > Clock.GetUtcNow())
                return (byte[])cached.Value.Clone();
        }
        var value = await FetchValueAsync(scope, name, version, ct);
        lock (_cacheGate)
        {
            if (_cache.Remove(key, out var old)) Array.Clear(old.Value);
            _cache[key] = ((byte[])value.Clone(), Clock.GetUtcNow().AddSeconds(Binding.TtlSeconds));
        }
        return value;
    }

    protected abstract Task<byte[]> FetchValueAsync(SecretScope scope, string name, int version, CancellationToken ct);

    protected void Invalidate(SecretScope scope, string name)
    {
        lock (_cacheGate)
            foreach (var key in _cache.Keys.Where(item => item.Scope == scope.Key && item.Name == name).ToList())
            {
                Array.Clear(_cache[key].Value);
                _cache.Remove(key);
            }
    }

    public void Dispose()
    {
        lock (_cacheGate)
        {
            foreach (var item in _cache.Values) Array.Clear(item.Value);
            _cache.Clear();
        }
        http.Dispose();
        WriteGate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task<JsonDocument?> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        bool missingIsNull = false)
    {
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(new Uri(Binding.Url.TrimEnd('/') + "/"), path.TrimStart('/')));
            var token = await credential(Binding, ct);
            if (StoreId == "vault") request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && missingIsNull) return null;
            if (!response.IsSuccessStatusCode)
                throw new SecretBackendException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "backend-auth-failed" : "backend-unavailable",
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "The selected backend refused its credential or permission."
                        : "The selected backend refused the operation.");
            if (method == HttpMethod.Delete) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? null : JsonDocument.Parse(bytes);
        }
        catch (HttpRequestException) { throw new SecretBackendException("backend-unreachable", "The selected backend cannot be reached."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new SecretBackendException("backend-timeout", "The selected backend timed out."); }
        catch (JsonException) { throw new SecretBackendException("backend-response-invalid", "The selected backend returned an invalid response."); }
    }

    protected static JsonElement Required(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) ? value
            : throw new SecretBackendException("backend-response-invalid", "The selected backend returned an invalid response.");

    protected static string RequiredString(JsonElement parent, string property)
        => Required(parent, property).GetString()
            ?? throw new SecretBackendException("backend-response-invalid", "The selected backend returned an invalid response.");
}
