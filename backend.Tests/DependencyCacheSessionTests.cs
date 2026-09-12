using AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// CAC-18: the dependency cache that heals instead of serving a poisoned tree
/// forever. <see cref="DependencyPreparationState"/> already treats a missing
/// install-complete marker as a miss (root-cause fix item 1); these tests lock
/// that in and cover the transactional save + eviction added alongside it
/// (items 2-3).
/// </summary>
public sealed class DependencyCacheSessionTests : IDisposable
{
    private readonly string _root;

    public DependencyCacheSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentstudio-dep-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Evaluate_NodeModulesPresentWithoutInstallCompleteMarker_IsMiss()
    {
        // Reproduces the CAC-18 fixture: node_modules exists (a `Directory.Move`
        // left a tree behind) but the install never completed, so `.nm-state`
        // was never stamped. A cache entry is valid only with that marker.
        var installRoot = Path.Combine(_root, "install-root");
        Directory.CreateDirectory(Path.Combine(installRoot, "node_modules"));
        File.WriteAllText(Path.Combine(installRoot, "package-lock.json"), "{}");

        var evidence = DependencyPreparationState.Evaluate(
            installRoot, new ReviewDependencyScopeDto("", ["package-lock.json"]));

        Assert.Equal("miss", evidence.State);
        Assert.Equal("marker-missing", evidence.Reason);
    }

    [Fact]
    public void Evaluate_MarkerHashMismatch_IsMiss()
    {
        var installRoot = Path.Combine(_root, "install-root");
        Directory.CreateDirectory(Path.Combine(installRoot, "node_modules"));
        File.WriteAllText(Path.Combine(installRoot, "package-lock.json"), "{}");
        File.WriteAllText(Path.Combine(installRoot, ".nm-state"), "stale-hash-from-a-different-lockfile");

        var evidence = DependencyPreparationState.Evaluate(
            installRoot, new ReviewDependencyScopeDto("", ["package-lock.json"]));

        Assert.Equal("miss", evidence.State);
        Assert.Equal("lock-changed", evidence.Reason);
    }

    [Fact]
    public void Save_MovesWorkspaceContentIntoTheCacheEntry()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules"));
        File.WriteAllText(Path.Combine(workspace, "node_modules", "marker.txt"), "installed");
        File.WriteAllText(Path.Combine(workspace, ".nm-state"), "new-hash");
        var cacheParent = Path.Combine(_root, "cache-parent");
        var scopes = new[] { new ReviewDependencyScopeDto("", new[] { "package-lock.json" }) };

        var session = DependencyCacheSession.Create(cacheParent, "repo-identity", workspace, scopes);
        var messages = session.Save();

        Assert.Contains(messages, m => m.Contains("state=committed"));
        var contentRoot = Path.Combine(DependencyCacheSession.CachePath(cacheParent, "repo-identity"), "content");
        Assert.True(File.Exists(Path.Combine(contentRoot, "node_modules", "marker.txt")));
        Assert.Equal("new-hash", File.ReadAllText(Path.Combine(contentRoot, ".nm-state")));
        // A committed save leaves no staging siblings behind.
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(DependencyCacheSession.CachePath(cacheParent, "repo-identity")),
            d => Path.GetFileName(d).StartsWith("content.saving-", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_WhenStagingCannotBeWritten_LeavesThePreviousEntryIntact()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "node_modules"));
        File.WriteAllText(Path.Combine(workspace, "node_modules", "marker.txt"), "new-install");
        File.WriteAllText(Path.Combine(workspace, ".nm-state"), "new-hash");
        var cacheParent = Path.Combine(_root, "cache-parent");
        var scopes = new[] { new ReviewDependencyScopeDto("", new[] { "package-lock.json" }) };

        // Seed a previously-saved, valid entry.
        var cacheRoot = DependencyCacheSession.CachePath(cacheParent, "repo-identity");
        var contentRoot = Path.Combine(cacheRoot, "content");
        Directory.CreateDirectory(Path.Combine(contentRoot, "node_modules"));
        File.WriteAllText(Path.Combine(contentRoot, "node_modules", "marker.txt"), "old-install");
        File.WriteAllText(Path.Combine(contentRoot, ".nm-state"), "old-hash");

        // A file at the injected staging path makes Directory.CreateDirectory
        // fail with IOException on both Windows and Unix. This exercises the
        // real staging-write failure path without relying on Unix permissions.
        var blockedStagingRoot = Path.Combine(cacheRoot, "blocked-staging");
        File.WriteAllText(blockedStagingRoot, "blocks the staging directory");
        var session = DependencyCacheSession.Create(
            cacheParent,
            "repo-identity",
            workspace,
            scopes,
            _ => blockedStagingRoot);

        var messages = session.Save();
        Assert.Contains(
            messages,
            m => m.Contains("save item=node_modules state=failed reason=IOException"));
        Assert.Contains(messages, m => m.Contains("state=aborted"));

        Assert.True(File.Exists(Path.Combine(contentRoot, "node_modules", "marker.txt")));
        Assert.Equal("old-install", File.ReadAllText(Path.Combine(contentRoot, "node_modules", "marker.txt")));
        Assert.Equal("old-hash", File.ReadAllText(Path.Combine(contentRoot, ".nm-state")));
    }

    [Fact]
    public void Discard_RemovesTheEntry_SoTheNextRestoreIsADeterministicMiss()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var cacheParent = Path.Combine(_root, "cache-parent");
        var scopes = new[] { new ReviewDependencyScopeDto("", new[] { "package-lock.json" }) };

        var cacheRoot = DependencyCacheSession.CachePath(cacheParent, "repo-identity");
        var contentRoot = Path.Combine(cacheRoot, "content");
        Directory.CreateDirectory(Path.Combine(contentRoot, "node_modules"));
        File.WriteAllText(Path.Combine(contentRoot, "node_modules", "vite-client.placeholder"), "poisoned");
        File.WriteAllText(Path.Combine(contentRoot, ".nm-state"), "hash-of-a-lockfile-that-never-finished-installing");

        var session = DependencyCacheSession.Create(cacheParent, "repo-identity", workspace, scopes);
        var messages = session.Discard("gate-environment-failure");

        Assert.Contains(messages, m => m.Contains("dependency-cache evicted") && m.Contains("gate-environment-failure"));
        Assert.False(Directory.Exists(contentRoot));

        // Restoring after eviction moves nothing - the workspace is untouched,
        // so the next preparation step sees a plain miss and reinstalls.
        var restoreMessages = session.Restore();
        Assert.False(Directory.Exists(Path.Combine(workspace, "node_modules")));
        Assert.NotNull(restoreMessages);
    }

}
