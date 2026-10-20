using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>
/// The built-in encrypted store in the workspace's owner-only
/// <c>.security/secrets</c> directory. Layout, chosen so a file-level backup
/// taken at any moment restores consistently:
/// <code>
/// items/&lt;id&gt;/v000001.sealed.json  immutable, one per active version (envelope encrypted)
/// items/&lt;id&gt;/metadata.json        replaced atomically, authenticated with an HMAC
/// audit/&lt;yyyy-MM&gt;.jsonl            append-only audit (see SecretAuditLog)
/// </code>
/// A version file is durable before the metadata names it, so a crash leaves
/// at most an unreferenced version file. A revoked version's file is deleted
/// once the metadata records the revocation, so restoring an older metadata
/// file cannot make that version resolvable again. The key-encryption key is
/// never part of this directory; a restore needs the same key from its own
/// location. The store creates a key only while it holds no item, and seals a
/// new name only under a key that one of its items names.
/// </summary>
public sealed class BuiltInSecretStore : ISecretStore
{
    public const string Id = "builtin";
    private const int MetadataFormat = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly IKeyEncryptionKeySource _keys;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BuiltInSecretStore(IConfiguration configuration, IKeyEncryptionKeySource keys)
        : this(SecretStorePaths.DataRoot(configuration), keys)
    {
    }

    public BuiltInSecretStore(string root, IKeyEncryptionKeySource keys)
    {
        _root = root;
        _keys = keys;
    }

    public string StoreId => Id;
    public string Root => _root;
    private string ItemsRoot => Path.Combine(_root, "items");

    public static string SecretId(SecretScope scope, string name)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope.Key.ToLowerInvariant() + "\n" + name)))[..32];

    public static string VersionFileName(int version)
        => "v" + version.ToString("D6", CultureInfo.InvariantCulture) + ".sealed.json";

    public async Task<SecretListing> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var records = new List<SecretRecord>();
            var unreadable = new List<UnreadableSecret>();
            foreach (var id in ItemIds())
            {
                // One item that fails its key or integrity check is flagged; the rest still list.
                try
                {
                    if (ReadMetadata(id) is { } record) records.Add(record);
                }
                catch (SecretKeyUnavailableException ex) { unreadable.Add(Unreadable(id, UnreadableSecret.KeyUnavailable, ex.Message)); }
                catch (SecretIntegrityException ex) { unreadable.Add(Unreadable(id, UnreadableSecret.IntegrityFailed, ex.Message)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The exception message names absolute paths; viewers get the type only.
                    unreadable.Add(Unreadable(id, UnreadableSecret.ReadFailed, "The item could not be read: " + ex.GetType().Name + "."));
                }
            }
            return new SecretListing(
                records.OrderBy(item => item.Scope, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.Ordinal).ToList(),
                unreadable.OrderBy(item => item.Id, StringComparer.Ordinal).ToList());
        }
        finally { _gate.Release(); }
    }

    public async Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadMetadata(SecretId(scope, name)); }
        finally { _gate.Release(); }
    }

    public async Task<SecretRecord> AppendVersionAsync(
        SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = SecretId(scope, name);
            var current = ReadMetadata(id);
            var next = transition(current) with { Id = id, Scope = scope.Key, Name = name };
            var version = next.Versions.Max(item => item.Version);
            if (current is not null && version <= current.Versions.Max(item => item.Version))
                throw new InvalidOperationException("A new secret version must be newer than every stored version.");

            // A key is created only for an empty store; once items exist only the key that sealed them may open them.
            var existing = ItemIds().ToList();
            var kek = _keys.Load(createIfMissing: existing.Count == 0);
            if (current is null) RequireKeyOfExistingItems(kek, existing);
            var sealedVersion = SecretEnvelope.Seal(kek, id, version, value.Span);
            var directory = EnsureDirectory(Path.Combine(ItemsRoot, id));
            WriteAtomically(Path.Combine(directory, VersionFileName(version)),
                JsonSerializer.SerializeToUtf8Bytes(sealedVersion, Json));
            WriteMetadata(kek, next);
            DeleteRevokedVersionFiles(next);
            return next;
        }
        finally { _gate.Release(); }
    }

    public async Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = SecretId(scope, name);
            var current = ReadMetadata(id) ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
            var next = transition(current) with { Id = id, Scope = scope.Key, Name = name };
            if (!ReferenceEquals(next, current) && next != current)
            {
                WriteMetadata(_keys.Load(createIfMissing: false), next);
                DeleteRevokedVersionFiles(next);
            }
            return next;
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = SecretId(scope, name);
            var record = ReadMetadata(id) ?? throw new SecretNotFoundException(new SecretReference(scope, name).ToString());
            var stored = record.Versions.FirstOrDefault(item => item.Version == version)
                         ?? throw new SecretNotFoundException(new SecretReference(scope, name, version).ToString());
            // Checked under the store lock: a revocation that won the race since the caller picked the version stops issuance.
            if (!stored.Active) throw new SecretRevokedException(new SecretReference(scope, name, version).ToString());
            var path = Path.Combine(ItemsRoot, id, VersionFileName(version));
            if (!File.Exists(path))
                throw new SecretIntegrityException($"Version {version} is named by the metadata but its sealed file is missing.");
            SealedSecretVersion? sealedVersion;
            try { sealedVersion = JsonSerializer.Deserialize<SealedSecretVersion>(File.ReadAllBytes(path), Json); }
            catch (JsonException ex) { throw new SecretIntegrityException("The sealed version file is not valid: " + ex.Message); }
            if (sealedVersion is null) throw new SecretIntegrityException("The sealed version file is empty.");
            return SecretEnvelope.Open(_keys.Load(createIfMissing: false), sealedVersion, id, version);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var directory = Path.Combine(ItemsRoot, SecretId(scope, name));
            if (!Directory.Exists(directory)) return false;
            // Rename first so a crash never leaves metadata pointing at half-deleted versions.
            var tombstone = directory + ".deleted-" + Guid.NewGuid().ToString("N");
            Directory.Move(directory, tombstone);
            Directory.Delete(tombstone, recursive: true);
            return true;
        }
        finally { _gate.Release(); }
    }

    private IEnumerable<string> ItemIds()
        => Directory.Exists(ItemsRoot)
            ? Directory.EnumerateDirectories(ItemsRoot).Select(Path.GetFileName).OfType<string>()
                .Where(id => !id.Contains('.', StringComparison.Ordinal))
            : [];

    /// <summary>
    /// Refuses to seal a new name under a valid but wrong key (stale or
    /// mistyped environment key): when the store holds sealed items, at least
    /// one of them must name the loaded key, or the store would fork.
    /// </summary>
    private void RequireKeyOfExistingItems(KeyEncryptionKey kek, IReadOnlyList<string> ids)
    {
        var sealedKeys = ids.Select(MetadataKeyId).OfType<string>().ToList();
        if (sealedKeys.Count == 0 || sealedKeys.Contains(kek.Id, StringComparer.Ordinal)) return;
        throw new SecretKeyUnavailableException(
            $"The configured key-encryption key {kek.Id} sealed none of the {sealedKeys.Count} stored items "
            + $"(they name {string.Join(", ", sealedKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}). "
            + "A new secret is not sealed under it, so the store does not fork: configure the key that sealed the items.");
    }

    /// <summary>The key id an item's metadata names, unverified; null when the item has no readable metadata.</summary>
    private string? MetadataKeyId(string id)
    {
        var path = Path.Combine(ItemsRoot, id, "metadata.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MetadataEnvelope>(File.ReadAllBytes(path), Json)?.KeyId is { Length: > 0 } keyId ? keyId : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Scope and name come from the unverified payload, for display only; either may be null.</summary>
    private UnreadableSecret Unreadable(string id, string problem, string reason)
    {
        string? scope = null, name = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<MetadataEnvelope>(File.ReadAllBytes(Path.Combine(ItemsRoot, id, "metadata.json")), Json);
            if (envelope?.Payload is { Length: > 0 } payload
                && JsonSerializer.Deserialize<UnverifiedIdentity>(payload, PayloadJson) is { } claimed)
            {
                scope = SecretScope.TryParse(claimed.Scope, out var parsed) ? parsed.Key : null;
                name = SecretReference.IsValidName(claimed.Name) ? claimed.Name : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            reason += " Its scope and name cannot be read either: " + ex.GetType().Name + ".";
        }
        return new UnreadableSecret(id, scope, name, problem, reason);
    }

    /// <summary>Removes the sealed file of every revoked version after the metadata recorded the revocation.</summary>
    private void DeleteRevokedVersionFiles(SecretRecord record)
    {
        foreach (var version in record.Versions.Where(item => !item.Active))
        {
            var path = Path.Combine(ItemsRoot, record.Id, VersionFileName(version.Version));
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private SecretRecord? ReadMetadata(string id)
    {
        var path = Path.Combine(ItemsRoot, id, "metadata.json");
        if (!File.Exists(path)) return null;
        MetadataEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<MetadataEnvelope>(File.ReadAllBytes(path), Json); }
        catch (JsonException ex) { throw new SecretIntegrityException($"Secret metadata {id} is not valid JSON: {ex.Message}"); }
        if (envelope is null || envelope.Format != MetadataFormat || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Mac))
            throw new SecretIntegrityException($"Secret metadata {id} has an unsupported shape.");
        var kek = _keys.Load(createIfMissing: false);
        if (!string.Equals(envelope.KeyId, kek.Id, StringComparison.Ordinal))
            throw new SecretKeyUnavailableException(
                $"Secret metadata {id} was written with key-encryption key {envelope.KeyId}; the configured key is {kek.Id}.");
        byte[] expected;
        try { expected = Convert.FromBase64String(envelope.Mac); }
        catch (FormatException) { throw new SecretIntegrityException($"Secret metadata {id} carries a malformed MAC."); }
        if (!CryptographicOperations.FixedTimeEquals(Mac(kek, id, envelope.Payload), expected))
            throw new SecretIntegrityException($"Secret metadata {id} failed its integrity check.");
        var record = JsonSerializer.Deserialize<SecretRecord>(envelope.Payload, PayloadJson)
            ?? throw new SecretIntegrityException($"Secret metadata {id} is empty.");
        if (!string.Equals(record.Id, id, StringComparison.Ordinal))
            throw new SecretIntegrityException($"Secret metadata {id} names another secret.");
        return record;
    }

    private void WriteMetadata(KeyEncryptionKey kek, SecretRecord record)
    {
        var payload = JsonSerializer.Serialize(record, PayloadJson);
        var envelope = new MetadataEnvelope(MetadataFormat, kek.Id, payload, Convert.ToBase64String(Mac(kek, record.Id, payload)));
        var directory = EnsureDirectory(Path.Combine(ItemsRoot, record.Id));
        WriteAtomically(Path.Combine(directory, "metadata.json"), JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
    }

    private static byte[] Mac(KeyEncryptionKey kek, string id, string payload)
    {
        var key = kek.DeriveMetadataKey();
        try { return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(id + "\n" + payload)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private string EnsureDirectory(string directory)
    {
        foreach (var path in new[] { _root, ItemsRoot, directory })
            OwnerOnlyAccess.CreateDirectory(path);
        return directory;
    }

    internal static void WriteAtomically(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        OwnerOnlyAccess.ApplyCreateMode(options);
        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record MetadataEnvelope(int Format, string KeyId, string Payload, string Mac);
    private sealed record UnverifiedIdentity(string? Scope, string? Name);
}
