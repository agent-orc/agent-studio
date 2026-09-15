using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// TE-52: the build/test gate plans its verify commands after repository
/// preparation restored this subject's dependencies into per-run cache folders.
/// A verify command that does not inherit those folders resolves against a
/// package folder the restore never wrote to and dies with NETSDK1064 on
/// <c>--no-restore</c>. These tests drive the real gate against a fake
/// repository whose build step needs exactly what its prepare restored.
/// </summary>
public sealed class BuildTestGatePreparationBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gate-preparation-binding-" + Guid.NewGuid().ToString("N"));

    private string Repository => Path.Combine(_root, "repo");
    private string CacheRoot => Path.Combine(_root, "product-cache");
    private string ObservedBindings => Path.Combine(Repository, "observed-bindings.txt");

    public BuildTestGatePreparationBindingTests()
    {
        Directory.CreateDirectory(Repository);
        WriteRepository();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public async Task Cache_miss_gate_runs_its_verify_commands_against_the_restored_dependencies()
    {
        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.All(result.PreparationManifest!.Caches, cache => Assert.Equal("published", cache.State));
        AssertBoundToThisRun();
    }

    [Fact]
    public async Task Cache_hit_gate_runs_its_verify_commands_against_the_restored_dependencies()
    {
        var first = await RunGateAsync();
        Assert.Equal(BuildTestGateVerdict.Ok, first.Verdict);
        File.Delete(ObservedBindings);

        var second = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Ok, second.Verdict);
        Assert.All(second.PreparationManifest!.Caches, cache => Assert.Equal("hit", cache.State));
        AssertBoundToThisRun();
    }

    [Fact]
    public async Task Gate_releases_the_per_run_cache_folders_and_keeps_the_published_entries()
    {
        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        var runs = Path.Combine(CacheRoot, ".runs");
        Assert.True(!Directory.Exists(runs) || Directory.GetDirectories(runs).Length == 0,
            "The gate owns the per-run cache folders only until its last verify command.");
        foreach (var cache in result.PreparationManifest!.Caches)
            Assert.True(File.Exists(Path.Combine(cache.EntryPath, "manifest.json")));
    }

    private async Task<BuildTestGateResult> RunGateAsync()
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            CacheRoot);
        return await runner.RunAsync(
            new BuildTestGateRequest(Repository, null, "test", RequireExactSubject: false),
            changedFiles: null,
            profile: null,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
    }

    /// <summary>
    /// The verify command recorded the cache locations it actually saw. They must
    /// be this gate run's own per-run folders, never the gate's shared npm cache
    /// and never a published immutable entry.
    /// </summary>
    private void AssertBoundToThisRun()
    {
        var observed = File.ReadAllLines(ObservedBindings);
        Assert.Equal(2, observed.Length);
        var runs = Path.Combine(CacheRoot, ".runs") + Path.DirectorySeparatorChar;
        Assert.All(observed, path => Assert.StartsWith(runs, path, StringComparison.Ordinal));
        Assert.NotEqual(BuildTestGateRunner.NpmCachePath, observed[1]);
    }

    private void WriteRepository()
    {
        Write("packages.lock.json", "{\"version\":1,\"dependencies\":{}}");
        Write("package-lock.json", "{\"lockfileVersion\":3}");
        Write(".agent-studio/project.yml", """
            schemaVersion: 1
            stack: [dotnet, node]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - sh .agent-studio/verify-build
              test:
              lint:
            testSuites:
            cachePaths: [bin]
            capabilities: [linux]
            environment:
              CI: "true"
            """);
        // Stands in for `npm ci` + `dotnet restore`: both write into the
        // executor-owned cache folders the preparation hands them.
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            mkdir -p "$NUGET_PACKAGES/xunit.analyzers/1.4.0"
            printf nupkg > "$NUGET_PACKAGES/xunit.analyzers/1.4.0/xunit.analyzers.nupkg"
            mkdir -p "$NPM_CONFIG_CACHE/_cacache"
            printf cache > "$NPM_CONFIG_CACHE/_cacache/marker"
            """);
        // Stands in for `dotnet build --no-restore` plus `npm test`: neither may
        // restore anything again, so both fail unless they see what prepare did.
        Write(".agent-studio/verify-build", """
            #!/bin/sh
            set -eu
            test -f "$NUGET_PACKAGES/xunit.analyzers/1.4.0/xunit.analyzers.nupkg"
            test -f "$NPM_CONFIG_CACHE/_cacache/marker"
            printf '%s\n%s\n' "$NUGET_PACKAGES" "$NPM_CONFIG_CACHE" > observed-bindings.txt
            """);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Repository, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
