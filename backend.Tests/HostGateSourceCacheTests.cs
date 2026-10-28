using System.Text.Json;
using System.Security.Cryptography;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HostGateSourceCacheTests
{
    [Fact]
    public async Task Healthy_sha_miss_keeps_cache_and_receives_only_new_objects()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "gate-incremental-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var cache = Path.Combine(root, "cache", new string('a', 64));
            var storeRoot = Path.Combine(root, "store");
            await Git(root, ["init", source]);
            await Git(source, ["config", "user.email", "gate@example.invalid"]);
            await Git(source, ["config", "user.name", "Gate Test"]);
            File.WriteAllBytes(Path.Combine(source, "base.bin"), RandomNumberGenerator.GetBytes(1024 * 1024));
            await Git(source, ["add", "."]);
            await Git(source, ["commit", "-qm", "base"]);
            var baseSha = (await Value(source, ["rev-parse", "HEAD"])).Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await Git(root, ["clone", "--bare", "--no-hardlinks", source, cache]);
            await Git(cache, ["update-ref", "refs/heads/gate-cache/" + baseSha, baseSha]);
            File.WriteAllText(Path.Combine(source, "change.txt"), "small candidate");
            await Git(source, ["add", "."]);
            await Git(source, ["commit", "-qm", "candidate"]);
            var sha = (await Value(source, ["rev-parse", "HEAD"])).Trim();
            Assert.Equal(PullGateDaemon.GateSourceCacheState.HealthyWithoutSubject,
                await PullGateDaemon.InspectCacheAsync(cache, sha, root, CancellationToken.None));
            Assert.Equal(baseSha, await PullGateDaemon.CacheTipAsync(cache, root, CancellationToken.None));

            await Git(source, ["branch", "gate-subject", sha]);
            var bundle = Path.Combine(root, "full.bundle");
            await Git(source, ["bundle", "create", bundle, "refs/heads/gate-subject"]);
            var runId = Guid.NewGuid().ToString("N");
            var digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(bundle)));
            var invocation = new RemoteGateInvocation(1, runId, digest,
                new BuildTestGateRequest("source.git", sha, "incremental"), null, null,
                PostStepMode.Fail, 60, 120);
            var clock = new FakeTimeProvider();
            var store = new PullGateStore(storeRoot, clock);
            store.Register("gate-1", "Gate");
            store.Enqueue(invocation, bundle);
            var lease = store.Claim("gate-1")!.Lease;
            var (thin, thinDigest) = await store.BundleForLeaseAsync("gate-1", runId, lease,
                baseSha, CancellationToken.None);
            Assert.True(new FileInfo(thin).Length < new FileInfo(bundle).Length / 10);
            Assert.Equal(thinDigest, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(thin))));
            var attempt = Path.Combine(root, "attempt.git");
            await Git(root, ["clone", "--bare", cache, attempt]);
            await Git(attempt, ["fetch", thin, "refs/heads/gate-subject:refs/heads/gate-subject"]);
            await Git(attempt, ["fsck", "--full", "--no-reflogs", "--no-progress"]);
            Assert.Equal(sha, (await Value(attempt, ["rev-parse", "refs/heads/gate-subject"])).Trim());
            var run = Path.Combine(root, runId);
            Directory.CreateDirectory(run);
            File.Copy(thin, Path.Combine(run, "source.bundle"));
            var workerInvocation = invocation with
            {
                CachePath = cache,
                TransferBundleSha256 = thinDigest,
                Request = invocation.Request with { RequiredTestLevel = TestExecutionLevels.BuildOnly },
                Profile = new BuildProfile { BuildCmds = ["true"] },
            };
            var requestPath = Path.Combine(run, "request.json");
            await File.WriteAllTextAsync(requestPath,
                JsonSerializer.Serialize(workerInvocation, RemoteBuildTestGateRunner.Json));
            Assert.Equal(0, await RemoteGateWorker.RunAsync(requestPath,
                runnerFactory: logger => new BuildTestGateRunner(logger,
                    BuildTestMachineGateMode.BypassForHermeticTest,
                    Path.Combine(root, "preparation-cache"))));
            var response = JsonSerializer.Deserialize<RemoteGateResponse>(
                await File.ReadAllTextAsync(Path.Combine(run, "response.json")),
                RemoteBuildTestGateRunner.Json)!;
            Assert.Equal(BuildTestGateVerdict.Ok, response.Result.Verdict);
            Assert.Equal(sha, response.Result.TestedSha);
            Assert.Equal(digest, response.BundleSha256);
            Assert.True(await PullGateDaemon.CacheHasSubjectAsync(cache, sha, root, CancellationToken.None));
            Assert.True(Directory.Exists(cache));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Cache_sweep_evicts_oldest_entry_and_keeps_active_entry()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-cache-sweep-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider();
        Directory.CreateDirectory(root);
        try
        {
            var entries = Enumerable.Range(0, 9).Select(index => Path.Combine(root,
                index.ToString("x64"))).ToArray();
            for (var index = 0; index < entries.Length; index++)
            {
                Directory.CreateDirectory(entries[index]);
                File.WriteAllText(Path.Combine(entries[index], "object"), "cache");
                Directory.SetLastWriteTimeUtc(entries[index], clock.GetUtcNow().UtcDateTime.AddMinutes(index));
            }
            GateSourceCacheRetention.Sweep(root, entries[8], clock.GetUtcNow().UtcDateTime.AddHours(1));
            Assert.False(Directory.Exists(entries[0]));
            Assert.True(Directory.Exists(entries[8]));
            Assert.Equal(8, Directory.EnumerateDirectories(root).Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Warm_cache_runs_exact_commit_without_bundle_and_rejects_corruption()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "gate-source-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var cache = Path.Combine(root, "cache", new string('a', 64));
            var runId = Guid.NewGuid().ToString("N");
            var run = Path.Combine(root, runId);
            Directory.CreateDirectory(run);
            await Git(root, ["init", source]);
            await Git(source, ["config", "user.email", "gate@example.invalid"]);
            await Git(source, ["config", "user.name", "Gate Test"]);
            File.WriteAllText(Path.Combine(source, "subject.txt"), "exact subject");
            await Git(source, ["add", "."]);
            await Git(source, ["commit", "-qm", "subject"]);
            var sha = (await Value(source, ["rev-parse", "HEAD"])).Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await Git(root, ["clone", "--bare", "--no-hardlinks", source, cache]);
            Assert.True(await PullGateDaemon.CacheHasSubjectAsync(cache, sha, root, CancellationToken.None));

            var request = new BuildTestGateRequest("source.git", sha, "warm-cache")
            {
                SubjectRef = "refs/heads/gate-subject",
                InfrastructureTimeout = TimeSpan.FromSeconds(30),
                RequiredTestLevel = TestExecutionLevels.BuildOnly,
            };
            var invocation = new RemoteGateInvocation(1, runId, new string('0', 64), request,
                null, new BuildProfile { BuildCmds = ["true"] }, PostStepMode.Fail, 60, 120)
            { CachePath = cache, SourceFromCache = true };
            var requestPath = Path.Combine(run, "request.json");
            await File.WriteAllTextAsync(requestPath,
                JsonSerializer.Serialize(invocation, RemoteBuildTestGateRunner.Json));
            Assert.Equal(0, await RemoteGateWorker.RunAsync(requestPath,
                runnerFactory: logger => new BuildTestGateRunner(logger,
                    BuildTestMachineGateMode.BypassForHermeticTest,
                    Path.Combine(root, "preparation-cache"))));
            var response = JsonSerializer.Deserialize<RemoteGateResponse>(
                await File.ReadAllTextAsync(Path.Combine(run, "response.json")),
                RemoteBuildTestGateRunner.Json)!;
            Assert.Equal(BuildTestGateVerdict.Ok, response.Result.Verdict);
            Assert.Equal(sha, response.Result.TestedSha);
            Assert.False(File.Exists(Path.Combine(run, "source.bundle")));

            var objectFile = Directory.EnumerateFiles(Path.Combine(cache, "objects"), "*",
                    SearchOption.AllDirectories)
                .First(path => path.EndsWith(".pack", StringComparison.Ordinal)
                    || Path.GetFileName(Path.GetDirectoryName(path)!).Length == 2);
            File.SetAttributes(objectFile, FileAttributes.Normal);
            await using (var stream = File.Open(objectFile, FileMode.Open, FileAccess.Write))
                stream.WriteByte(0);
            Assert.False(await PullGateDaemon.CacheHasSubjectAsync(cache, sha, root, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task Git(string cwd, IReadOnlyList<string> args)
        => RemoteGateProcess.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30), CancellationToken.None);

    private static async Task<string> Value(string cwd, IReadOnlyList<string> args)
    {
        var lines = new List<string>();
        await RemoteGateProcess.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30),
            CancellationToken.None, lines.Add);
        return string.Join('\n', lines);
    }
}
