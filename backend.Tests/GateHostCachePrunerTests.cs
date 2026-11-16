using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3866: the remote gate worker prunes the gate host's Node compile cache
/// and old gate workspaces, and names critical disk on a failed gate.
/// </summary>
public sealed class GateHostCachePrunerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-host-prune-" + Guid.NewGuid().ToString("N"));

    public GateHostCachePrunerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Node_compile_cache_root_follows_NODE_COMPILE_CACHE_or_the_temp_default()
    {
        Assert.Equal(Path.Combine("/tmp", "node-compile-cache"),
            GateHostCachePruner.NodeCompileCacheRoot(null, "/tmp"));
        Assert.Equal(Path.Combine("/tmp", "node-compile-cache"),
            GateHostCachePruner.NodeCompileCacheRoot(" ", "/tmp"));
        Assert.Equal("/srv/node-cache", GateHostCachePruner.NodeCompileCacheRoot("/srv/node-cache", "/tmp"));
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(-3, 24)]
    [InlineData(2, 13)]
    [InlineData(48, 48)]
    public void Configured_age_is_clamped_above_the_gate_run_budget(int configured, int expectedHours)
        => Assert.Equal(TimeSpan.FromHours(expectedHours), GateHostCachePruner.MaxAge(configured));

    [Fact]
    public void Remote_gate_options_carry_the_configured_prune_age()
    {
        IConfiguration Config(string? hours) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RemoteGate:SshHost"] = "gate.example",
                ["RemoteGate:WorkerPath"] = "/opt/gate/worker.dll",
                ["RemoteGate:CachePruneAgeHours"] = hours,
            }).Build();

        Assert.Equal(36, RemoteGateOptions.Read(Config("36")).CachePruneAgeHours);
        Assert.Equal(0, RemoteGateOptions.Read(Config(null)).CachePruneAgeHours);
        Assert.Equal(0, RemoteGateOptions.Read(Config("-5")).CachePruneAgeHours);
    }

    [Fact]
    public void Prune_removes_old_cache_files_and_workspaces_and_logs_freed_bytes_per_target()
    {
        var cache = Path.Combine(_root, "node-compile-cache");
        var oldEntry = Write(Path.Combine(cache, "v22.23.1-x64-9de703df-0", "old"), 1000, Now.AddHours(-30));
        var freshEntry = Write(Path.Combine(cache, "v22.23.1-x64-9de703df-0", "fresh"), 10, Now.AddHours(-1));
        Write(Path.Combine(cache, "v20.0.0-x64-0", "stale"), 24, Now.AddHours(-30));
        var workspaces = Path.Combine(_root, "agentstudio-review-gates");
        var oldWorkspace = Path.Combine(workspaces, "0123abcd");
        Write(Path.Combine(oldWorkspace, "frontend", "dist", "main.js"), 4000, Now.AddHours(-40));
        Directory.SetLastWriteTimeUtc(oldWorkspace, Now.AddHours(-40));
        var liveWorkspace = Path.Combine(workspaces, "4567efgh");
        Write(Path.Combine(liveWorkspace, "README.md"), 7, Now.AddHours(-40));
        Directory.SetLastWriteTimeUtc(liveWorkspace, Now.AddHours(-2));
        var dependencyCache = Path.Combine(workspaces, BuildTestGateRunner.DependencyCacheDirectoryName);
        Write(Path.Combine(dependencyCache, "pkg"), 9, Now.AddHours(-40));
        Directory.SetLastWriteTimeUtc(dependencyCache, Now.AddHours(-40));
        var stamp = Path.Combine(_root, "prune", "last-prune");
        var lines = new List<string>();

        var pruned = GateHostCachePruner.PruneIfDue(cache, workspaces, stamp,
            TimeSpan.FromHours(24), Now, lines.Add);

        Assert.False(File.Exists(oldEntry));
        Assert.True(File.Exists(freshEntry));
        Assert.False(Directory.Exists(Path.Combine(cache, "v20.0.0-x64-0")));
        Assert.False(Directory.Exists(oldWorkspace));
        Assert.True(Directory.Exists(liveWorkspace));
        Assert.True(Directory.Exists(dependencyCache));
        Assert.Equal(new GateHostCachePrune(GateHostCachePruner.NodeCompileCacheTarget, cache, 2, 1024),
            pruned[0]);
        Assert.Equal(new GateHostCachePrune(GateHostCachePruner.GateWorkspacesTarget, workspaces, 1, 4000),
            pruned[1]);
        Assert.Equal(new[]
        {
            $"remote_gate_cache_pruned target=node-compile-cache path={cache} entries=2 freed_bytes=1024 max_age_hours=24",
            $"remote_gate_cache_pruned target=gate-workspaces path={workspaces} entries=1 freed_bytes=4000 max_age_hours=24",
        }, lines);

        // A second worker on the same host within the interval does not prune again.
        Write(Path.Combine(cache, "again"), 5, Now.AddHours(-30));
        Assert.Empty(GateHostCachePruner.PruneIfDue(cache, workspaces, stamp,
            TimeSpan.FromHours(24), Now.AddMinutes(30), lines.Add));
        Assert.True(File.Exists(Path.Combine(cache, "again")));
        Assert.Single(GateHostCachePruner.PruneIfDue(cache, workspaces, stamp,
            TimeSpan.FromHours(24), Now.AddHours(2), _ => { }).Where(item => item.Entries == 1));
    }

    [Fact]
    public async Task Worker_names_gate_host_disk_pressure_on_a_failed_run_and_prunes_its_caches()
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = Path.Combine(_root, "source");
        var runId = Guid.NewGuid().ToString("N");
        var run = Path.Combine(_root, runId);
        Directory.CreateDirectory(run);
        Git(_root, "init", "-q", source);
        Git(source, "config", "user.email", "gate@example.invalid");
        Git(source, "config", "user.name", "Gate Test");
        File.WriteAllText(Path.Combine(source, "subject.txt"), "exact subject");
        Git(source, "add", ".");
        Git(source, "commit", "-qm", "subject");
        var sha = Git(source, "rev-parse", "HEAD").Trim();
        Git(source, "branch", "gate-subject");
        var bundle = Path.Combine(run, "source.bundle");
        Git(source, "bundle", "create", bundle, "refs/heads/gate-subject");
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(bundle)));
        var request = new BuildTestGateRequest("source.git", sha, "remote-worker")
        {
            SubjectRef = "refs/heads/gate-subject",
            InfrastructureTimeout = TimeSpan.FromSeconds(30),
            RequiredTestLevel = TestExecutionLevels.BuildOnly,
        };
        var invocation = new RemoteGateInvocation(1, runId, digest, request, null,
            new BuildProfile { BuildCmds = ["echo 'write failed' >&2; exit 3"] },
            PostStepMode.Fail, 60, 120) { CachePruneAgeHours = 13 };
        var requestPath = Path.Combine(run, "request.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(invocation, RemoteBuildTestGateRunner.Json));
        var roots = new GateHostCacheRoots(Path.Combine(_root, "node-compile-cache"),
            Path.Combine(_root, "workspaces"), Path.Combine(_root, "prune", "last-prune"));
        var stale = Write(Path.Combine(roots.NodeCompileCache, "v22", "entry"), 64, Now.AddDays(-30));

        Assert.Equal(0, await RemoteGateWorker.RunAsync(requestPath,
            runnerFactory: logger => new BuildTestGateRunner(
                logger, BuildTestMachineGateMode.BypassForHermeticTest,
                Path.Combine(_root, "preparation-cache")),
            diskProbe: new FixedHostFilesystemProbe(4L << 30, 246L << 30), cacheRoots: roots));

        // The worker prunes against the real clock; this entry is older than any run of this test.
        Assert.False(File.Exists(stale));
        var response = JsonSerializer.Deserialize<RemoteGateResponse>(
            await File.ReadAllTextAsync(Path.Combine(run, "response.json")), RemoteBuildTestGateRunner.Json)!;
        Assert.Equal(BuildTestGateVerdict.Fail, response.Result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.DiskFull, response.Result.FailureKind);
        Assert.Equal(1.6, response.Result.DiskPressure?.FreePercent);
        Assert.Equal(4L << 30, response.Result.DiskPressure?.FreeBytes);
        Assert.StartsWith(GateDiskPressure.ReasonPrefix + "gate run file system " + run,
            response.Result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_leaves_a_failed_run_unattributed_while_the_disk_is_healthy()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runId = Guid.NewGuid().ToString("N");
        var run = Path.Combine(_root, runId);
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "source.bundle"), "not a bundle");
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(run, "source.bundle"))));
        var invocation = new RemoteGateInvocation(1, runId, digest,
            new BuildTestGateRequest("source.git", new string('a', 40), "remote-worker")
            { InfrastructureTimeout = TimeSpan.FromSeconds(30) },
            null, new BuildProfile(), PostStepMode.Fail, 60, 120);
        var requestPath = Path.Combine(run, "request.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(invocation, RemoteBuildTestGateRunner.Json));

        Assert.Equal(0, await RemoteGateWorker.RunAsync(requestPath,
            diskProbe: FixedHostFilesystemProbe.Healthy,
            cacheRoots: new GateHostCacheRoots(Path.Combine(_root, "ncc"), Path.Combine(_root, "ws"),
                Path.Combine(_root, "prune", "last-prune"))));

        var response = JsonSerializer.Deserialize<RemoteGateResponse>(
            await File.ReadAllTextAsync(Path.Combine(run, "response.json")), RemoteBuildTestGateRunner.Json)!;
        Assert.Equal(BuildTestGateVerdict.Fail, response.Result.Verdict);
        Assert.Null(response.Result.DiskPressure);
        Assert.Equal(BuildTestGateFailureKind.Environment, response.Result.FailureKind);
    }

    private static string Write(string path, int bytes, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}
