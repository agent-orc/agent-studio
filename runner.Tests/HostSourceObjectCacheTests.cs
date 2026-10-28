using System.Security.Cryptography;
using System.Text;
using AgentRunner;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AgentRunner.Tests;

public sealed class HostSourceObjectCacheTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Bundle_only_subject_downloads_once_and_checks_digest()
    {
        var root = Path.Combine(Path.GetTempPath(), "bundle-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var origin = Path.Combine(root, "origin");
            await Git(root, ["init", origin]);
            await Git(origin, ["config", "user.email", "cache@example.invalid"]);
            await Git(origin, ["config", "user.name", "Cache Test"]);
            File.WriteAllText(Path.Combine(origin, "source.txt"), "bundled");
            await Git(origin, ["add", "."]);
            await Git(origin, ["commit", "-qm", "source"]);
            var sha = (await Git(origin, ["rev-parse", "HEAD"])).StdOut.Trim();
            await Git(origin, ["branch", "source"]);
            var bundle = Path.Combine(root, "source.bundle");
            await Git(origin, ["bundle", "create", bundle, "refs/heads/source"]);
            var content = await File.ReadAllBytesAsync(bundle);
            var digest = Convert.ToHexStringLower(SHA256.HashData(content));
            var clock = new FakeTimeProvider();
            var downloads = 0;
            Task<byte[]> Download(CancellationToken _)
            {
                downloads++;
                return Task.FromResult(content);
            }
            Task<ProcessResult> Run(string cwd, IReadOnlyList<string> args,
                bool _, CancellationToken ct) => ProcessRunner.RunAsync("git", args, cwd, ct: ct);
            var cacheRoot = Path.Combine(root, "cache");
            await HostSourceObjectCache.MaterializeBundleAsync(digest, sha,
                Path.Combine(root, "first"), Download, Run,
                () => clock.GetUtcNow().UtcDateTime, CancellationToken.None, cacheRoot: cacheRoot);
            var warm = await HostSourceObjectCache.MaterializeBundleAsync(digest, sha,
                Path.Combine(root, "second"), Download, Run,
                () => clock.GetUtcNow().UtcDateTime, CancellationToken.None, cacheRoot: cacheRoot);
            Assert.Equal(1, downloads);
            Assert.Equal(0, warm.Bytes);
            Assert.Equal("bundled", File.ReadAllText(Path.Combine(root, "second", "source.txt")));
            await Assert.ThrowsAsync<ReviewInfrastructureException>(() =>
                HostSourceObjectCache.MaterializeBundleAsync(new string('0', 64), sha,
                    Path.Combine(root, "invalid"), Download, Run,
                    () => clock.GetUtcNow().UtcDateTime, CancellationToken.None,
                    cacheRoot: Path.Combine(root, "bad-cache")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Warm_commit_is_reused_sparsely_and_corruption_is_refetched()
    {
        var root = Path.Combine(Path.GetTempPath(), "source-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var origin = Path.Combine(root, "origin");
            var cacheRoot = Path.Combine(root, "cache");
            await Git(root, ["init", origin]);
            await Git(origin, ["config", "user.email", "cache@example.invalid"]);
            await Git(origin, ["config", "user.name", "Cache Test"]);
            Directory.CreateDirectory(Path.Combine(origin, "keep"));
            Directory.CreateDirectory(Path.Combine(origin, "skip"));
            File.WriteAllText(Path.Combine(origin, "keep", "a.txt"), "wanted");
            File.WriteAllText(Path.Combine(origin, "skip", "b.txt"), "not in checkout");
            await Git(origin, ["add", "."]);
            await Git(origin, ["commit", "-qm", "source"]);
            var sha = (await Git(origin, ["rev-parse", "HEAD"])).StdOut.Trim();
            var clock = new FakeTimeProvider();
            var fetches = 0;
            async Task<ProcessResult> Run(string cwd, IReadOnlyList<string> args,
                bool _, CancellationToken ct)
            {
                if (args[0] == "fetch") fetches++;
                return await ProcessRunner.RunAsync("git", args, cwd, ct: ct);
            }

            var first = Path.Combine(root, "first");
            await HostSourceObjectCache.MaterializeAsync(origin, sha, sha, first,
                Run, () => clock.GetUtcNow().UtcDateTime, CancellationToken.None, ["keep"], cacheRoot);
            Assert.Equal("wanted", File.ReadAllText(Path.Combine(first, "keep", "a.txt")));
            Assert.False(File.Exists(Path.Combine(first, "skip", "b.txt")));
            Assert.Equal(1, fetches);

            var second = Path.Combine(root, "second");
            var warm = await HostSourceObjectCache.MaterializeAsync(origin, sha, sha, second,
                Run, () => clock.GetUtcNow().UtcDateTime, CancellationToken.None, ["keep"], cacheRoot);
            Assert.Equal(0, warm.Bytes);
            Assert.Equal(1, fetches);
            var warmSeconds = new List<double> { warm.Ready.TotalSeconds };
            for (var index = 0; index < 4; index++)
            {
                var sample = await HostSourceObjectCache.MaterializeAsync(origin, sha, sha,
                    Path.Combine(root, "warm-" + index), Run,
                    () => clock.GetUtcNow().UtcDateTime, CancellationToken.None, ["keep"], cacheRoot);
                Assert.Equal(0, sample.Bytes);
                warmSeconds.Add(sample.Ready.TotalSeconds);
            }
            Assert.Equal(1, fetches);
            output.WriteLine($"Warm source median bytes=0 ready_seconds={warmSeconds.Order().ElementAt(2):F3} samples=5");

            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(origin)));
            var cache = Path.Combine(cacheRoot, key);
            var objectFile = Directory.EnumerateFiles(Path.Combine(cache, "objects"), "*",
                    SearchOption.AllDirectories)
                .First(path => path.EndsWith(".pack", StringComparison.Ordinal)
                    || Path.GetFileName(Path.GetDirectoryName(path)!).Length == 2);
            File.SetAttributes(objectFile, FileAttributes.Normal);
            await using (var stream = File.Open(objectFile, FileMode.Open, FileAccess.Write))
                stream.WriteByte(0);

            var third = Path.Combine(root, "third");
            await HostSourceObjectCache.MaterializeAsync(origin, sha, sha, third,
                Run, () => clock.GetUtcNow().UtcDateTime, CancellationToken.None, ["keep"], cacheRoot);
            Assert.Equal(2, fetches);
            Assert.Equal("wanted", File.ReadAllText(Path.Combine(third, "keep", "a.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<ProcessResult> Git(string cwd, IReadOnlyList<string> args)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd);
        Assert.True(result.Success, result.StdErr);
        return result;
    }
}
