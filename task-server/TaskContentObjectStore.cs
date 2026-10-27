using System.Security.Cryptography;

namespace AgentStudio.TaskServer;

/// <summary>
/// Content-addressed blob store for task files and run artifacts whose content
/// is larger than the inline limit (pipeline delivery target, section 05
/// "Task-folder artefacts", AGT-3320). A row keeps the SHA-256 and size; the
/// bytes live at <c>&lt;root&gt;/sha256/&lt;aa&gt;/&lt;bb&gt;/&lt;sha256&gt;</c>.
/// Objects are immutable and written once: a second write of the same hash is
/// a no-op, so concurrent writers and retries need no coordination. Every read
/// verifies the hash, so a damaged object is reported, never served.
/// </summary>
public sealed class TaskContentObjectStore(string root, long inlineLimitBytes)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Content of at most this many bytes stays inline in its SQLite row.</summary>
    public long InlineLimitBytes { get; } = Math.Max(0, inlineLimitBytes);

    public bool Spills(long sizeBytes) => sizeBytes > InlineLimitBytes;

    public static string Sha256(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public string PathFor(string sha256)
    {
        var hash = NormalizeHash(sha256);
        return Path.Combine(Root, "sha256", hash[..2], hash[2..4], hash);
    }

    public bool Contains(string sha256) => File.Exists(PathFor(sha256));

    /// <summary>Writes <paramref name="content"/> under its hash; the caller's hash must match.</summary>
    public void Put(string sha256, byte[] content)
    {
        var hash = NormalizeHash(sha256);
        if (!string.Equals(Sha256(content), hash, StringComparison.Ordinal))
            throw new ArgumentException("Object content does not match its SHA-256.");
        var target = PathFor(hash);
        if (File.Exists(target) && new FileInfo(target).Length == content.LongLength) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Reads and verifies one object; <see cref="InvalidDataException"/> when it is missing or damaged.</summary>
    public byte[] Read(string sha256)
    {
        var hash = NormalizeHash(sha256);
        var path = PathFor(hash);
        if (!File.Exists(path))
            throw new InvalidDataException($"Content object {hash} is missing from {Root}.");
        var content = File.ReadAllBytes(path);
        if (!string.Equals(Sha256(content), hash, StringComparison.Ordinal))
            throw new InvalidDataException($"Content object {hash} does not match its hash.");
        return content;
    }

    private static string NormalizeHash(string sha256)
    {
        var hash = (sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("A content object is addressed by a 64-digit SHA-256.");
        return hash;
    }
}
