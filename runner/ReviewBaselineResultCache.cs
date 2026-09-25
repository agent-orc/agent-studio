using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// Identity of one cached baseline verify result. These are exactly the inputs
/// the review grade already records for a baseline-compared command: the
/// repository, the resolved merge-base SHA, the verify command line, and the
/// toolchain that executed it.
/// </summary>
internal sealed record ReviewBaselineCacheKey(
    string RepositoryId,
    string BaselineSha,
    string CommandHash,
    string ToolchainFingerprint);

/// <summary>
/// One baseline verify result on disk: the parsed failure list used for
/// classification plus the complete process streams, so a reused baseline is
/// as citable in the grade as a freshly executed one.
/// </summary>
internal sealed record BaselineCacheEntry(
    int ParserVersion,
    string RepositoryId,
    string BaselineSha,
    string CommandHash,
    string ToolchainFingerprint,
    string AttemptId,
    string HeadSha,
    string TreeSha,
    int ExitCode,
    IReadOnlyList<string> Failures,
    string StdOut,
    string StdErr,
    DateTime CreatedAt);

internal sealed record ReviewBaselineCachePruneResult(
    int InspectedBaselines,
    int RemovedOffBranch,
    int RemovedExpired,
    int Failed);

/// <summary>
/// Per-host store of baseline verify results (AGT-2843). After a candidate
/// verify failure the executor re-runs the same command on the merge-base to
/// separate new from pre-existing failures. That second run costs as much as
/// the first, and several attempts on one integration branch share the same
/// baseline SHA within the hour, so the result is stored once per repository,
/// baseline SHA, command and toolchain and reused by every later attempt.
///
/// A hit only ever replaces the baseline run. The candidate command always
/// executes in the attempt's own workspace.
/// </summary>
internal static class ReviewBaselineResultCache
{
    /// <summary>
    /// Bounded lifetime of a cached result. An entry can outlive the facts it
    /// was produced from (host toolchain drift the fingerprint does not see,
    /// an infrastructure change, a flake that has since been fixed), so a
    /// result is never reused indefinitely even while its baseline SHA stays
    /// on the integration branch.
    /// </summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    internal static string RepositoryDirectory(string root, string repositoryId)
        => Path.Combine(root, RemoteReviewWorkspace.HashText(repositoryId));

    internal static string BaselineDirectory(string root, ReviewBaselineCacheKey key)
        => Path.Combine(
            RepositoryDirectory(root, key.RepositoryId),
            RemoteReviewWorkspace.SafeSegment(key.BaselineSha));

    internal static string EntryPath(string root, ReviewBaselineCacheKey key)
        => Path.Combine(
            BaselineDirectory(root, key),
            $"{key.CommandHash}.{Short(key.ToolchainFingerprint)}.json");

    /// <summary>
    /// Pure reuse decision. Every key input is re-checked against the stored
    /// entry, so a file that survives a key change or a parser change is
    /// ignored rather than trusted by its path alone.
    /// </summary>
    internal static bool IsReusable(
        BaselineCacheEntry entry,
        ReviewBaselineCacheKey key,
        int parserVersion,
        DateTime utcNow)
        => entry.ExitCode == 0
           && entry.ParserVersion == parserVersion
           && string.Equals(entry.RepositoryId, key.RepositoryId, StringComparison.Ordinal)
           && string.Equals(entry.BaselineSha, key.BaselineSha, StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.CommandHash, key.CommandHash, StringComparison.Ordinal)
           && string.Equals(entry.ToolchainFingerprint, key.ToolchainFingerprint, StringComparison.Ordinal)
           && !IsExpired(entry, utcNow);

    internal static bool IsExpired(BaselineCacheEntry entry, DateTime utcNow)
        => Age(entry, utcNow) > MaximumAge;

    internal static TimeSpan Age(BaselineCacheEntry entry, DateTime utcNow)
    {
        var createdAt = entry.CreatedAt.Kind == DateTimeKind.Local
            ? entry.CreatedAt.ToUniversalTime()
            : DateTime.SpecifyKind(entry.CreatedAt, DateTimeKind.Utc);
        var age = utcNow - createdAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    internal static async Task<BaselineCacheEntry?> ReadAsync(
        string path,
        ReviewBaselineCacheKey key,
        int parserVersion,
        DateTime utcNow,
        CancellationToken ct)
    {
        var entry = await ReadEntryAsync(path, ct);
        return entry is not null && IsReusable(entry, key, parserVersion, utcNow) ? entry : null;
    }

    internal static async Task WriteAsync(string path, BaselineCacheEntry entry, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: ct);
            await stream.FlushAsync(ct);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Drops what may no longer be reused: every entry past
    /// <see cref="MaximumAge"/>, and every baseline SHA the integration branch
    /// no longer contains (a rebased or force-pushed line leaves results no
    /// future attempt can key against).
    /// </summary>
    internal static async Task<ReviewBaselineCachePruneResult> PruneAsync(
        string root,
        string repositoryId,
        Func<string, CancellationToken, Task<bool>> stillOnIntegrationBranch,
        DateTime utcNow,
        Action<string> log,
        CancellationToken ct)
    {
        var repository = RepositoryDirectory(root, repositoryId);
        if (!Directory.Exists(repository))
            return new ReviewBaselineCachePruneResult(0, 0, 0, 0);

        var inspected = 0;
        var offBranch = 0;
        var expired = 0;
        var failed = 0;
        foreach (var directory in Directory.EnumerateDirectories(repository))
        {
            ct.ThrowIfCancellationRequested();
            var sha = Path.GetFileName(directory);
            if (!IsShaSegment(sha)) continue;
            inspected++;

            if (!await stillOnIntegrationBranch(sha, ct))
            {
                if (TryDeleteDirectory(directory, log))
                {
                    offBranch++;
                    log($"review baseline cache pruned baseline={sha} reason=off-integration-branch");
                }
                else failed++;
                continue;
            }

            var remaining = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var entry = await ReadEntryAsync(file, ct);
                if (entry is not null && !IsExpired(entry, utcNow))
                {
                    remaining++;
                    continue;
                }
                if (TryDeleteFile(file, log))
                {
                    expired++;
                    log($"review baseline cache pruned baseline={sha} entry={Path.GetFileName(file)} reason=expired");
                }
                else failed++;
            }
            if (remaining == 0) TryDeleteDirectory(directory, log);
        }

        var result = new ReviewBaselineCachePruneResult(inspected, offBranch, expired, failed);
        if (result.RemovedOffBranch > 0 || result.RemovedExpired > 0 || result.Failed > 0)
            log(
                $"review baseline cache prune repository={repositoryId} " +
                $"inspected={result.InspectedBaselines} offBranch={result.RemovedOffBranch} " +
                $"expired={result.RemovedExpired} failed={result.Failed} " +
                $"maxAgeHours={MaximumAge.TotalHours:F0}");
        return result;
    }

    private static async Task<BaselineCacheEntry?> ReadEntryAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<BaselineCacheEntry>(stream, cancellationToken: ct);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryDeleteFile(string path, Action<string> log)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"review baseline cache prune failed path={path}: {exception.Message}");
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path, Action<string> log)
    {
        try
        {
            ResilientDirectory.Delete(path);
            return !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"review baseline cache prune failed path={path}: {exception.Message}");
            return false;
        }
    }

    private static bool IsShaSegment(string value)
        => value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit);

    private static string Short(string fingerprint)
        => fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}
