using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>Routes each exact scope to its chosen backend; inheritance stays in SecretProvider.</summary>
public sealed class ScopedSecretStore(BuiltInSecretStore builtIn, SecretAdapterSettings settings,
    IKeyEncryptionKeySource keys, IConfiguration configuration, IHttpClientFactory clients, TimeProvider clock,
    Func<string, string?>? readCredential = null) : ISecretStore
{
    private readonly Dictionary<string, (ISecretStore Store, IExternalSecretBackend Backend)> _external = [];
    private readonly object _gate = new();
    public string StoreId => settings.Snapshot().Count == 0 ? builtIn.StoreId : "scoped";

    public async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        var selections = settings.Snapshot();
        var baseListing = await builtIn.ListAsync(ct);
        var records = baseListing.Records.Where(item => !selections.ContainsKey(item.Scope)).ToList();
        var unreadable = baseListing.Unreadable.Where(item => item.Scope is null || !selections.ContainsKey(item.Scope)).ToList();
        foreach (var (key, config) in selections)
        {
            if (!SecretScope.TryParse(key, out var scope)) continue;
            try
            {
                var listing = await StoreFor(scope).ListAsync(ct);
                records.AddRange(listing.Records.Where(item => item.ParsedScope == scope));
                unreadable.AddRange(listing.Unreadable.Where(item => item.Scope is null || item.Scope == key));
            }
            catch (SecretValidationException ex)
            {
                unreadable.Add(new UnreadableSecret("adapter:" + key, key, null, UnreadableSecret.ReadFailed, ex.Message));
            }
        }
        return new SecretListing(records, unreadable);
    }

    public async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
    {
        var config = settings.For(scope);
        var record = await StoreFor(scope).FindAsync(scope, name, ct);
        // A broken child adapter cannot silently bypass itself and inherit a parent's value.
        if (record is null && config is not null)
            await ExternalFor(scope, config).Backend.ProbeAsync(ct);
        return record;
    }
    public Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
        => StoreFor(scope).AppendVersionAsync(scope, name, value, transition, ct);
    public Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
        => StoreFor(scope).UpdateAsync(scope, name, transition, ct);
    public Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
        => StoreFor(scope).ReadValueAsync(scope, name, version, ct);
    public Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
        => StoreFor(scope).DeleteAsync(scope, name, ct);

    public async Task<SecretAdapterStatus> StatusAsync(SecretScope scope, CancellationToken ct)
    {
        var config = settings.For(scope);
        if (config is null) return new(scope.Key, builtIn.StoreId, true, "Built-in store selected.", 0);
        try
        {
            await ExternalFor(scope, config).Backend.ProbeAsync(ct);
            return new(scope.Key, config.Kind, true, "Reachable.", config.CacheTtlSeconds);
        }
        catch (SecretValidationException ex)
        {
            return new(scope.Key, config.Kind, false, ex.Message, config.CacheTtlSeconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return new(scope.Key, config.Kind, false, "Adapter health check failed: " + ex.GetType().Name + ".", config.CacheTtlSeconds);
        }
    }

    private ISecretStore StoreFor(SecretScope scope)
    {
        var config = settings.For(scope);
        return config is null ? builtIn : ExternalFor(scope, config).Store;
    }

    private (ISecretStore Store, IExternalSecretBackend Backend) ExternalFor(SecretScope scope, SecretAdapterConfig config)
    {
        SecretAdapterSettings.Validate(config);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope.Key + "\n" + JsonSerializer.Serialize(config))));
        lock (_gate)
        {
            if (_external.TryGetValue(fingerprint, out var existing)) return existing;
            var root = Path.Combine(SecretStorePaths.DataRoot(configuration), "adapter-state", fingerprint);
            IExternalSecretBackend backend = config.Kind == "1password"
                ? new OnePasswordBackend(config, readCredential: readCredential)
                : new VaultKv2Backend(config, clients.CreateClient("secret-adapters"), readCredential);
            var store = new ExternalSecretStore(new BuiltInSecretStore(root, keys), backend, clock,
                TimeSpan.FromSeconds(config.CacheTtlSeconds));
            return _external[fingerprint] = (store, backend);
        }
    }
}
