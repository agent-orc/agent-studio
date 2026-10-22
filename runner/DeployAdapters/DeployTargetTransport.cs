using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;

namespace AgentRunner.DeployAdapters;

/// <summary>Resolves only through the claimed deploy job's audited secret-provider route.</summary>
public sealed class DeployWorkloadSecretResolver(
    HttpClient client, string project, string environment, string jobId, string leaseToken) : IDeploySecretResolver
{
    private sealed record SecretResponse(string Value);

    public async Task<string> ResolveAsync(string reference, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("api/runner/secrets/resolve", new
        {
            reference,
            purpose = "deploy:" + environment,
            workload = new { kind = "deploy-job", project, jobId, leaseToken },
        }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SecretResponse>(ct))?.Value
            ?? throw new InvalidDataException("Secret provider returned no value.");
    }
}

internal static class VerifiedPackage
{
    internal const long MaxArchiveBytes = 64 * 1024 * 1024;
    internal const long MaxExtractedBytes = 128 * 1024 * 1024;
    internal const int MaxEntries = 4096;

    public static async Task<string> ExtractAsync(DeployTargetRequest request, DeployTempDirectory temp, CancellationToken ct)
    {
        await DeployArtifactVerifier.VerifyPackageAsync(request, ct);
        var outputRoot = Path.Combine(temp.Path, "package");
        Directory.CreateDirectory(outputRoot);
        await using var archive = File.OpenRead(request.PackagePath!);
        using var gzip = (request.PackageArtifact ?? request.Artifact).StartsWith("release-package@sha256:", StringComparison.Ordinal)
            ? new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true) : null;
        using var reader = new TarReader((Stream?)gzip ?? archive);
        long extractedBytes = 0;
        var entries = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (++entries > MaxEntries)
                throw new InvalidDataException("Release package contains too many entries.");
            var relative = entry.Name.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(relative) || relative.StartsWith('/')
                || Path.IsPathRooted(relative)
                || (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
                || relative.Split('/').Any(segment => segment is "" or "." or ".."))
                throw new InvalidDataException("Release package contains an unsafe path.");
            var path = Path.GetFullPath(Path.Combine(outputRoot, relative));
            var withinRoot = Path.GetRelativePath(outputRoot, path);
            if (withinRoot == "." || withinRoot == ".."
                || withinRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(withinRoot))
                throw new InvalidDataException("Release package contains an unsafe path.");
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                if (entry.Length < 0 || entry.Length > MaxExtractedBytes - extractedBytes)
                    throw new InvalidDataException("Release package exceeds the extraction limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var output = File.Create(path);
                if (entry.DataStream is not null)
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await entry.DataStream.ReadAsync(buffer, ct)) != 0)
                    {
                        if (read > MaxExtractedBytes - extractedBytes)
                            throw new InvalidDataException("Release package exceeds the extraction limit.");
                        extractedBytes += read;
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
            }
            else throw new InvalidDataException("Release package contains an unsupported entry.");
        }
        return outputRoot;
    }

    public static string RelativePath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains('\\')
            || relative.Split('/').Any(segment => segment is "" or "." or ".."
                || segment.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))))
            throw new InvalidDataException("Package path is invalid.");
        var path = Path.Combine(root, relative);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new InvalidDataException("Package path is missing.");
        return path;
    }
}
