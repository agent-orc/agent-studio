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
/// items/&lt;id&gt;/v000001.sealed.json  immutable, one per version (envelope encrypted)
/// items/&lt;id&gt;/metadata.json        replaced atomically, authenticated with an HMAC
/// audit/&lt;yyyy-MM&gt;.jsonl            append-only audit (see SecretAuditLog)
/// </code>
/// A version file is durable before the metadata names it, so a crash leaves
/// at most an unreferenced version file. The key-encryption key is never part
/// of this directory; a restore needs the same key from its own location.
/// </summary>
public sealed class BuiltInSecretStore : ISecretStore
{
    public const string Id = "builtin";
    private const int MetadataFormat = 1;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
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

    public async Task<IReadOnlyList<SecretRecord>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(ItemsRoot)) return [];
            var records = new List<SecretRecord>();
            foreach (var directory in Directory.EnumerateDirectories(ItemsRoot))
            {
                var id = Path.GetFileName(directory);
                if (id.Contains('.', StringComparison.Ordinal)) continue;
                if (ReadMetadata(id) is { } record) records.Add(record);
            }
            return records.OrderBy(item => item.Scope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
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

            var kek = _keys.Load(createIfMissing: true);
            var sealedVersion = SecretEnvelope.Seal(kek, id, version, value.Span);
            var directory = EnsureDirectory(Path.Combine(ItemsRoot, id));
            WriteAtomically(Path.Combine(directory, VersionFileName(version)),
                JsonSerializer.SerializeToUtf8Bytes(sealedVersion, Json));
            WriteMetadata(kek, next);
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
                WriteMetadata(_keys.Load(createIfMissing: false), next);
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
            if (record.Versions.All(item => item.Version != version))
                throw new SecretNotFoundException(new SecretReference(scope, name, version).ToString());
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
        {
            if (Directory.Exists(path)) continue;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, OwnerOnlyDirectory);
        }
        return directory;
    }

    internal static void WriteAtomically(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;
        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record MetadataEnvelope(int Format, string KeyId, string Payload, string Mac);
}
