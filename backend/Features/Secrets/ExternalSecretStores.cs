using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>HTTP transport shared by the two external adapters. Error bodies are never logged or returned.</summary>
public abstract class HttpSecretStore(SecretStoreAssignment assignment, SecretAdapterCredentials credentials,
    HttpClient client, TimeProvider clock) : ISecretStore, IRemoteSecretStore
{
    protected readonly SecretStoreAssignment Assignment = assignment;
    protected readonly TimeProvider Clock = clock;
    private readonly ConcurrentDictionary<string, (byte[] Value, DateTimeOffset Expires)> _values = new();
    protected readonly SemaphoreSlim Gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, string By, string Purpose, int Version)> _use = new();
    protected SecretRecord WithUsage(SecretRecord record)
    {
        if (!_use.TryGetValue(record.Scope + "/" + record.Name, out var stamp)) return record;
        return record with
        {
            LastUsedAt = stamp.At, LastUsedBy = stamp.By, LastUsedPurpose = stamp.Purpose,
            Versions = record.Versions.Select(item => item.Version == stamp.Version
                ? item with { LastUsedAt = stamp.At } : item).ToList()
        };
    }
    protected void RememberUsage(SecretRecord previous, SecretRecord desired)
    {
        if (desired.LastUsedAt is not { } at || desired.LastUsedBy is not { } by
            || desired.LastUsedPurpose is not { } purpose) return;
        var changed = desired.Versions.FirstOrDefault(item => previous.Versions.Any(old =>
            old.Version == item.Version && !ReferenceEquals(old, item)));
        if (changed is not null) _use[desired.Scope + "/" + desired.Name] = (at, by, purpose, changed.Version);
    }
    protected void ClearUsage(SecretScope scope, string name) => _use.TryRemove(scope.Key + "/" + name, out _);
    public abstract string StoreId { get; }
    public abstract Task ProbeAsync(CancellationToken ct);
    public abstract Task<SecretListing> ListAsync(CancellationToken ct);
    public abstract Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct);
    public abstract Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct);
    public abstract Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct);
    public abstract Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct);
    protected abstract Task<byte[]> FetchValueAsync(SecretScope scope, string name, int version, CancellationToken ct);

    public virtual async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        var key = scope.Key + "/" + name + "@" + version;
        if (_values.TryGetValue(key, out var cached) && Clock.GetUtcNow() < cached.Expires)
            return cached.Value.ToArray();
        var value = await FetchValueAsync(scope, name, version, ct);
        _values[key] = (value.ToArray(), Clock.GetUtcNow().AddSeconds(Assignment.TtlSeconds));
        return value;
    }

    protected void Clear(SecretScope scope, string name)
    {
        foreach (var key in _values.Keys.Where(x => x.StartsWith(scope.Key + "/" + name + "@", StringComparison.Ordinal)))
            if (_values.TryRemove(key, out var old)) Array.Clear(old.Value);
    }

    protected async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body, bool missingIsNull, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(Assignment.Address!.TrimEnd('/') + "/"), path.TrimStart('/')));
        var token = await credentials.ResolveAsync(Assignment.Credential, ct);
        if (StoreId == "vault") request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && missingIsNull) return null;
            if (!response.IsSuccessStatusCode)
                throw new SecretProviderUnavailableException($"{StoreId} returned HTTP {(int)response.StatusCode}; check provider configuration and permissions.");
            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException)
        {
            throw new SecretProviderUnavailableException($"{StoreId} is unreachable.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SecretProviderUnavailableException($"{StoreId} did not respond in time.");
        }
        catch (JsonException)
        {
            throw new SecretProviderUnavailableException($"{StoreId} returned an invalid response.");
        }
    }

    protected static DateTime Date(JsonElement parent, string field, DateTime fallback)
        => parent.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String
           && DateTime.TryParse(element.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime() : fallback;

    protected static SecretRecord Record(SecretScope scope, string name, string id, DateTime created, DateTime updated,
        List<SecretVersionRecord> versions) => new()
    {
        Id = id, Scope = scope.Key, Name = name, CreatedAt = created, CreatedBy = "external",
        UpdatedAt = updated, UpdatedBy = "external", Versions = versions
    };
}

/// <summary>Vault and OpenBao KV v2: remote version numbers and delete markers are authoritative.</summary>
public sealed class VaultKv2SecretStore(SecretStoreAssignment assignment, SecretAdapterCredentials credentials,
    HttpClient client, TimeProvider clock) : HttpSecretStore(assignment, credentials, client, clock)
{
    public override string StoreId => "vault";
    private string Root => "v1/" + Assignment.Mount + "/";
    private string PathFor(string operation, SecretScope scope, string name)
        => Root + operation + "/" + scope.Key.Split('/').Select(Uri.EscapeDataString).Aggregate((a, b) => a + "/" + b)
           + "/" + Uri.EscapeDataString(name);

    public override async Task ProbeAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new HttpMethod("LIST"), Root + "metadata/"
            + string.Join('/', Assignment.Scope.Split('/').Select(Uri.EscapeDataString)), null, true, ct);
    }

    public override async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        var scope = SecretScope.Parse(Assignment.Scope);
        var prefix = Root + "metadata/" + string.Join('/', scope.Key.Split('/').Select(Uri.EscapeDataString));
        using var listing = await SendAsync(new HttpMethod("LIST"), prefix, null, true, ct);
        if (listing is null) return new SecretListing([], []);
        var keys = listing.RootElement.GetProperty("data").GetProperty("keys").EnumerateArray()
            .Select(x => x.GetString()).Where(x => x is not null && SecretReference.IsValidName(x)).ToList();
        var records = new List<SecretRecord>();
        foreach (var key in keys)
            if (await FindAsync(scope, key!, ct) is { } record) records.Add(record);
        return new SecretListing(records, []);
    }

    public override async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, PathFor("metadata", scope, name), null, true, ct);
        if (document is null) return null;
        var data = document.RootElement.GetProperty("data");
        var now = Clock.GetUtcNow().UtcDateTime;
        var versions = new List<SecretVersionRecord>();
        foreach (var property in data.GetProperty("versions").EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var number)) continue;
            var item = property.Value;
            var deleted = item.TryGetProperty("deletion_time", out var deletion) && deletion.ValueKind == JsonValueKind.String
                          && !string.IsNullOrEmpty(deletion.GetString());
            var destroyed = item.TryGetProperty("destroyed", out var flag) && flag.ValueKind == JsonValueKind.True;
            versions.Add(new SecretVersionRecord
            {
                Version = number, CreatedAt = Date(item, "created_time", now), CreatedBy = "external",
                Origin = number == 1 ? SecretAuditActions.Put : SecretAuditActions.Rotate,
                RevokedAt = deleted || destroyed ? Date(item, "deletion_time", now) : null,
                RevokedBy = deleted || destroyed ? "external" : null
            });
        }
        return WithUsage(Record(scope, name, BuiltInSecretStore.SecretId(scope, name), Date(data, "created_time", now),
            Date(data, "updated_time", now), versions));
    }

    public override async Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var current = await FindAsync(scope, name, ct);
            var desired = transition(current);
            var expected = current?.Versions.Max(x => x.Version) ?? 0;
            using var written = await SendAsync(HttpMethod.Post, PathFor("data", scope, name),
                new { options = new { cas = expected }, data = new { value = Encoding.UTF8.GetString(value.Span) } }, false, ct);
            var actual = written?.RootElement.GetProperty("data").GetProperty("version").GetInt32()
                ?? throw new SecretProviderUnavailableException("Vault did not return a version.");
            if (actual != expected + 1)
                throw new SecretProviderUnavailableException("Vault returned an unexpected version.");
            if (current is not null && desired.Versions.Any(x => x.Version <= expected && !x.Active))
            {
                var revoked = desired.Versions.Where(x => x.Version <= expected && !x.Active).Select(x => x.Version).ToArray();
                if (revoked.Length > 0)
                    using (await SendAsync(HttpMethod.Post, PathFor("delete", scope, name), new { versions = revoked }, false, ct)) { }
            }
            Clear(scope, name);
            return await FindAsync(scope, name, ct) ?? throw new SecretProviderUnavailableException("Vault write was not visible.");
        }
        finally { Gate.Release(); }
    }

    public override async Task<SecretRecord> UpdateAsync(SecretScope scope, string name,
        Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var current = await FindAsync(scope, name, ct) ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
            var desired = transition(current);
            var revoked = current.Versions.Where(old => old.Active && desired.Versions.Any(x => x.Version == old.Version && !x.Active))
                .Select(x => x.Version).ToArray();
            if (revoked.Length > 0)
            {
                using var response = await SendAsync(HttpMethod.Post, PathFor("delete", scope, name), new { versions = revoked }, false, ct);
                Clear(scope, name);
                return await FindAsync(scope, name, ct) ?? throw new SecretProviderUnavailableException("Vault revocation was not visible.");
            }
            RememberUsage(current, desired);
            return desired;
        }
        finally { Gate.Release(); }
    }

    public override async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        var current = await FindAsync(scope, name, ct)
            ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
        _ = SecretVersionPolicy.Select(current, version);
        return await base.ReadValueAsync(scope, name, version, ct);
    }

    protected override async Task<byte[]> FetchValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, PathFor("data", scope, name) + "?version=" + version, null, true, ct);
        if (response is null) throw new SecretRevokedException(new SecretReference(scope, name, version).ToString());
        var data = response.RootElement.GetProperty("data");
        if (data.GetProperty("metadata").GetProperty("version").GetInt32() != version)
            throw new SecretIntegrityException("Vault returned a different secret version.");
        return Encoding.UTF8.GetBytes(data.GetProperty("data").GetProperty("value").GetString() ?? string.Empty);
    }

    public override async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        if (await FindAsync(scope, name, ct) is null) return false;
        using var response = await SendAsync(HttpMethod.Delete, PathFor("metadata", scope, name), null, false, ct);
        Clear(scope, name);
        ClearUsage(scope, name);
        return true;
    }
}

/// <summary>1Password Connect uses a service-account-backed vault and returns the item's current version.</summary>
public sealed class OnePasswordSecretStore(SecretStoreAssignment assignment, SecretAdapterCredentials credentials,
    HttpClient client, TimeProvider clock) : HttpSecretStore(assignment, credentials, client, clock)
{
    public override string StoreId => "onepassword";
    private string Root => "v1/vaults/" + Uri.EscapeDataString(Assignment.Vault!) + "/items";
    private string Title(SecretScope scope, string name) => scope.Key + "/" + name;
    private readonly ConcurrentDictionary<string, string> _ids = new();

    public override async Task ProbeAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "v1/vaults/" + Uri.EscapeDataString(Assignment.Vault!), null, false, ct);
    }

    public override async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Root, null, false, ct);
        var scope = SecretScope.Parse(Assignment.Scope);
        var records = new List<SecretRecord>();
        foreach (var item in response!.RootElement.EnumerateArray())
        {
            var title = item.GetProperty("title").GetString();
            if (title is null || !title.StartsWith(scope.Key + "/", StringComparison.OrdinalIgnoreCase)
                || !item.TryGetProperty("tags", out var tags)
                || !tags.EnumerateArray().Any(x => x.GetString() == "agent-studio-secret")) continue;
            var name = title[(scope.Key.Length + 1)..];
            if (!SecretReference.IsValidName(name)) continue;
            var id = item.GetProperty("id").GetString()!;
            _ids[title] = id;
            using var full = await SendAsync(HttpMethod.Get, Root + "/" + Uri.EscapeDataString(id), null, false, ct);
            records.Add(ToRecord(scope, name, full!.RootElement));
        }
        return new SecretListing(records, []);
    }

    public override async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
    {
        if (!_ids.TryGetValue(Title(scope, name), out var id))
        {
            var listing = await ListAsync(ct);
            return listing.Records.FirstOrDefault(x => x.ParsedScope == scope && x.Name == name);
        }
        using var response = await SendAsync(HttpMethod.Get, Root + "/" + Uri.EscapeDataString(id), null, true, ct);
        return response is null ? null : ToRecord(scope, name, response.RootElement);
    }

    public override async Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var current = await FindAsync(scope, name, ct);
            _ = transition(current);
            var payload = new Dictionary<string, object>
            {
                ["vault"] = new { id = Assignment.Vault }, ["category"] = "PASSWORD", ["title"] = Title(scope, name),
                ["tags"] = new[] { "agent-studio-secret" },
                ["fields"] = new[] { new { purpose = "PASSWORD", label = "password", value = Encoding.UTF8.GetString(value.Span) } }
            };
            if (current is not null)
            {
                payload["id"] = current.Id;
                payload["version"] = current.Versions.Max(x => x.Version);
            }
            var path = current is null ? Root : Root + "/" + Uri.EscapeDataString(current.Id);
            using var response = await SendAsync(current is null ? HttpMethod.Post : HttpMethod.Put, path, payload, false, ct);
            Clear(scope, name);
            var record = ToRecord(scope, name, response!.RootElement);
            _ids[Title(scope, name)] = record.Id;
            return record;
        }
        finally { Gate.Release(); }
    }

    public override async Task<SecretRecord> UpdateAsync(SecretScope scope, string name,
        Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
    {
        var current = await FindAsync(scope, name, ct) ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
        var desired = transition(current);
        if (current.Versions.Any(old => old.Active && desired.Versions.Any(item =>
            item.Version == old.Version && !item.Active)))
            throw new SecretProviderUnsupportedException("revocation of 1Password item versions");
        RememberUsage(current, desired);
        return desired;
    }

    public override async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        var current = await FindAsync(scope, name, ct)
            ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
        _ = SecretVersionPolicy.Select(current, version);
        return await base.ReadValueAsync(scope, name, version, ct);
    }

    protected override async Task<byte[]> FetchValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        if (!_ids.TryGetValue(Title(scope, name), out var id))
            throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
        using var response = await SendAsync(HttpMethod.Get, Root + "/" + Uri.EscapeDataString(id), null, false, ct);
        if (response!.RootElement.GetProperty("version").GetInt32() != version)
            throw new SecretRevokedException(new SecretReference(scope, name, version).ToString());
        var fields = response!.RootElement.GetProperty("fields").EnumerateArray();
        var field = fields.FirstOrDefault(x =>
            (x.TryGetProperty("purpose", out var purpose) && purpose.GetString() == "PASSWORD")
            || (x.TryGetProperty("label", out var label) && label.GetString() == "password"));
        if (field.ValueKind == JsonValueKind.Undefined) throw new SecretIntegrityException("1Password item has no password field.");
        return Encoding.UTF8.GetBytes(field.GetProperty("value").GetString() ?? string.Empty);
    }

    public override async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        var record = await FindAsync(scope, name, ct);
        if (record is null) return false;
        using var response = await SendAsync(HttpMethod.Delete, Root + "/" + Uri.EscapeDataString(record.Id), null, false, ct);
        _ids.TryRemove(Title(scope, name), out _);
        Clear(scope, name);
        ClearUsage(scope, name);
        return true;
    }

    private SecretRecord ToRecord(SecretScope scope, string name, JsonElement item)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var number = item.GetProperty("version").GetInt32();
        if (number is < 1 or > 10000)
            throw new SecretProviderUnavailableException("1Password returned an invalid item version.");
        var changed = Date(item, "updatedAt", now);
        var versions = Enumerable.Range(1, number).Select(version => new SecretVersionRecord
        {
            Version = version, CreatedAt = version == 1 ? Date(item, "createdAt", now) : changed,
            CreatedBy = "external", Origin = version == 1 ? SecretAuditActions.Put : SecretAuditActions.Rotate,
            RevokedAt = version == number ? null : changed, RevokedBy = version == number ? null : "external"
        }).ToList();
        return WithUsage(Record(scope, name, item.GetProperty("id").GetString()!, Date(item, "createdAt", now), changed, versions));
    }
}
