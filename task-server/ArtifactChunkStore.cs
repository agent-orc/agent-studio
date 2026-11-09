using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

/// <summary>Durable, digest-addressed staging for proxy-sized artifact requests.</summary>
public sealed class ArtifactChunkStore(IOptions<TaskServerOptions> configured)
{
    public const int MaxChunkBytes = ArtifactChunkProtocol.MaxChunkBytes;
    private readonly TaskServerOptions _options = configured.Value;

    public ArtifactChunkStatus Status(string runId, string sha256, int count, long sizeBytes)
    {
        ValidateManifest(sha256, count, sizeBytes);
        var directory = DirectoryPath(runId, sha256);
        var received = Enumerable.Range(0, count)
            .Where(index => File.Exists(PartPath(directory, index))).ToArray();
        return new ArtifactChunkStatus(received, count, sizeBytes);
    }

    public ArtifactChunkStatus Put(string runId, ArtifactChunkRequest request)
    {
        ValidateManifest(request.Sha256, request.Count, request.SizeBytes);
        if (request.Index < 0 || request.Index >= request.Count)
            throw new ArgumentException("Artifact chunk index is outside the manifest.");
        if (request.ContentBase64 is null)
            throw new ArgumentException("Artifact chunk content is required.");
        if (request.ContentBase64.Length > (MaxChunkBytes + 2) / 3 * 4 + 4)
            throw new ArtifactChunkTooLargeException(MaxChunkBytes);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ContentBase64); }
        catch (FormatException) { throw new ArgumentException("Artifact chunk is not valid base64."); }
        if (bytes.Length > MaxChunkBytes)
            throw new ArtifactChunkTooLargeException(MaxChunkBytes);
        PruneStale();
        var directory = DirectoryPath(runId, request.Sha256);
        Directory.CreateDirectory(directory);
        var part = PartPath(directory, request.Index);
        if (File.Exists(part))
        {
            if (!File.ReadAllBytes(part).AsSpan().SequenceEqual(bytes))
                throw new ArgumentException("Artifact chunk replay differs from the staged bytes.");
        }
        else
        {
            var temporary = part + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                try { File.Move(temporary, part); }
                catch (IOException) when (File.Exists(part))
                {
                    if (!File.ReadAllBytes(part).AsSpan().SequenceEqual(bytes))
                        throw new ArgumentException("Artifact chunk replay differs from the staged bytes.");
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return Status(runId, request.Sha256, request.Count, request.SizeBytes);
    }

    public async Task<ArtifactDto> CompleteAsync(
        string runId, ArtifactChunkCompleteRequest request, TaskServerStore store, string actor, CancellationToken ct)
    {
        ValidateManifest(request.Sha256, request.Count, request.SizeBytes);
        var content = await AssembleAsync(runId, request.Sha256, request.Count, request.SizeBytes, ct);
        var result = await store.IngestArtifactBytesAsync(runId, new ArtifactIngestRequest(
            request.ArtifactId, request.Name, request.MediaType,
            string.Empty,
            request.Sha256, request.IdempotencyKey, request.Fence,
            request.RunnerId, request.InstanceId, request.LeaseId, request.Sequence), content, actor, ct);
        try { Directory.Delete(DirectoryPath(runId, request.Sha256), recursive: true); }
        catch (IOException) { /* The fenced artifact is durable; stale chunks are pruned later. */ }
        catch (UnauthorizedAccessException) { /* Keep the acknowledgement after the durable write. */ }
        return result;
    }

    public async Task<byte[]> AssembleAsync(string runId, string sha256, int count, long sizeBytes, CancellationToken ct)
    {
        ValidateManifest(sha256, count, sizeBytes);
        var directory = DirectoryPath(runId, sha256);
        using var output = new MemoryStream((int)sizeBytes);
        for (var index = 0; index < count; index++)
        {
            var path = PartPath(directory, index);
            if (!File.Exists(path)) throw new ArgumentException($"Artifact chunk {index} is missing.");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length > MaxChunkBytes || output.Length + bytes.Length > sizeBytes)
                throw new ArgumentException("Artifact chunks exceed the declared size.");
            await output.WriteAsync(bytes, ct);
        }
        if (output.Length != sizeBytes ||
            !Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)))
                .Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Artifact chunks do not match the declared size and SHA-256.");
        return output.GetBuffer();
    }

    private void ValidateManifest(string sha256, int count, long sizeBytes)
    {
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Artifact SHA-256 is invalid.");
        if (sizeBytes < 0 || sizeBytes > _options.ResultArtifactMaxFileBytes
            || sizeBytes > _options.ResultArtifactMaxTotalBytes)
            throw new ArtifactChunkTooLargeException(Math.Min(_options.ResultArtifactMaxFileBytes,
                _options.ResultArtifactMaxTotalBytes));
        if (sizeBytes > int.MaxValue || count < 1
            || count != Math.Max(1, (sizeBytes + MaxChunkBytes - 1) / MaxChunkBytes))
            throw new ArgumentException("Artifact chunk count does not match the declared size.");
    }

    private string DirectoryPath(string runId, string sha256)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId + ":" + sha256)))
            .ToLowerInvariant();
        return Path.Combine(_options.ResolveDataDirectory(), "artifact-chunks", key);
    }

    private void PruneStale()
    {
        var root = Path.Combine(_options.ResolveDataDirectory(), "artifact-chunks");
        if (!Directory.Exists(root)) return;
        var expiredBefore = DateTime.UtcNow.AddDays(-1);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < expiredBefore)
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { /* A concurrent upload owns this staging directory. */ }
            catch (UnauthorizedAccessException) { /* Keep the upload path available. */ }
        }
    }

    private static string PartPath(string directory, int index)
        => Path.Combine(directory, index.ToString("D6") + ".part");
}

public sealed class ArtifactChunkTooLargeException(long limit) : Exception
{
    public long Limit { get; } = limit;
}
