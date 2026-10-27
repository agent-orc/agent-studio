using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HostGateSourceCacheTests
{
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
