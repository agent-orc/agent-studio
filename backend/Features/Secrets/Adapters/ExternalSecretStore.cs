using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Secrets;

/// <summary>Encrypted local metadata with values held only by the selected external service.</summary>
public sealed class ExternalSecretStore(ISecretStore metadata, IExternalSecretBackend backend,
    TimeProvider clock, TimeSpan ttl) : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Expires, byte[] Value)> _cache = [];
    public string StoreId => backend.Kind;

    public Task<SecretListing> ListAsync(CancellationToken ct) => metadata.ListAsync(ct);
    public Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct) => metadata.FindAsync(scope, name, ct);

    public async Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await metadata.FindAsync(scope, name, ct);
            var next = transition(current);
            var previous = current?.Versions.Max(item => item.Version) ?? 0;
            var expected = next.Versions.Max(item => item.Version);
            if (expected != previous + 1)
                throw new SecretAdapterException("adapter-version-conflict", "The adapter metadata version is inconsistent.");
            var actual = await backend.WriteAsync(scope, name, value, previous, ct);
            if (actual != expected)
                throw new SecretAdapterException("adapter-version-conflict", "The external version differs from Agent Studio metadata.");
            var record = await metadata.AppendVersionAsync(scope, name, ReadOnlyMemory<byte>.Empty, transition, ct);
            Invalidate(scope, name);
            if (current is not null && backend.Kind != "1password")
            {
                var revoked = record.Versions.Where(item => !item.Active && current.Versions.Any(old => old.Version == item.Version && old.Active))
                    .Select(item => item.Version).ToArray();
                await backend.RevokeAsync(scope, name, revoked, ct);
            }
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = await metadata.FindAsync(scope, name, ct)
                ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
            var after = transition(before);
            var newlyRevoked = after.Versions.Where(item => !item.Active && before.Versions.Any(old => old.Version == item.Version && old.Active))
                .Select(item => item.Version).ToArray();
            if (newlyRevoked.Length > 0) await backend.RevokeAsync(scope, name, newlyRevoked, ct);
            var saved = await metadata.UpdateAsync(scope, name, _ => after, ct);
            if (newlyRevoked.Length > 0) Invalidate(scope, name);
            return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var record = await metadata.FindAsync(scope, name, ct)
                ?? throw new SecretNotFoundException(new SecretReference(scope, name, version).ToString());
            if (record.Versions.All(item => item.Version != version))
                throw new SecretNotFoundException(new SecretReference(scope, name, version).ToString());
            if (!record.Versions.Any(item => item.Version == version && item.Active))
                throw new SecretRevokedException(new SecretReference(scope, name, version).ToString());
            var key = scope.Key + "\n" + name + "\n" + version;
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.Expires > clock.GetUtcNow()) return cached.Value.ToArray();
                CryptographicOperations.ZeroMemory(cached.Value);
                _cache.Remove(key);
            }
            var value = await backend.ReadAsync(scope, name, version, ct);
            if (ttl > TimeSpan.Zero) _cache[key] = (clock.GetUtcNow() + ttl, value.ToArray());
            return value;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await metadata.FindAsync(scope, name, ct) is null) return false;
            await backend.DeleteAsync(scope, name, ct);
            Invalidate(scope, name);
            return await metadata.DeleteAsync(scope, name, ct);
        }
        finally { _gate.Release(); }
    }

    private void Invalidate(SecretScope scope, string name)
    {
        var prefix = scope.Key + "\n" + name + "\n";
        foreach (var key in _cache.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            CryptographicOperations.ZeroMemory(_cache[key].Value);
            _cache.Remove(key);
        }
    }
}
