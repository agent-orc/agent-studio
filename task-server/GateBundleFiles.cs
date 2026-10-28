using System.Security.Cryptography;

namespace AgentStudio.TaskServer;

/// <summary>
/// Immutable, content-addressed gate source bundles (DP-S4). A bundle lives at
/// <c>&lt;root&gt;/gate-bundles/&lt;aa&gt;/&lt;sha256&gt;.bundle</c>. It is
/// streamed to a temporary file, bounded by its declared size, hashed while it
/// is written, and moved into place only when size and digest both match.
/// A second upload of the same digest leaves the first file untouched.
/// </summary>
internal static class GateBundleFiles
{
    public static string PathFor(string root, string sha256)
        => Path.Combine(root, "gate-bundles", sha256[..2], sha256 + ".bundle");

    /// <summary>Writes the bundle once; throws when the stream breaks its declared size or digest.</summary>
    public static async Task WriteAsync(string root, string sha256, long declaredBytes,
        Stream content, CancellationToken ct)
    {
        var target = PathFor(root, sha256);
        if (File.Exists(target) && new FileInfo(target).Length == declaredBytes) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > declaredBytes)
                        throw new TaskServerConflictException("gate-bundle-size-mismatch",
                            "Gate bundle exceeds its declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            if (written != declaredBytes)
                throw new TaskServerConflictException("gate-bundle-size-mismatch",
                    "Gate bundle is shorter than its declared size.");
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.Ordinal))
                throw new TaskServerConflictException("gate-bundle-digest-mismatch",
                    "Gate bundle does not match its declared SHA-256 digest.");
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
