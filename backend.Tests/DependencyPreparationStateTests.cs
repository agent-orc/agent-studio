using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2720/CAC-18 root cause: a cache entry whose stamped marker matches the
/// current lock hash can still be a corrupted, partial <c>node_modules</c> tree
/// (2,580 of 25,748 files, missing <c>.package-lock.json</c>) when npm's own
/// install was itself interrupted on a broken host filesystem. The read-side
/// check is opt-in (<c>requireInstallCompleteMarker</c>) so the same contract's
/// other consumer - Remote Review preparation commands that never run
/// <c>npm ci</c> - keeps its existing, install-tool-agnostic behavior.
/// </summary>
public sealed class DependencyPreparationStateTests
{
    private static ReviewDependencyScopeDto Scope() => new("", ["package-lock.json"]);

    private static string SeedInstallRoot(string root, bool stampMarker, bool writeNpmCompleteMarker)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package-lock.json"), "lock-v1");
        var nodeModules = Path.Combine(root, DependencyPreparationState.DependencyDirectoryName);
        Directory.CreateDirectory(nodeModules);
        if (writeNpmCompleteMarker)
            File.WriteAllText(
                Path.Combine(nodeModules, DependencyPreparationState.NpmInstallCompleteMarkerName),
                "{}");
        if (stampMarker)
        {
            var hash = DependencyPreparationState.ComputeLockHash(root, ["package-lock.json"]);
            DependencyPreparationState.Stamp(root, hash);
        }
        return root;
    }

    [Fact]
    public void MissingNpmCompleteMarker_WithRequireFlag_IsMissEvenWhenStampMatches()
    {
        var root = SeedInstallRoot(
            Path.Combine(Path.GetTempPath(), "dep-prep-" + Path.GetRandomFileName()),
            stampMarker: true,
            writeNpmCompleteMarker: false);
        try
        {
            var decision = DependencyPreparationState.Evaluate(root, Scope(), requireInstallCompleteMarker: true);

            Assert.Equal("miss", decision.State);
            Assert.Equal("npm-install-incomplete", decision.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingNpmCompleteMarker_WithoutRequireFlag_StillHits()
    {
        // Remote Review's generic preparation commands never opt in, so a
        // corrupted-looking tree by this new signal must not regress their
        // existing cache-hit behavior.
        var root = SeedInstallRoot(
            Path.Combine(Path.GetTempPath(), "dep-prep-" + Path.GetRandomFileName()),
            stampMarker: true,
            writeNpmCompleteMarker: false);
        try
        {
            var decision = DependencyPreparationState.Evaluate(root, Scope());

            Assert.Equal("hit", decision.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NpmCompleteMarkerPresent_WithRequireFlag_Hits()
    {
        var root = SeedInstallRoot(
            Path.Combine(Path.GetTempPath(), "dep-prep-" + Path.GetRandomFileName()),
            stampMarker: true,
            writeNpmCompleteMarker: true);
        try
        {
            var decision = DependencyPreparationState.Evaluate(root, Scope(), requireInstallCompleteMarker: true);

            Assert.Equal("hit", decision.State);
            Assert.Equal("lock-unchanged", decision.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// Coverage for the AGT-2720 transactional save and eviction contract on
/// <see cref="DependencyCacheSession"/>.
/// </summary>
public sealed class DependencyCacheSessionTests
{
    private static DependencyCacheSession Session(string cacheParent, string repositoryPath, string workspace)
        => DependencyCacheSession.Create(
            cacheParent,
            repositoryPath,
            workspace,
            [new ReviewDependencyScopeDto("", ["package-lock.json"])]);

    private static void WriteNodeModulesMarker(string workspace, string content)
    {
        var nodeModules = Path.Combine(workspace, "node_modules");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "sentinel.txt"), content);
    }

    [Fact]
    public void InterruptedSave_StagingArtifactAlone_NeverCorruptsThePreviousEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "dep-cache-" + Path.GetRandomFileName());
        var cacheParent = Path.Combine(root, "cache");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            WriteNodeModulesMarker(workspace, "v1");
            Session(cacheParent, "repo-under-test", workspace).Save();

            var cacheRoot = DependencyCacheSession.CachePath(cacheParent, "repo-under-test");
            var destination = Path.Combine(cacheRoot, "content", "node_modules");
            Assert.Equal("v1", File.ReadAllText(Path.Combine(destination, "sentinel.txt")));

            // Simulate a process kill AFTER the first move (workspace -> staging)
            // but BEFORE the second (staging -> destination): only the staging
            // sibling exists with the would-be-next content, and the current
            // entry has not been touched.
            var staging = destination + ".incoming";
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "sentinel.txt"), "v2-interrupted");

            Assert.Equal("v1", File.ReadAllText(Path.Combine(destination, "sentinel.txt")));

            // The next real save cleans up the orphaned staging directory and
            // replaces the entry atomically rather than merging with it.
            Directory.Delete(workspace, recursive: true);
            Directory.CreateDirectory(workspace);
            WriteNodeModulesMarker(workspace, "v3");
            Session(cacheParent, "repo-under-test", workspace).Save();

            Assert.Equal("v3", File.ReadAllText(Path.Combine(destination, "sentinel.txt")));
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Evict_MovesContentAsideAndForcesTheNextEvaluateToMiss()
    {
        var root = Path.Combine(Path.GetTempPath(), "dep-cache-" + Path.GetRandomFileName());
        var cacheParent = Path.Combine(root, "cache");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            WriteNodeModulesMarker(workspace, "corrupted");
            var session = Session(cacheParent, "repo-under-test", workspace);
            session.Save();

            var cacheRoot = DependencyCacheSession.CachePath(cacheParent, "repo-under-test");
            Assert.True(Directory.Exists(Path.Combine(cacheRoot, "content")));

            var messages = session.Evict("toolchain-startup-failure");

            Assert.Contains(messages, m => m.Contains("dependency-cache evicted")
                && m.Contains("reason=toolchain-startup-failure")
                && m.Contains("state=moved"));
            Assert.False(Directory.Exists(Path.Combine(cacheRoot, "content")));
            Assert.True(Directory.Exists(cacheRoot + "-evicted"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Evict_WithNoCachedContent_IsNoopAndReportsAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "dep-cache-" + Path.GetRandomFileName());
        var cacheParent = Path.Combine(root, "cache");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            var messages = Session(cacheParent, "repo-under-test", workspace).Evict("toolchain-startup-failure");

            Assert.Contains(messages, m => m.Contains("state=absent"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
