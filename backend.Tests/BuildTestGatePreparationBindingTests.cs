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
    private string CacheRoot => Path.Combine(_root, "agentstudio-preparation-cache");
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

    [Fact]
    public async Task Cache_failure_quarantines_entries_and_the_gate_retries_once()
    {
        Assert.Equal(BuildTestGateVerdict.Ok, (await RunGateAsync()).Verdict);
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            if [ ! -f cache-retry-attempted ]; then
              touch cache-retry-attempted
              printf 'EINTEGRITY cached package mismatch\n' >&2
              exit 1
            fi
            mkdir -p "$NUGET_PACKAGES/xunit.analyzers/1.4.0"
            printf nupkg > "$NUGET_PACKAGES/xunit.analyzers/1.4.0/xunit.analyzers.nupkg"
            printf metadata > "$NUGET_PACKAGES/xunit.analyzers/1.4.0/.nupkg.metadata"
            mkdir -p "$NPM_CONFIG_CACHE/_cacache"
            printf cache > "$NPM_CONFIG_CACHE/_cacache/marker"
            """);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.None, result.FailureKind);
        Assert.True(result.PreparationCacheRetryPerformed);
        Assert.Contains("integration gate retried once", result.Reason);
        Assert.All(result.PreparationManifest!.Caches, cache => Assert.Equal("published", cache.State));
    }

    [Fact]
    public async Task Persistent_cache_failure_is_environment_and_spends_only_one_retry()
    {
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -eu
            printf 'attempt\n' >> cache-attempts.txt
            printf 'EINTEGRITY cached package mismatch\n' >&2
            exit 1
            """);

        var result = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        Assert.True(result.PreparationCacheRetryPerformed);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(Repository, "cache-attempts.txt")).Length);
    }

    [Fact]
    public async Task Torn_nuget_run_failure_is_environmental_and_evicts_the_published_block()
    {
        var seeded = await RunGateAsync();
        var nuget = Assert.Single(seeded.PreparationManifest!.Caches, item => item.Block == "nuget");
        Assert.True(Directory.Exists(nuget.EntryPath));
        Write(".agent-studio/verify-build", """
            #!/bin/sh
            set -eu
            printf "NuGet.targets(198,5): error : Could not find file '%s/example.package/1.0.0/example.package.1.0.0.nupkg'.\n" "$NUGET_PACKAGES" >&2
            exit 1
            """);

        var failed = await RunGateAsync();

        Assert.Equal(BuildTestGateVerdict.Fail, failed.Verdict);
        Assert.Equal(BuildTestGateFailureKind.Environment, failed.FailureKind);
        Assert.False(Directory.Exists(nuget.EntryPath));
        Assert.Contains("block=nuget", failed.Output, StringComparison.Ordinal);
        Assert.Contains("state=evicted reason=gate-environment-failure", failed.Output, StringComparison.Ordinal);
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
            printf metadata > "$NUGET_PACKAGES/xunit.analyzers/1.4.0/.nupkg.metadata"
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
