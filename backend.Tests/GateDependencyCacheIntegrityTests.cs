using AgentStudio.TaskServer.Contracts;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2720 root cause: the gate restored <c>node_modules</c> from a shared
/// dependency-cache entry that had been truncated by an interrupted save, kept a
/// valid <c>.nm-state</c> hash, and therefore reported
/// <c>dependency-cache hit reason=lock-unchanged</c> forever. The coding-agent-chat
/// entry held 2,580 of 25,748 files, <c>vite/dist/client/</c> was empty and
/// <c>node_modules/.package-lock.json</c> was gone; every pre-main gate since
/// 2026-08-10 died inside vite before the first test while the Linux review host,
/// which installs fresh, passed the same suite 412 times.
///
/// These tests pin the two structural properties that make that state impossible:
/// a hit requires proof of a completed install, and a save is transactional.
/// </summary>
public sealed class GateDependencyCacheIntegrityTests : IDisposable
{
    private readonly string _root;

    public GateDependencyCacheIntegrityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-dep-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Evaluate_CompleteNpmInstall_IsHit()
    {
        var install = SeedInstalledScope(installComplete: true, marker: true);

        var decision = DependencyPreparationState.Evaluate(install, NpmScope());

        Assert.Equal("hit", decision.State);
        Assert.Equal("lock-unchanged", decision.Reason);
    }

    [Fact]
    public void Evaluate_TruncatedTreeWithValidMarker_IsMissNotHit()
    {
        // The exact CAC-18 shape: hash marker present and matching, node_modules
        // populated, but the install-completion file gone. Before the fix this
        // returned "hit" and skipped `npm ci` on a broken tree.
        var install = SeedInstalledScope(installComplete: false, marker: true);

        var decision = DependencyPreparationState.Evaluate(install, NpmScope());

        Assert.Equal("miss", decision.State);
        Assert.Equal("install-incomplete", decision.Reason);
    }

    [Fact]
    public void Evaluate_MissingMarker_IsMiss()
    {
        var install = SeedInstalledScope(installComplete: true, marker: false);

        var decision = DependencyPreparationState.Evaluate(install, NpmScope());

        Assert.Equal("miss", decision.State);
        Assert.Equal("marker-missing", decision.Reason);
    }

    [Fact]
    public void Evaluate_NonNpmLockfile_DoesNotRequireTheNpmCompletionFile()
    {
        // Only npm writes node_modules/.package-lock.json. A scope locked by a
        // different package manager must not be declared incomplete for missing
        // a file its toolchain never creates.
        var install = Path.Combine(_root, "pnpm-scope");
        Directory.CreateDirectory(Path.Combine(install, "node_modules"));
        File.WriteAllText(Path.Combine(install, "pnpm-lock.yaml"), "lockfileVersion: 9");
        var scope = new ReviewDependencyScopeDto("", ["pnpm-lock.yaml"]);
        DependencyPreparationState.Stamp(
            install,
            DependencyPreparationState.ComputeLockHash(install, ["pnpm-lock.yaml"]));

        var decision = DependencyPreparationState.Evaluate(install, scope);

        Assert.Equal("hit", decision.State);
    }

    [Fact]
    public void SaveThenRestore_RoundTripsACompleteInstall()
    {
        var workspace = SeedInstalledScope(installComplete: true, marker: true, name: "workspace");
        var session = NewSession(workspace);

        session.Save();
        Assert.False(Directory.Exists(Path.Combine(workspace, "node_modules")));

        var restored = NewSession(workspace);
        restored.Restore();

        Assert.Equal(
            "hit",
            DependencyPreparationState.Evaluate(workspace, NpmScope()).State);
    }

    [Fact]
    public void InterruptedSave_LeavesThePreviousEntryIntact()
    {
        // A save that cannot transfer every item must discard its staging tree
        // rather than publish it. The transfer failure is injected portably by
        // occupying the staging path with a file, so every move throws exactly
        // where the real interrupted save died.
        var workspace = SeedInstalledScope(installComplete: true, marker: true, name: "workspace");
        NewSession(workspace).Save();
        var entry = Path.Combine(CacheRoot(), "content", "node_modules");
        var goodFileCount = Directory.GetFiles(entry, "*", SearchOption.AllDirectories).Length;
        Assert.True(goodFileCount > 0);

        SeedInstalledScope(installComplete: true, marker: true, name: "workspace");
        File.WriteAllText(Path.Combine(CacheRoot(), "content.incoming"), "not a directory");

        var messages = NewSession(workspace).Save();

        Assert.Contains(messages, message => message.Contains("state=discarded", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("state=promoted", StringComparison.Ordinal));
        Assert.True(Directory.Exists(entry));
        Assert.Equal(
            goodFileCount,
            Directory.GetFiles(entry, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Evict_DropsTheEntryAndNamesTheReason()
    {
        var workspace = SeedInstalledScope(installComplete: true, marker: true, name: "workspace");
        var session = NewSession(workspace);
        session.Save();
        Assert.True(Directory.Exists(Path.Combine(CacheRoot(), "content")));

        var messages = session.Evict("gate environment: vite case-insensitive FS probe failed");

        Assert.False(Directory.Exists(Path.Combine(CacheRoot(), "content")));
        Assert.Contains(
            messages,
            message => message.StartsWith("dependency-cache evicted", StringComparison.Ordinal)
                       && message.Contains("reason=gate-environment", StringComparison.Ordinal));
    }

    [Fact]
    public void GateEnvironmentFailure_EvictsInsteadOfSavingTheSuspectTree()
    {
        // The loop that kept CAC-18 red: a gate ran on a broken entry, failed in
        // vite, and then SAVED that same workspace back as the entry. On this
        // failure class the gate must evict instead, so the retry reinstalls.
        var workspace = SeedInstalledScope(installComplete: true, marker: true, name: "workspace");
        var session = GateSession(workspace);
        session.Save();
        Assert.True(Directory.Exists(Path.Combine(GateCacheRoot(), "content")));

        var reseeded = SeedInstalledScope(installComplete: false, marker: true, name: "workspace");
        Assert.Equal(reseeded, workspace);
        var failed = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 5, string.Empty,
            "gate environment: vite case-insensitive FS probe failed", false, true)
        {
            FailureKind = BuildTestGateFailureKind.GateEnvironment,
            GateEnvironmentReason = "gate environment: vite case-insensitive FS probe failed",
        };

        var recorded = BuildTestGateRunner.SaveDependencyCache(failed, GateSession(workspace));

        Assert.False(Directory.Exists(Path.Combine(GateCacheRoot(), "content")));
        Assert.Contains("dependency-cache evicted", recorded.Output, StringComparison.Ordinal);
        Assert.Contains("reason=gate-environment", recorded.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryProductFailure_StillSavesTheEntry()
    {
        // Only the toolchain class evicts. A red suite is a verdict about the
        // delivery and must keep its warm dependency cache.
        var workspace = SeedInstalledScope(installComplete: true, marker: true, name: "workspace");
        var failed = new BuildTestGateResult(
            BuildTestGateVerdict.Fail, 1, 5, string.Empty, "3 tests failed", false, true)
        {
            FailureKind = BuildTestGateFailureKind.Code,
        };

        var recorded = BuildTestGateRunner.SaveDependencyCache(failed, GateSession(workspace));

        Assert.True(Directory.Exists(Path.Combine(GateCacheRoot(), "content", "node_modules")));
        Assert.DoesNotContain("dependency-cache evicted", recorded.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoredTruncatedEntry_ForcesAFreshInstall()
    {
        // End-to-end shape of the incident: a corrupted entry is restored into a
        // fresh workspace and the gate must decide to install, not to trust it.
        var seeded = SeedInstalledScope(installComplete: false, marker: true, name: "seed");
        NewSession(seeded).Save();

        var workspace = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "package-lock.json"), LockContent);
        NewSession(workspace).Restore();

        var decision = DependencyPreparationState.Evaluate(workspace, NpmScope());

        Assert.True(Directory.Exists(Path.Combine(workspace, "node_modules")));
        Assert.Equal("miss", decision.State);
        Assert.Equal("install-incomplete", decision.Reason);
    }

    private const string LockContent = "{\"lockfileVersion\":3,\"name\":\"coding-agent-chat\"}";

    private static ReviewDependencyScopeDto NpmScope() => new("", ["package-lock.json"]);

    private string CacheRoot()
        => DependencyCacheSession.CachePath(Path.Combine(_root, "cache"), "repo-under-test");

    /// <summary>
    /// The pipeline adapter the gate itself uses, rooted inside the test's own
    /// review-workspace directory so nothing touches the machine-wide cache.
    /// </summary>
    private GateDependencyCacheSession GateSession(string workspace)
        => GateDependencyCacheSession.Create(
            Path.Combine(_root, "review-gates"),
            "repo-under-test",
            workspace,
            [
                new GatePreparationCommand(
                    VerifyEcosystem.Node,
                    "",
                    "npm ci",
                    VerifyCommandShell.Platform,
                    [new GateDependencyScope("", ["package-lock.json"])]),
            ],
            [],
            NullLogger<GateDependencyCacheIntegrityTests>.Instance);

    private string GateCacheRoot()
        => GateDependencyCacheSession.CachePath(
            Path.Combine(_root, "review-gates"),
            "repo-under-test");

    private DependencyCacheSession NewSession(string workspace)
        => DependencyCacheSession.Create(
            Path.Combine(_root, "cache"),
            "repo-under-test",
            workspace,
            [NpmScope()]);

    /// <summary>
    /// Builds a scope that looks like a real npm install: a lockfile, a
    /// node_modules tree with a vite package, the hash marker, and - unless the
    /// caller asks for the corrupted shape - the install-completion file.
    /// </summary>
    private string SeedInstalledScope(bool installComplete, bool marker, string name = "scope")
    {
        var install = Path.Combine(_root, name);
        var viteClient = Path.Combine(install, "node_modules", "vite", "dist", "client");
        Directory.CreateDirectory(viteClient);
        File.WriteAllText(Path.Combine(install, "package-lock.json"), LockContent);
        File.WriteAllText(Path.Combine(viteClient, "client.mjs"), "export {}");
        File.WriteAllText(
            Path.Combine(install, "node_modules", "vite", "package.json"),
            "{\"name\":\"vite\"}");
        if (installComplete)
        {
            File.WriteAllText(
                Path.Combine(install, "node_modules", DependencyPreparationState.InstallCompletionFileName),
                "{\"lockfileVersion\":3}");
        }
        if (marker)
        {
            DependencyPreparationState.Stamp(
                install,
                DependencyPreparationState.ComputeLockHash(install, ["package-lock.json"]));
        }
        return install;
    }
}
