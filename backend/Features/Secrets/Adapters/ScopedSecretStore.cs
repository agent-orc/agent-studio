using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Secrets;

/// <summary>Routes each exact scope to its configured store. Parent fallback remains in SecretProvider.</summary>
public sealed class ScopedSecretStore : ISecretStore
{
    private readonly BuiltInSecretStore _builtIn;
    private readonly SecretAdapterSettings _settings;
    private readonly IKeyEncryptionKeySource _keys;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly string _root;
    private readonly ConcurrentDictionary<SecretAdapterBinding, ExternalSecretStore> _adapters = new();

    public ScopedSecretStore(BuiltInSecretStore builtIn, SecretAdapterSettings settings, IKeyEncryptionKeySource keys,
        IHttpClientFactory http, TimeProvider clock, IConfiguration configuration)
    {
        _builtIn = builtIn;
        _settings = settings;
        _keys = keys;
        _http = http;
        _clock = clock;
        _root = Path.Combine(SecretStorePaths.DataRoot(configuration), "adapter-metadata");
    }

    public string StoreId => _settings.HasBindings ? "scoped" : BuiltInSecretStore.Id;

    public async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        var bindings = await _settings.ReadAsync(ct);
        var selected = bindings.Select(binding => SecretScope.Parse(binding.Scope)).ToHashSet();
        var local = await _builtIn.ListAsync(ct);
        var records = local.Records.Where(record => !selected.Contains(record.ParsedScope)).ToList();
        var unreadable = local.Unreadable.Where(item => item.Scope is null || !SecretScope.TryParse(item.Scope, out var scope) || !selected.Contains(scope)).ToList();
        foreach (var binding in bindings)
        {
            var scope = SecretScope.Parse(binding.Scope);
            try
            {
                var listing = await Adapter(binding).ListAsync(ct);
                records.AddRange(listing.Records.Where(item => item.ParsedScope == scope));
                unreadable.AddRange(listing.Unreadable.Where(item => item.Scope == scope.Key));
            }
            catch (SecretAdapterException ex)
            {
                unreadable.Add(new UnreadableSecret("adapter:" + scope.Key, scope.Key, null, "adapter-unavailable", ex.Message));
            }
        }
        return new SecretListing(records, unreadable);
    }

    public async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
        => await (await StoreForAsync(scope, ct)).FindAsync(scope, name, ct);

    public async Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
        => await (await StoreForAsync(scope, ct)).AppendVersionAsync(scope, name, value, transition, ct);

    public async Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
        => await (await StoreForAsync(scope, ct)).UpdateAsync(scope, name, transition, ct);

    public async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
        => await (await StoreForAsync(scope, ct)).ReadValueAsync(scope, name, version, ct);

    public async Task<byte[]> ReadValueForRecordAsync(SecretRecord record, int version, CancellationToken ct)
    {
        var current = (await _settings.ReadAsync(ct)).FirstOrDefault(item =>
            string.Equals(item.Scope, record.Scope, StringComparison.OrdinalIgnoreCase));
        if (current != record.AdapterBinding)
            throw new SecretAdapterException("adapter-binding-changed", "The secret store binding changed during resolution. Retry the request.");
        ISecretStore store = current is null ? _builtIn : Adapter(current);
        return await store.ReadValueAsync(record.ParsedScope, record.Name, version, ct);
    }

    public async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
        => await (await StoreForAsync(scope, ct)).DeleteAsync(scope, name, ct);

    public async Task<SecretAdapterHealth> HealthAsync(SecretAdapterBinding binding, CancellationToken ct)
    {
        try
        {
            await Adapter(binding).ProbeAsync(ct);
            return new SecretAdapterHealth(binding.Scope, binding.Kind, true, null);
        }
        catch (SecretAdapterException ex) { return new SecretAdapterHealth(binding.Scope, binding.Kind, false, ex.Message); }
    }

    private async Task<ISecretStore> StoreForAsync(SecretScope scope, CancellationToken ct)
    {
        var binding = (await _settings.ReadAsync(ct)).FirstOrDefault(item =>
            string.Equals(item.Scope, scope.Key, StringComparison.OrdinalIgnoreCase));
        return binding is null ? _builtIn : Adapter(binding);
    }

    private ExternalSecretStore Adapter(SecretAdapterBinding binding)
        => _adapters.GetOrAdd(binding, current =>
        {
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                current.Scope.ToLowerInvariant() + "\n" + current.Kind + "\n" + current.Endpoint + "\n" + current.Container)))[..24];
            var metadata = new BuiltInSecretStore(Path.Combine(_root, id), _keys);
            return new ExternalSecretStore(current, metadata, _builtIn, _http.CreateClient("secret-adapters"), _clock);
        });
}
