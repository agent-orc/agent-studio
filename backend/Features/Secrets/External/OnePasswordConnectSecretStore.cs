using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>
/// 1Password Connect item transport. Connect exposes the current item only: replace is supported,
/// while historical reads, rotate, and version-specific revocation are refused explicitly.
/// </summary>
public sealed class OnePasswordConnectSecretStore(SecretBackendBinding binding, HttpClient http,
    Func<SecretBackendBinding, CancellationToken, Task<string>> credential, TimeProvider clock)
    : HttpSecretStore(binding, http, credential, clock)
{
    public override string StoreId => "onepassword";
    private string Items => "v1/vaults/" + Uri.EscapeDataString(Binding.VaultId!) + "/items";
    private string Item(string id) => Items + "/" + Uri.EscapeDataString(id);
    private string Title(SecretScope scope, string name) => "agent-studio/" + scope.Key + "/" + name;

    public override async Task HealthAsync(CancellationToken ct)
    {
        using var _ = await RequestAsync(HttpMethod.Get, Items, null, ct);
    }

    private async Task<List<(string Id, string Name)>> Summaries(CancellationToken ct)
    {
        using var response = await RequestAsync(HttpMethod.Get, Items, null, ct)
            ?? throw new SecretBackendException("backend-response-invalid", "The selected backend returned an invalid response.");
        var prefix = "agent-studio/" + Binding.Scope + "/";
        var result = new List<(string, string)>();
        foreach (var item in response.RootElement.EnumerateArray())
        {
            var title = RequiredString(item, "title");
            if (title.StartsWith(prefix, StringComparison.Ordinal) && SecretReference.IsValidName(title[prefix.Length..]))
                result.Add((RequiredString(item, "id"), title[prefix.Length..]));
        }
        return result;
    }

    private async Task<(string Id, JsonDocument Detail)?> Detail(SecretScope scope, string name, CancellationToken ct)
    {
        var matches = (await Summaries(ct)).Where(item => item.Name == name).ToList();
        if (matches.Count > 1)
            throw new SecretBackendException("backend-duplicate-item", "The selected backend has duplicate items for this secret.");
        if (matches.Count == 0) return null;
        var detail = await RequestAsync(HttpMethod.Get, Item(matches[0].Id), null, ct)
            ?? throw new SecretBackendException("backend-item-missing", "The selected backend item disappeared.");
        return (matches[0].Id, detail);
    }

    private SecretRecord Record(JsonElement item, SecretScope scope, string name)
    {
        var notes = RequiredString(item, "notesPlain");
        SecretRecord? record;
        try { record = JsonSerializer.Deserialize<SecretRecord>(notes, Json); }
        catch (JsonException) { throw new SecretBackendException("backend-record-invalid", "The selected backend item has invalid Studio metadata."); }
        if (record is null || record.Scope != scope.Key || record.Name != name)
            throw new SecretBackendException("backend-record-invalid", "The selected backend item does not match its scope.");
        var revision = Revision(item);
        if (revision != (record.StoreRevision ?? record.Versions.Max(version => version.Version)))
            throw new SecretBackendException("backend-version-conflict", "The 1Password item revision changed outside Studio.");
        return record with { StoreRevision = revision };
    }

    private static int Revision(JsonElement item)
    {
        if (!item.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var revision)
            || revision < 1)
            throw new SecretBackendException("backend-response-invalid", "The selected backend returned an invalid item revision.");
        return revision;
    }

    public override async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        var scope = SecretScope.Parse(Binding.Scope);
        var records = new List<SecretRecord>();
        var unreadable = new List<UnreadableSecret>();
        foreach (var (_, name) in await Summaries(ct))
        {
            try { if (await FindAsync(scope, name, ct) is { } record) records.Add(record); }
            catch (SecretBackendException ex)
            {
                unreadable.Add(new("onepassword:" + scope.Key + "/" + name, scope.Key, name,
                    UnreadableSecret.ReadFailed, ex.Message));
            }
        }
        return new(records, unreadable);
    }

    public override async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
    {
        var detail = await Detail(scope, name, ct);
        if (detail is null) return null;
        using var document = detail.Value.Detail;
        return Record(document.RootElement, scope, name);
    }

    public override async Task<SecretRecord> AppendVersionAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct);
        try
        {
            var detail = await Detail(scope, name, ct);
            using var old = detail?.Detail;
            var current = old is null ? null : Record(old.RootElement, scope, name);
            var next = transition(current) with { Id = BuiltInSecretStore.SecretId(scope, name), Scope = scope.Key, Name = name };
            // The Connect API cannot retrieve past item revisions. A put replaces the current value.
            if (current is not null && next.Versions.Any(item => item.Active && item.Version != current.Versions.Max(v => v.Version) + 1))
                throw new SecretBackendException("backend-rotation-unsupported", "1Password Connect cannot retain readable historical versions; use Replace.");
            var expectedRevision = checked((current?.StoreRevision ?? 0) + 1);
            var valueVersion = next.Versions.Max(item => item.Version);
            next = next with
            {
                StoreRevision = expectedRevision,
                Versions = next.Versions.Select(item => item.Version == valueVersion
                    ? item with { Version = expectedRevision } : item).ToList()
            };
            var payload = new
            {
                vault = new { id = Binding.VaultId },
                title = Title(scope, name),
                category = "SECURE_NOTE",
                notesPlain = JsonSerializer.Serialize(next, Json),
                fields = new[] { new { id = "value", label = "value", type = "CONCEALED", value = Encoding.UTF8.GetString(value.Span) } }
            };
            using var response = await RequestAsync(detail is null ? HttpMethod.Post : HttpMethod.Put,
                detail is null ? Items : Item(detail.Value.Id), payload, ct);
            if (response is null || Revision(response.RootElement) != expectedRevision)
                throw new SecretBackendException("backend-version-conflict", "The 1Password item revision changed during the write.");
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
            var detail = await Detail(scope, name, ct)
                ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
            using var document = detail.Detail;
            var current = Record(document.RootElement, scope, name);
            var next = transition(current);
            if (next.Versions.Any(item => current.Versions.Any(old => old.Version == item.Version && old.Active && !item.Active)))
                throw new SecretBackendException("backend-revoke-unsupported", "1Password Connect cannot revoke an individual item revision; delete the item instead.");
            if (next == current) return current;
            var expectedRevision = checked(current.StoreRevision!.Value + 1);
            next = next with { StoreRevision = expectedRevision };
            // JSON Patch changes only Studio's notes; the concealed value field stays in Connect.
            using var response = await RequestAsync(HttpMethod.Patch, Item(detail.Id),
                new[] { new { op = "replace", path = "/notesPlain", value = JsonSerializer.Serialize(next, Json) } }, ct);
            if (response is null || Revision(response.RootElement) != expectedRevision)
                throw new SecretBackendException("backend-version-conflict", "The 1Password item revision changed during the update.");
            return next;
        }
        finally { WriteGate.Release(); }
    }

    protected override async Task<byte[]> FetchValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        var detail = await Detail(scope, name, ct)
            ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
        using var document = detail.Detail;
        var record = Record(document.RootElement, scope, name);
        if (SecretVersionPolicy.Select(record, version).Version != record.Versions.Max(item => item.Version))
            throw new SecretBackendException("backend-version-unsupported", "1Password Connect cannot read historical item revisions.");
        foreach (var field in Required(document.RootElement, "fields").EnumerateArray())
            if (RequiredString(field, "id") == "value") return Encoding.UTF8.GetBytes(RequiredString(field, "value"));
        throw new SecretBackendException("backend-record-invalid", "The selected backend item has no value field.");
    }

    public override async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        var detail = await Detail(scope, name, ct);
        if (detail is null) return false;
        detail.Value.Detail.Dispose();
        using (await RequestAsync(HttpMethod.Delete, Item(detail.Value.Id), null, ct)) { }
        Invalidate(scope, name);
        return true;
    }
}
