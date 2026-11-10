using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Management;

/// <summary>First archive member: what the archive holds and how to put it back.</summary>
public sealed record BackupManifest(
    int Format,
    string Id,
    string CreatedAt,
    string StudioVersion,
    string Host,
    string KeyId,
    string? SecretKeyId,
    IReadOnlyList<BackupManifestSource> Sources,
    IReadOnlyList<BackupCheckout> Checkouts,
    IReadOnlyList<string> NotIncluded);

public sealed record BackupManifestSource(string Name, string Path, bool Projects, bool Present);

/// <summary>Last archive member: every file with its hash, and how consistent the capture was.</summary>
public sealed record BackupContents(IReadOnlyList<BackupContentEntry> Files, BackupConsistency Consistency,
    IReadOnlyDictionary<string, int>? CredentialFiles = null);

public sealed record BackupContentEntry(string Path, long Size, string Sha256, string Capture);

public sealed record BackupConsistency(
    bool WritesHeld,
    bool WritesDrained,
    long WritesHeldMilliseconds,
    bool HoldReleasedEarly,
    int SqliteSnapshots,
    int JournalPrefixes,
    int Retried,
    int Vanished,
    int KeyFilesExcluded,
    int SymlinksSkipped,
    IReadOnlyList<string> Unsettled);

/// <summary>
/// Archive layout inside the encryption envelope: a gzip-compressed PAX tar.
/// <code>
/// studio-backup/manifest.json        first member
/// studio-backup/bundles/N.bundle     refs that exist only on this host
/// &lt;source&gt;/&lt;relative path&gt;           files and directories of each source
/// studio-backup/contents.json        last member: file list with SHA-256
/// </code>
/// </summary>
public static class BackupArchive
{
    public const int Format = 1;
    public const string ManifestMember = "studio-backup/manifest.json";
    public const string ContentsMember = "studio-backup/contents.json";
    public const string BundlePrefix = "studio-backup/bundles/";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static TarWriter OpenWriter(Stream encrypted)
        => new(new GZipStream(encrypted, CompressionLevel.Fastest, leaveOpen: false), TarEntryFormat.Pax, leaveOpen: false);

    public static void WriteJson<T>(TarWriter writer, string member, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        using var data = new MemoryStream(bytes);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, member) { DataStream = data, Mode = OwnerOnly });
    }

    public static void WriteFile(TarWriter writer, string member, string spool, DateTimeOffset modified, UnixFileMode? mode)
    {
        using var data = File.OpenRead(spool);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, member)
        {
            DataStream = data, ModificationTime = modified, Mode = mode ?? OwnerOnly,
        });
    }

    public static void WriteDirectory(TarWriter writer, string member)
        => writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, member.TrimEnd('/') + "/")
        {
            Mode = OwnerOnly | UnixFileMode.UserExecute,
        });

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Streams a decrypted archive. Each regular member is handed to
    /// <paramref name="onFile"/> with its data stream; the manifest must come
    /// first and the content list last, and every file hash is checked against
    /// that list. Returns the manifest and content list on success.
    /// </summary>
    public static (BackupManifest Manifest, BackupContents Contents) Read(
        Stream archive, BackupKey key,
        Action<BackupManifest>? onManifest,
        Action<string, Stream, UnixFileMode>? onFile,
        Action<string>? onDirectory)
    {
        using var decrypted = BackupCipher.Decrypt(archive, key);
        using var gzip = new GZipStream(decrypted, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        BackupManifest? manifest = null;
        BackupContents? contents = null;
        var hashes = new Dictionary<string, (long Size, string Sha256)>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            var name = entry.Name;
            if (contents is not null)
                throw new BackupException("backup-corrupt", "The archive has members after its content list.");
            ValidateMemberName(name);
            if (manifest is null)
            {
                if (name != ManifestMember || entry.DataStream is null)
                    throw new BackupException("backup-corrupt", "The archive does not start with its manifest.");
                manifest = JsonSerializer.Deserialize<BackupManifest>(entry.DataStream, Json)
                    ?? throw new BackupException("backup-corrupt", "The manifest is empty.");
                if (manifest.Format != Format)
                    throw new BackupException("backup-format-unknown", $"Archive format {manifest.Format} is not supported.");
                onManifest?.Invoke(manifest);
                continue;
            }
            if (entry.EntryType == TarEntryType.Directory)
            {
                onDirectory?.Invoke(name.TrimEnd('/'));
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new BackupException("backup-corrupt", $"Unexpected archive member type {entry.EntryType} for {name}.");
            if (name == ContentsMember)
            {
                contents = JsonSerializer.Deserialize<BackupContents>(entry.DataStream!, Json)
                    ?? throw new BackupException("backup-corrupt", "The content list is empty.");
                continue;
            }
            using var hashing = new HashingReadStream(entry.DataStream ?? Stream.Null);
            if (onFile is null) hashing.CopyTo(Stream.Null);
            else
            {
                onFile(name, hashing, entry.Mode);
                hashing.CopyTo(Stream.Null);
            }
            hashes[name] = (hashing.BytesRead, hashing.Hash());
        }
        // Read to the authenticated final chunk, so a cut after the tar end is noticed too.
        gzip.CopyTo(Stream.Null);
        decrypted.CopyTo(Stream.Null);
        if (manifest is null) throw new BackupException("backup-corrupt", "The archive is empty.");
        if (contents is null) throw new BackupException("backup-truncated", "The archive has no content list.");
        foreach (var file in contents.Files)
        {
            if (!hashes.Remove(file.Path, out var actual))
                throw new BackupException("backup-corrupt", $"The archive lacks {file.Path}.");
            if (actual.Size != file.Size || !string.Equals(actual.Sha256, file.Sha256, StringComparison.Ordinal))
                throw new BackupException("backup-corrupt", $"{file.Path} does not match its recorded hash.");
        }
        if (hashes.Count > 0)
            throw new BackupException("backup-corrupt", $"The archive holds {hashes.Count} unlisted members.");
        return (manifest, contents);
    }

    /// <summary>Rejects absolute names and parent segments, so no member can escape a restore root.</summary>
    public static void ValidateMemberName(string name)
    {
        var trimmed = name.TrimEnd('/');
        if (trimmed.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains('\0')
            || trimmed.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new BackupException("backup-corrupt", $"Archive member name '{name}' is not allowed.");
    }

    /// <summary>Counts and hashes what passes through, for the ciphertext checksum.</summary>
    public sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long BytesWritten { get; private set; }

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _hash.AppendData(buffer);
            BytesWritten += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long BytesRead { get; private set; }

        public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _hash.AppendData(buffer[..read]);
            BytesRead += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
