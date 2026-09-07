using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// AGT-2720 - the dependency cache must never report a usable tree it cannot
/// prove. The CAC-18 entry kept a matching lock hash beside a tree of 2,580 of
/// 25,748 files, so 412 green remote reviews still failed every pre-main gate
/// inside vite. These tests pin the three structural guarantees: a tree without
/// install proof is a miss, an interrupted save leaves the previous entry intact,
/// and an evicted scope is neither restored nor written back.
/// </summary>
public sealed class DependencyCacheIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agentstudio-dependency-cache-tests",
        Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the temp sweep owns whatever survives */ }
    }

    [Fact]
    public void Completed_install_is_a_hit()
    {
        var installRoot = InstallRoot("workspace");
        SeedLockfile(installRoot, "{\"lockfileVersion\":3}");
        SeedCompletedInstall(installRoot);

        var decision = Evaluate(installRoot);

        Assert.Equal("hit", decision.State);
        Assert.Equal("lock-unchanged", decision.Reason);
    }

    [Fact]
    public void Tree_without_the_npm_completion_file_is_a_miss()
    {
        var installRoot = InstallRoot("workspace");
        SeedLockfile(installRoot, "{\"lockfileVersion\":3}");
        SeedCompletedInstall(installRoot);
        // Exactly the CAC-18 entry: the marker still matches, the tree does not.
        File.Delete(Path.Combine(
            installRoot,
            DependencyPreparationState.DependencyDirectoryName,
            DependencyPreparationState.NpmInstallCompleteFileName));

        var decision = Evaluate(installRoot);

        Assert.Equal("miss", decision.State);
        Assert.Equal("deps-incomplete", decision.Reason);
    }

    [Fact]
    public void Marker_without_an_install_completion_claim_is_a_miss()
    {
        var installRoot = InstallRoot("workspace");
        SeedLockfile(installRoot, "{\"lockfileVersion\":3}");
        SeedCompletedInstall(installRoot);
        var present = new[] { "package-lock.json" };
        // A marker in the pre-AGT-2720 bare-hash format claims nothing about the
        // install that produced the tree.
        File.WriteAllText(
            Path.Combine(installRoot, DependencyPreparationState.MarkerFileName),
            DependencyPreparationState.ComputeLockHash(installRoot, present));

        var decision = Evaluate(installRoot);

        Assert.Equal("miss", decision.State);
        Assert.Equal("install-incomplete", decision.Reason);
    }

    [Fact]
    public void Changed_lockfile_still_forces_a_reinstall()
    {
        var installRoot = InstallRoot("workspace");
        SeedLockfile(installRoot, "{\"lockfileVersion\":3}");
        SeedCompletedInstall(installRoot);
        SeedLockfile(installRoot, "{\"lockfileVersion\":3,\"changed\":true}");

        var decision = Evaluate(installRoot);

        Assert.Equal("miss", decision.State);
        Assert.Equal("lock-changed", decision.Reason);
    }

    [Fact]
    public void A_yarn_scope_is_judged_by_its_marker_alone()
    {
        var installRoot = InstallRoot("workspace");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(Path.Combine(installRoot, "yarn.lock"), "# yarn lockfile v1");
        Directory.CreateDirectory(Path.Combine(
            installRoot, DependencyPreparationState.DependencyDirectoryName));
        DependencyPreparationState.Stamp(
            installRoot,
            DependencyPreparationState.ComputeLockHash(installRoot, ["yarn.lock"]));

        var decision = DependencyPreparationState.Evaluate(
            installRoot,
            new ReviewDependencyScopeDto("", ["yarn.lock"]));

        Assert.Equal("hit", decision.State);
    }

    [Fact]
    public void An_interrupted_save_leaves_the_previous_entry_intact()
    {
        var workspace = InstallRoot("workspace");
        SeedLockfile(workspace, "{\"lockfileVersion\":3}");
        SeedCompletedInstall(workspace);
        var session = Session(workspace);
        session.Save();

        var cachedTree = Path.Combine(
            CacheEntryRoot(), DependencyPreparationState.DependencyDirectoryName);
        Assert.True(File.Exists(Path.Combine(cachedTree, "marker.txt")));

        // Reproduce the crash window of a transactional save: the previous entry
        // was already renamed aside and the incoming tree had not taken its place.
        Directory.Move(cachedTree, cachedTree + ".retired");
        Directory.CreateDirectory(cachedTree + ".incoming");
        File.WriteAllText(Path.Combine(cachedTree + ".incoming", "half-moved.txt"), "partial");

        var restored = InstallRoot("restored");
        SeedLockfile(restored, "{\"lockfileVersion\":3}");
        Session(restored).Restore();

        var restoredTree = Path.Combine(
            restored, DependencyPreparationState.DependencyDirectoryName);
        Assert.True(File.Exists(Path.Combine(restoredTree, "marker.txt")));
        Assert.False(File.Exists(Path.Combine(restoredTree, "half-moved.txt")));
        Assert.False(Directory.Exists(cachedTree + ".incoming"));
        Assert.False(Directory.Exists(cachedTree + ".retired"));
    }

    [Fact]
    public void An_evicted_scope_is_dropped_and_not_written_back()
    {
        var workspace = InstallRoot("workspace");
        SeedLockfile(workspace, "{\"lockfileVersion\":3}");
        SeedCompletedInstall(workspace);
        Session(workspace).Save();

        var restored = InstallRoot("restored");
        SeedLockfile(restored, "{\"lockfileVersion\":3}");
        var session = Session(restored);
        session.Restore();

        var messages = session.Evict("toolchain-failed-before-test-discovery");
        session.Save();

        Assert.Contains(
            messages,
            message => message.Contains("dependency-cache evicted scope=. reason=toolchain-failed-before-test-discovery", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(
            CacheEntryRoot(), DependencyPreparationState.DependencyDirectoryName)));
        Assert.False(File.Exists(Path.Combine(
            CacheEntryRoot(), DependencyPreparationState.MarkerFileName)));
    }

    private ReviewDependencyCacheEvidenceDto Evaluate(string installRoot)
        => DependencyPreparationState.Evaluate(
            installRoot,
            new ReviewDependencyScopeDto("", ["package-lock.json"]));

    private DependencyCacheSession Session(string workspace)
        => DependencyCacheSession.Create(
            Path.Combine(_root, "cache"),
            "repository-under-test",
            workspace,
            [new ReviewDependencyScopeDto("", ["package-lock.json"])]);

    private string CacheEntryRoot()
        => Path.Combine(
            DependencyCacheSession.CachePath(Path.Combine(_root, "cache"), "repository-under-test"),
            "content");

    private string InstallRoot(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SeedLockfile(string installRoot, string content)
        => File.WriteAllText(Path.Combine(installRoot, "package-lock.json"), content);

    /// <summary>Everything a successful `npm ci` leaves behind.</summary>
    private static void SeedCompletedInstall(string installRoot)
    {
        var dependencies = Path.Combine(
            installRoot, DependencyPreparationState.DependencyDirectoryName);
        Directory.CreateDirectory(dependencies);
        File.WriteAllText(
            Path.Combine(dependencies, DependencyPreparationState.NpmInstallCompleteFileName),
            "{\"lockfileVersion\":3}");
        File.WriteAllText(Path.Combine(dependencies, "marker.txt"), "installed");
        DependencyPreparationState.Stamp(
            installRoot,
            DependencyPreparationState.ComputeLockHash(installRoot, ["package-lock.json"]));
    }
}
