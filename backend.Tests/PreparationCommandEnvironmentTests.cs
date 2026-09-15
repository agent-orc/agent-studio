using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2834: the preparation gate restores dependencies into product cache
/// locations that no toolchain rediscovers on its own. A command that runs
/// afterwards without the same locations fails with NETSDK1064 ("package ...
/// was not found. It might have been deleted since NuGet restore"), which is
/// exactly what the Windows merge gate reported on 15.09.2026 after a green
/// 7.2 s preparation.
///
/// The fixture repository stands in for that stack without a network: its
/// prepare script "restores" a package into <c>NUGET_PACKAGES</c> and its build
/// command requires that package, so the build is red on every path that does
/// not carry the preparation environment.
/// </summary>
public sealed class PreparationCommandEnvironmentTests : IDisposable
{
    private const string PackageFile = "fake.package/1.0.0/fake.package.nupkg";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "preparation-command-environment-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _publishedEntries = [];

    public PreparationCommandEnvironmentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var entry in _publishedEntries)
        {
            try { if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); }
            catch { /* best-effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    // A published block is the location the miss restored into.
    [InlineData("published", "NUGET_PACKAGES", "/cache/nuget/content", "/cache/nuget/content")]
    // A hit block is the entry the prepare process was bound to.
    [InlineData("hit", "NUGET_PACKAGES", "/cache/nuget/content", "/cache/nuget/content")]
    // A discarded block has no surviving content, so there is nothing to hand on.
    [InlineData("discarded", "NUGET_PACKAGES", null, null)]
    // A block from a manifest written before this contract names no variable.
    [InlineData("published", null, "/cache/nuget/content", null)]
    public void Resolve_hands_on_the_locations_whose_content_survived_the_run(
        string state,
        string? variable,
        string? contentPath,
        string? expected)
    {
        var resolved = PreparationCommandEnvironment.Resolve(
            [new PreparationCacheManifest("nuget", "key", state, "/cache/nuget", [], variable, contentPath)]);

        if (expected is null) Assert.Empty(resolved);
        else Assert.Equal(expected, Assert.Contains(variable!, resolved));
    }

    [Fact]
    public void Failed_preparation_hands_on_no_cache_location()
    {
        var caches = new PreparationCacheManifest[]
        {
            new("nuget", "key", "discarded", "/cache/nuget", [], "NUGET_PACKAGES", null),
        };
        var manifest = new ProjectPreparationManifest(
            1, null, new string('a', 64), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1,
            Succeeded: false, ".agent-studio/prepare",
            new Dictionary<string, string>(), new Dictionary<string, string>(), caches,
            PreparationFailureKind.Command, "command:1:deadbeef", "Prepare command failed.", null);

        var result = new ProjectPreparationResult(
            true, false, manifest, [], string.Empty, 1,
            PreparationFailureKind.Command, "command:1:deadbeef", "Prepare command failed.");

        Assert.Empty(PreparationCommandEnvironment.Resolve(manifest));
        Assert.Empty(result.CommandEnvironment);
    }

    [Fact]
    public void Nothing_is_bound_for_a_workspace_that_was_never_prepared()
        => Assert.Empty(PreparedWorkspaceEnvironment.For(_root));

    /// <summary>
    /// Gate path, cache miss: the first green preparation publishes its block and
    /// the gate's own verify command must find that package. Without the
    /// preparation environment the build command is red, which is the reported
    /// NETSDK1064 failure in miniature.
    /// </summary>
    [SkippableFact]
    public async Task Gate_verify_commands_after_a_cache_miss_find_the_restored_package()
    {
        Skip.IfNot(PosixShell.IsAvailable, "The fixture repository's prepare script is a POSIX script.");
        WriteRestoringRepository();

        var result = await RunGateAsync();

        var cache = SingleCache(result);
        Assert.Equal("published", cache.State);
        Assert.Equal("NUGET_PACKAGES", cache.EnvironmentVariable);
        Assert.True(File.Exists(Path.Combine(cache.ContentPath!, PackageFile.Replace('/', Path.DirectorySeparatorChar))),
            "The published cache entry must carry the package the prepare script restored.");
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Contains($"preparation environment: NUGET_PACKAGES={cache.ContentPath}", result.Output);
    }

    /// <summary>
    /// Gate path, cache hit: the second run restores nothing new, so the build
    /// depends entirely on being pointed at the existing entry.
    /// </summary>
    [SkippableFact]
    public async Task Gate_verify_commands_after_a_cache_hit_find_the_restored_package()
    {
        Skip.IfNot(PosixShell.IsAvailable, "The fixture repository's prepare script is a POSIX script.");
        WriteRestoringRepository();

        var miss = await RunGateAsync();
        var hit = await RunGateAsync();

        Assert.Equal("published", SingleCache(miss).State);
        Assert.Equal("hit", SingleCache(hit).State);
        Assert.Equal(BuildTestGateVerdict.Ok, hit.Verdict);
        // Miss and hit resolve to the same immutable entry content, so the build
        // reads the identical packages on both paths.
        Assert.Equal(SingleCache(miss).ContentPath, SingleCache(hit).ContentPath);
    }

    /// <summary>
    /// A hit binds the prepare process to the published entry instead of copying
    /// it into the per-run root, and a green run leaves no working folder behind.
    /// Both halves are what makes the resolved location on a hit the location the
    /// prepare process actually wrote to.
    /// </summary>
    [SkippableFact]
    public async Task Green_preparation_publishes_its_content_and_keeps_no_per_run_working_folder()
    {
        Skip.IfNot(PosixShell.IsAvailable, "The fixture repository's prepare script is a POSIX script.");
        WriteRestoringRepository();
        var cacheRoot = Path.Combine(_root, "local-cache");

        var miss = await RunPreparationAsync(cacheRoot);
        var hit = await RunPreparationAsync(cacheRoot);

        Assert.True(miss.Succeeded, miss.FailureReason);
        Assert.True(hit.Succeeded, hit.FailureReason);
        var runRoots = Path.Combine(cacheRoot, ".runs");
        Assert.True(
            !Directory.Exists(runRoots) || !Directory.EnumerateFileSystemEntries(runRoots).Any(),
            "A green preparation must not leave per-run cache working folders behind.");
        var content = Assert.Single(hit.Manifest!.Caches).ContentPath!;
        Assert.Equal(Assert.Single(miss.Manifest!.Caches).ContentPath, content);
        Assert.True(File.Exists(Path.Combine(content, PackageFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>
    /// Coding-run path: the run binds the locations its preparation resolved to
    /// the prepared workspace, and the agent's commands are started with them.
    /// The unbound control is the regression the binding exists for.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coding_run_commands_find_the_restored_package_on_a_miss_and_on_a_hit(bool warmCache)
    {
        Skip.IfNot(PosixShell.IsAvailable, "The fixture repository's prepare script is a POSIX script.");
        WriteRestoringRepository();
        var cacheRoot = Path.Combine(_root, "local-cache");
        if (warmCache) await RunPreparationAsync(cacheRoot);

        var preparation = await RunPreparationAsync(cacheRoot);

        Assert.True(preparation.Succeeded, preparation.FailureReason + " | " + preparation.Output);
        Assert.Equal(warmCache ? "hit" : "published", Assert.Single(preparation.Manifest!.Caches).State);
        // What ProjectRunner does once preparation is green, and what the CLI
        // spawn reads back for the working directory it starts the agent in.
        PreparedWorkspaceEnvironment.Bind(_root, preparation.CommandEnvironment);
        try
        {
            var bound = PreparedWorkspaceEnvironment.For(Path.Combine(_root, ".agent-studio"));
            Assert.Equal(preparation.CommandEnvironment, bound);
            Assert.Equal(0, await RunBuildCommandAsync(bound));
            Assert.NotEqual(0, await RunBuildCommandAsync(PreparationCommandEnvironment.None));
        }
        finally
        {
            PreparedWorkspaceEnvironment.Release(_root);
        }

        Assert.Empty(PreparedWorkspaceEnvironment.For(_root));
    }

    private async Task<BuildTestGateResult> RunGateAsync()
    {
        var runner = new BuildTestGateRunner(NullLogger<BuildTestGateRunner>.Instance);
        var result = await runner.RunAsync(
            new BuildTestGateRequest(_root, null, "preparation-environment", RequireExactSubject: false),
            changedFiles: null,
            profile: null,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        // The gate publishes into the machine-wide preparation cache; the
        // fixture's unique project file keeps the entry private to this test and
        // Dispose removes it again.
        foreach (var cache in result.PreparationManifest?.Caches ?? [])
            _publishedEntries.Add(cache.EntryPath);
        Assert.NotNull(result.PreparationManifest);
        return result;
    }

    private Task<ProjectPreparationResult> RunPreparationAsync(string cacheRoot)
        => ProjectPreparationExecutor.RunAsync(
            _root,
            cacheRoot,
            Path.Combine(_root, "results", ProjectPreparationPaths.ManifestFileName),
            "0f0f0f0",
            null,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

    private static PreparationCacheManifest SingleCache(BuildTestGateResult result)
        => Assert.Single(result.PreparationManifest!.Caches);

    /// <summary>
    /// Runs the fixture repository's own build command the way a coding run
    /// does: through a POSIX shell that carries the run's environment.
    /// </summary>
    private async Task<int> RunBuildCommandAsync(IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = PosixShell.RequirePath(),
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(BuildCommand);
        start.Environment.Remove("NUGET_PACKAGES");
        PreparationCommandEnvironment.ApplyTo(start.Environment, environment);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The fixture build command did not start.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>
    /// The fixture repository: a prepare script that restores one package into
    /// whatever <c>NUGET_PACKAGES</c> names, a build command that requires that
    /// package, and a project file whose unique content keeps the cache key (and
    /// therefore the published entry) private to this test.
    /// </summary>
    private void WriteRestoringRepository()
    {
        Write("Fake.csproj", $"<Project><!-- {Guid.NewGuid():N} --></Project>\n");
        Write(".agent-studio/project.yml", $"""
            schemaVersion: 1
            stack: [dotnet]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - {BuildCommand}
              test:
              lint:
            testSuites:
            cachePaths:
            capabilities: [linux]
            environment:
              CI: "true"
            """);
        // POSIX sh, not bash: the prepare script runs under /bin/sh, which is
        // dash on Debian-family hosts. tr normalizes the Windows separators a
        // native cache path carries so Git Bash resolves the same directory.
        Write(".agent-studio/prepare", """
            #!/bin/sh
            set -e
            [ -n "$NUGET_PACKAGES" ] || { echo 'NUGET_PACKAGES was not set for the prepare process' 1>&2; exit 3; }
            packages=$(printf '%s' "$NUGET_PACKAGES" | tr '\\' '/')
            mkdir -p "$packages/fake.package/1.0.0"
            printf 'restored\n' > "$packages/fake.package/1.0.0/fake.package.nupkg"
            """);
    }

    private const string BuildCommand =
        "test -n \"$NUGET_PACKAGES\" && test -f \"$(printf '%s' \"$NUGET_PACKAGES\" | tr '\\\\' '/')/"
        + PackageFile + "\"";

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
