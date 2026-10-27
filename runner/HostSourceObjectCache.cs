using System.Security.Cryptography;
using System.Text;

namespace AgentRunner;

/// <summary>Bounded, disposable Git object copies shared by review and gate slots on one host.</summary>
internal static class HostSourceObjectCache
{
    private const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 8;

    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "agentstudio", "source", "cache");

    internal static async Task<(long Bytes, TimeSpan Ready)> MaterializeAsync(
        string repositoryUrl, string resultRef, string expectedSha, string destination,
        Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<ProcessResult>> git,
        Func<DateTime> utcNow, CancellationToken ct, IReadOnlyList<string>? sparsePaths = null,
        string? cacheRoot = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryUrl)));
        var root = cacheRoot ?? Root;
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, key);
        // The lock file lives outside the evictable entry. Independent daemon
        // processes on the host cannot fetch or evict the same repository at once.
        await using var held = await LockAsync(Path.Combine(root, key + ".lock"), ct);
        if (Directory.Exists(cache) && !await HealthyAsync(cache, git, ct))
        {
            var bad = cache + ".corrupt-" + Guid.NewGuid().ToString("N");
            Directory.Move(cache, bad);
            ResilientDirectory.Delete(bad);
        }
        if (!Directory.Exists(cache))
            await Required(git, root, ["init", "--bare", "--quiet", cache], false, ct);

        // The cache holds no credential or remote configuration. A scoped
        // credential is present only on this fetch process.
        long transferred = 0;
        if (!await HasCommitAsync(cache, expectedSha, git, ct))
        {
            var before = ObjectBytes(cache);
            await Required(git, cache, ["fetch", "--no-tags", "--no-recurse-submodules",
                repositoryUrl, resultRef], true, ct);
            transferred = Math.Max(0, ObjectBytes(cache) - before);
            var fetched = await git(cache, ["rev-parse", "FETCH_HEAD"], false, ct);
            if (fetched.Success && !string.Equals(fetched.StdOut.Trim(), expectedSha,
                    StringComparison.OrdinalIgnoreCase))
                throw new ReviewInfrastructureException("ShaMismatch",
                    $"Fetched result ref '{resultRef}' at '{fetched.StdOut.Trim()}' "
                    + $"instead of expected Result-SHA '{expectedSha}'.");
        }
        if (!await HasCommitAsync(cache, expectedSha, git, ct) || !await HealthyAsync(cache, git, ct))
            throw new ReviewInfrastructureException("SnapshotUnavailable",
                "Host source cache could not verify the requested commit and objects.");
        await Required(git, cache, ["update-ref", "refs/heads/source-cache/" + expectedSha,
            expectedSha], false, ct);
        await Required(git, cache, ["symbolic-ref", "HEAD",
            "refs/heads/source-cache/" + expectedSha], false, ct);

        // Copy objects into the attempt, so eviction cannot invalidate a live
        // review. No alternates or hard links survive this boundary.
        await Required(git, root, ["clone", "--no-checkout", "--no-hardlinks", cache, destination], false, ct);
        await Required(git, destination, ["remote", "set-url", "origin", repositoryUrl], false, ct);
        if (sparsePaths is { Count: > 0 })
        {
            await Required(git, destination, ["sparse-checkout", "init", "--cone"], false, ct);
            await Required(git, destination, ["sparse-checkout", "set", "--", .. sparsePaths], false, ct);
        }
        await Required(git, destination, ["checkout", "--detach", expectedSha], false, ct);
        Directory.SetLastWriteTimeUtc(cache, utcNow());
        TryEvict(root, cache);
        started.Stop();
        return (transferred, started.Elapsed);
    }

    internal static async Task<(long Bytes, TimeSpan Ready)> MaterializeBundleAsync(
        string bundleSha256, string expectedSha, string destination,
        Func<CancellationToken, Task<byte[]>> download,
        Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<ProcessResult>> git,
        Func<DateTime> utcNow, CancellationToken ct, IReadOnlyList<string>? sparsePaths = null,
        string? cacheRoot = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var root = cacheRoot ?? Root;
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, bundleSha256.ToLowerInvariant());
        await using var held = await LockAsync(cache + ".lock", ct);
        if (Directory.Exists(cache) && (!await HealthyAsync(cache, git, ct)
            || !await HasCommitAsync(cache, expectedSha, git, ct)))
        {
            var bad = cache + ".corrupt-" + Guid.NewGuid().ToString("N");
            Directory.Move(cache, bad);
            ResilientDirectory.Delete(bad);
        }
        long bytes = 0;
        if (!Directory.Exists(cache))
        {
            var bundle = Path.Combine(root, "source-" + Guid.NewGuid().ToString("N") + ".bundle");
            try
            {
                var content = await download(ct);
                bytes = content.LongLength;
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)),
                    bundleSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ReviewInfrastructureException("SourceBundleDigestMismatch",
                        "Source bundle does not match its recorded SHA-256 digest.");
                await File.WriteAllBytesAsync(bundle, content, ct);
                await Required(git, root, ["clone", "--bare", "--quiet", bundle, cache], false, ct);
            }
            finally { if (File.Exists(bundle)) File.Delete(bundle); }
        }
        if (!await HasCommitAsync(cache, expectedSha, git, ct) || !await HealthyAsync(cache, git, ct))
            throw new ReviewInfrastructureException("SnapshotUnavailable",
                "Host source cache could not verify the bundled commit and objects.");
        await Required(git, cache, ["update-ref", "refs/heads/source-cache/" + expectedSha,
            expectedSha], false, ct);
        await Required(git, cache, ["symbolic-ref", "HEAD",
            "refs/heads/source-cache/" + expectedSha], false, ct);
        await Required(git, root, ["clone", "--no-checkout", "--no-hardlinks", cache, destination], false, ct);
        if (sparsePaths is { Count: > 0 })
        {
            await Required(git, destination, ["sparse-checkout", "init", "--cone"], false, ct);
            await Required(git, destination, ["sparse-checkout", "set", "--", .. sparsePaths], false, ct);
        }
        await Required(git, destination, ["checkout", "--detach", expectedSha], false, ct);
        Directory.SetLastWriteTimeUtc(cache, utcNow());
        TryEvict(root, cache);
        started.Stop();
        return (bytes, started.Elapsed);
    }

    private static async Task<bool> HasCommitAsync(string cache, string sha,
        Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<ProcessResult>> git,
        CancellationToken ct)
        => (await git(cache, ["cat-file", "-e", sha + "^{commit}"], false, ct)).Success;

    private static long ObjectBytes(string cache)
    {
        var objects = Path.Combine(cache, "objects");
        return Directory.Exists(objects)
            ? Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0;
    }

    private static async Task<bool> HealthyAsync(string cache,
        Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<ProcessResult>> git,
        CancellationToken ct)
        => (await git(cache, ["fsck", "--full", "--no-reflogs", "--no-progress"], false, ct)).Success;

    private static async Task Required(
        Func<string, IReadOnlyList<string>, bool, CancellationToken, Task<ProcessResult>> git,
        string cwd, IReadOnlyList<string> args, bool credentialed, CancellationToken ct)
    {
        var result = await git(cwd, args, credentialed, ct);
        if (!result.Success)
            throw new ReviewInfrastructureException("SnapshotUnavailable",
                $"Source cache git {args[0]} failed: {result.StdErr.Trim()}");
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, ct); }
        }
    }

    private static void TryEvict(string root, string active)
    {
        try { Evict(root, active); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Host source cache sweep failed: {0}", exception.Message);
        }
    }

    private static void Evict(string root, string active)
    {
        var entries = Directory.EnumerateDirectories(root)
            .Where(path => Path.GetFileName(path).Length == 64)
            .Select(path => (Path: path, Stamp: Directory.GetLastWriteTimeUtc(path),
                Bytes: Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length)))
            .OrderBy(entry => entry.Stamp).ToList();
        var bytes = entries.Sum(entry => entry.Bytes);
        while (entries.Count > 0 && (entries.Count > MaximumEntries || bytes > MaximumBytes))
        {
            var victim = entries.FirstOrDefault(entry => entry.Path != active);
            if (victim.Path is null) victim = entries[0];
            try
            {
                if (victim.Path == active)
                    ResilientDirectory.Delete(victim.Path);
                else
                {
                    using var held = new FileStream(victim.Path + ".lock", FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                    ResilientDirectory.Delete(victim.Path);
                }
                entries.Remove(victim);
                bytes -= victim.Bytes;
            }
            catch (IOException) { entries.Remove(victim); }
        }
    }
}
