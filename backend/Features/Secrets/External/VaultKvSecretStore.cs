using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>Vault and OpenBao KV v2. The remote version number is the secret reference version.</summary>
public sealed class VaultKvSecretStore(SecretBackendBinding binding, HttpClient http,
    Func<SecretBackendBinding, CancellationToken, Task<string>> credential, TimeProvider clock)
    : HttpSecretStore(binding, http, credential, clock)
{
    public override string StoreId => "vault";
    private string Prefix(string operation) => "v1/" + Binding.Mount!.Trim('/') + "/" + operation + "/";
    private static string ScopePath(SecretScope scope)
        => string.Join('/', scope.Key.Split('/').Select(Uri.EscapeDataString));
    private string PathOf(string operation, SecretScope scope, string name)
        => Prefix(operation) + ScopePath(scope) + "/" + Uri.EscapeDataString(name);
    private string MetadataSidecar(SecretScope scope, string name)
        => Prefix("data") + ScopePath(scope) + "/__studio_metadata__/" + Uri.EscapeDataString(name);

    private async Task EnsureMountAsync(CancellationToken ct)
    {
        using var response = await RequestAsync(HttpMethod.Get, "v1/" + Binding.Mount!.Trim('/') + "/config",
            null, ct, missingIsNull: true)
            ?? throw new SecretBackendException("backend-misconfigured", "The selected KV v2 mount was not found.");
        var data = Required(response.RootElement, "data");
        if (data.ValueKind != JsonValueKind.Object)
            throw new SecretBackendException("backend-misconfigured", "The selected mount is not a KV v2 store.");
        var maxVersions = Required(data, "max_versions");
        if (maxVersions.ValueKind != JsonValueKind.Number || !maxVersions.TryGetInt32(out _)
            || Required(data, "cas_required").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new SecretBackendException("backend-misconfigured", "The selected mount is not a KV v2 store.");
    }

    public override async Task HealthAsync(CancellationToken ct)
    {
        await EnsureMountAsync(ct);
        using var _ = await RequestAsync(new HttpMethod("LIST"), Prefix("metadata")
            + ScopePath(SecretScope.Parse(Binding.Scope)) + "/", null, ct, missingIsNull: true);
    }

    public override async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        await EnsureMountAsync(ct);
        var scope = SecretScope.Parse(Binding.Scope);
        using var response = await RequestAsync(new HttpMethod("LIST"), Prefix("metadata") + ScopePath(scope) + "/", null, ct, missingIsNull: true);
        if (response is null) return new([], []);
        var keys = Required(Required(response.RootElement, "data"), "keys");
        var records = new List<SecretRecord>();
        var unreadable = new List<UnreadableSecret>();
        foreach (var key in keys.EnumerateArray())
        {
            var name = key.GetString();
            if (!SecretReference.IsValidName(name)) continue;
            try
            {
                if (await FindAsync(scope, name!, ct) is { } record) records.Add(record);
            }
            catch (SecretBackendException ex)
            {
                unreadable.Add(new("vault:" + scope.Key + "/" + name, scope.Key, name,
                    UnreadableSecret.ReadFailed, ex.Message));
            }
        }
        return new(records, unreadable);
    }

    public override async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
    {
        await EnsureMountAsync(ct);
        using var response = await RequestAsync(HttpMethod.Get, PathOf("metadata", scope, name), null, ct, missingIsNull: true);
        if (response is null) return null;
        var data = Required(response.RootElement, "data");
        using var sidecar = await RequestAsync(HttpMethod.Get, MetadataSidecar(scope, name), null, ct, missingIsNull: true)
            ?? throw new SecretBackendException("backend-record-missing", "The selected backend item has no Studio metadata.");
        var stored = Required(Required(sidecar.RootElement, "data"), "data");
        SecretRecord? record;
        try { record = JsonSerializer.Deserialize<SecretRecord>(RequiredString(stored, "record"), Json); }
        catch (JsonException) { throw new SecretBackendException("backend-record-invalid", "The selected backend item has invalid Studio metadata."); }
        if (record is null)
            throw new SecretBackendException("backend-record-invalid", "The selected backend item has invalid Studio metadata.");
        if (record.Scope != scope.Key || record.Name != name)
            throw new SecretBackendException("backend-record-invalid", "The selected backend item does not match its scope.");
        // Remote deletion and destruction always win over stored metadata.
        var versions = Required(data, "versions");
        return record with { Versions = record.Versions.Select(version =>
        {
            if (!versions.TryGetProperty(version.Version.ToString(), out var remote)
                || Required(remote, "destroyed").GetBoolean()
                || (remote.TryGetProperty("deletion_time", out var deleted) && deleted.GetString() is { Length: > 0 }))
                return version with { RevokedAt = version.RevokedAt ?? Clock.GetUtcNow().UtcDateTime, RevokedBy = version.RevokedBy ?? "backend" };
            return version;
        }).ToList() };
    }

    private async Task SaveMetadata(SecretScope scope, string name, SecretRecord record, CancellationToken ct)
    {
        using var _ = await RequestAsync(HttpMethod.Post, MetadataSidecar(scope, name),
            new { data = new { record = JsonSerializer.Serialize(record, Json) } }, ct);
    }

    public override async Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct);
        try
        {
            var current = await FindAsync(scope, name, ct);
            var next = transition(current) with { Id = BuiltInSecretStore.SecretId(scope, name), Scope = scope.Key, Name = name };
            var expected = next.Versions.Max(item => item.Version);
            using var response = await RequestAsync(HttpMethod.Post, PathOf("data", scope, name),
                new { options = new { cas = current?.Versions.Max(item => item.Version) ?? 0 },
                    data = new { value = Encoding.UTF8.GetString(value.Span) } }, ct);
            if (response is null || Required(Required(response.RootElement, "data"), "version").GetInt32() != expected)
                throw new SecretBackendException("backend-version-conflict", "The backend version changed during the write.");
            await SaveMetadata(scope, name, next, ct);
            if (current is not null)
            {
                var revoked = next.Versions.Where(item => !item.Active && current.Versions.Any(old => old.Version == item.Version && old.Active))
                    .Select(item => item.Version).ToArray();
                if (revoked.Length > 0)
                    using (await RequestAsync(HttpMethod.Post, PathOf("delete", scope, name), new { versions = revoked }, ct)) { }
            }
            Invalidate(scope, name);
            return next;
        }
        finally { WriteGate.Release(); }
    }

    public override async Task<SecretRecord> UpdateAsync(SecretScope scope, string name,
        Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct);
        try
        {
            var current = await FindAsync(scope, name, ct) ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
            var next = transition(current);
            var revoked = next.Versions.Where(item => !item.Active && current.Versions.Any(old => old.Version == item.Version && old.Active))
                .Select(item => item.Version).ToArray();
            if (revoked.Length > 0)
                using (await RequestAsync(HttpMethod.Post, PathOf("delete", scope, name), new { versions = revoked }, ct)) { }
            if (next != current) await SaveMetadata(scope, name, next, ct);
            if (revoked.Length > 0) Invalidate(scope, name);
            return next;
        }
        finally { WriteGate.Release(); }
    }

    protected override async Task<byte[]> FetchValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        using var response = await RequestAsync(HttpMethod.Get, PathOf("data", scope, name) + "?version=" + version, null, ct, missingIsNull: true);
        if (response is null) throw new SecretRevokedException(new SecretReference(scope, name, version).ToString());
        var data = Required(Required(response.RootElement, "data"), "data");
        return Encoding.UTF8.GetBytes(RequiredString(data, "value"));
    }

    public override async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        if (await FindAsync(scope, name, ct) is null) return false;
        using (await RequestAsync(HttpMethod.Delete, PathOf("metadata", scope, name), null, ct)) { }
        using (await RequestAsync(HttpMethod.Delete, Prefix("metadata") + ScopePath(scope)
            + "/__studio_metadata__/" + Uri.EscapeDataString(name), null, ct, missingIsNull: true)) { }
        Invalidate(scope, name);
        return true;
    }
}
