using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class WarmIntegrationGateTests
{
    [Fact]
    public async Task Consecutive_candidates_keep_incremental_output_and_checkout_the_exact_new_tree()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "warm-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? warmWorkspace = null;
        try
        {
            var source = Path.Combine(root, "source");
            var cache = Path.Combine(root, "cache", new string('c', 64));
            await Git(root, ["init", "-b", "gate-subject", source]);
            await Git(source, ["config", "user.email", "test@example.invalid"]);
            await Git(source, ["config", "user.name", "Test"]);
            await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "bin/\n");
            await File.WriteAllTextAsync(Path.Combine(source, "source.txt"), "first");
            await Git(source, ["add", "."]);
            await Git(source, ["commit", "-qm", "first"]);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await Git(root, ["clone", "--bare", source, cache]);
            BuildTestGateResult? first = null;
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (cycle == 1)
                {
                    await File.WriteAllTextAsync(Path.Combine(source, "source.txt"), "second");
                    await Git(source, ["commit", "-qam", "second"]);
                    await Git(cache, ["fetch", source, "+refs/heads/gate-subject:refs/heads/gate-subject"]);
                }
                var sha = await Value(source, ["rev-parse", "HEAD"]);
                var id = Guid.NewGuid().ToString("N");
                var run = Path.Combine(root, id);
                Directory.CreateDirectory(run);
                await File.WriteAllTextAsync(Path.Combine(run, "heartbeat"), "");
                var invocation = new RemoteGateInvocation(1, id, new string('0', 64),
                    new BuildTestGateRequest("source.git", sha, "merge")
                    { IntegrationPriority = true, RequiredTestLevel = TestExecutionLevels.CompileOnly },
                    ["source.txt"], new BuildProfile
                    { BuildCmds = ["mkdir -p bin; if [ -f bin/incremental ]; then echo WARM_OUTPUT_REUSED; fi; cat source.txt; touch bin/incremental"] },
                    PostStepMode.Fail, 60, 120) { SourceFromCache = true, CachePath = cache };
                var requestPath = Path.Combine(run, "request.json");
                await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(invocation, RemoteBuildTestGateRunner.Json));
                Assert.Equal(0, await RemoteGateWorker.RunAsync(requestPath));
                var response = JsonSerializer.Deserialize<RemoteGateResponse>(
                    await File.ReadAllTextAsync(Path.Combine(run, "response.json")), RemoteBuildTestGateRunner.Json)!;
                var result = response.Result;
                Console.WriteLine("AGT4141_WORKER_TIMINGS " + JsonSerializer.Serialize(new
                {
                    kind = "isolated-worker-fixture", cycle, machine = Environment.MachineName,
                    result.GateHostLoadAtStart, result.GateHostLoadAtEnd, result.StageDurationsMs,
                }));
                Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
                Assert.Equal(sha, result.TestedSha);
                warmWorkspace = result.Workspace;
                Assert.True(File.Exists(Path.Combine(warmWorkspace!, "bin", "incremental")));
                Assert.True(result.StageDurationsMs.ContainsKey("sourceCheckout"));
                if (cycle == 0)
                {
                    first = result;
                    await File.WriteAllTextAsync(Path.Combine(warmWorkspace!, "stale.txt"), "untracked residue");
                }
                else
                {
                    Assert.Equal(first!.Workspace, result.Workspace);
                    Assert.Contains("WARM_OUTPUT_REUSED", result.Output);
                    Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(warmWorkspace!, "source.txt")));
                    Assert.False(File.Exists(Path.Combine(warmWorkspace!, "stale.txt")));
                }
            }
        }
        finally
        {
            if (warmWorkspace is not null && Directory.Exists(warmWorkspace)) Directory.Delete(warmWorkspace, true);
            Directory.Delete(root, true);
        }
    }

    private static Task Git(string cwd, IReadOnlyList<string> args)
        => RemoteGateProcess.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30), CancellationToken.None);

    private static async Task<string> Value(string cwd, IReadOnlyList<string> args)
    {
        var lines = new List<string>();
        await RemoteGateProcess.RunAsync("git", args, cwd, TimeSpan.FromSeconds(30), CancellationToken.None, lines.Add);
        return string.Join('\n', lines).Trim();
    }
}
