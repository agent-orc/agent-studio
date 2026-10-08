using AgentStudio.CliHosting;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskCleanContextStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "task-clean-context-store-tests",
        Guid.NewGuid().ToString("N"));

    private string StoreRoot => Path.Combine(_root, "store");

    [Fact]
    public void ResolveRoot_UsesUserProfileOnWindowsAndXdgStateOnLinux()
    {
        Assert.Equal(
            "C:\\Users\\runner\\.atp\\clean-context",
            TaskCleanContextStore.ResolveRoot(
                CleanContextHostPlatform.Windows,
                "C:\\Users\\runner",
                rootOverride: null,
                xdgStateHome: null));
        Assert.Equal(
            "/srv/runner-state/agent-studio/clean-context",
            TaskCleanContextStore.ResolveRoot(
                CleanContextHostPlatform.Unix,
                "/home/runner",
                rootOverride: null,
                xdgStateHome: "/srv/runner-state"));
        Assert.Equal(
            "/home/runner/.local/state/agent-studio/clean-context",
            TaskCleanContextStore.ResolveRoot(
                CleanContextHostPlatform.Unix,
                "/home/runner",
                rootOverride: null,
                xdgStateHome: null));
    }

    [Fact]
    public void Acquire_ReusesOneTaskHomeAndKeepsDifferentTasksIsolated()
    {
        var userHome = NewUserHome();
        Write(Path.Combine(userHome, ".codex", "auth.json"), "old-auth");
        Write(Path.Combine(userHome, ".codex", "config.toml"), "base-config");

        using var first = TaskCleanContextStore.Acquire("codex", "project::task-a", userHome, StoreRoot);
        Write(Path.Combine(first.HomePath, "sessions", "rollout.jsonl"), "session");
        first.Dispose();

        using var reopened = TaskCleanContextStore.Acquire("codex", "project::task-a", userHome, StoreRoot);
        using var other = TaskCleanContextStore.Acquire("codex", "project::task-b", userHome, StoreRoot);

        Assert.True(reopened.Reused);
        Assert.Equal(first.HomePath, reopened.HomePath);
        Assert.True(File.Exists(Path.Combine(reopened.HomePath, "sessions", "rollout.jsonl")));
        Assert.NotEqual(reopened.HomePath, other.HomePath);
        Assert.False(File.Exists(Path.Combine(other.HomePath, "sessions", "rollout.jsonl")));
    }

    [Fact]
    public void Acquire_RequiresTheMatchingMarkerBeforeAdoptingExistingState()
    {
        var userHome = NewUserHome();
        var home = TaskCleanContextStore.ResolveTaskHome(StoreRoot, "codex", "project::task-a");
        Directory.CreateDirectory(home);
        Write(Path.Combine(home, "sessions", "foreign.jsonl"), "foreign");

        var error = Assert.Throws<InvalidDataException>(() =>
            TaskCleanContextStore.Acquire("codex", "project::task-a", userHome, StoreRoot));

        Assert.Contains("refusing to adopt", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(home, "sessions", "foreign.jsonl")));
    }

    [Fact]
    public void CredentialIsSharedButBaseConfigRemainsAnIsolatedSnapshot()
    {
        var userHome = NewUserHome();
        var sourceAuth = Path.Combine(userHome, ".codex", "auth.json");
        var sourceConfig = Path.Combine(userHome, ".codex", "config.toml");
        Write(sourceAuth, "old-auth");
        Write(sourceConfig, "base-config");

        using var lease = TaskCleanContextStore.Acquire("codex", "project::task", userHome, StoreRoot);
        File.WriteAllText(Path.Combine(lease.HomePath, "auth.json"), "refreshed-auth");
        File.WriteAllText(Path.Combine(lease.HomePath, "config.toml"), "task-config");

        Assert.Equal("refreshed-auth", File.ReadAllText(sourceAuth));
        Assert.Equal("base-config", File.ReadAllText(sourceConfig));
    }

    [Fact]
    public void Atomic_native_refresh_detaches_hard_links_until_drained_rebind_converges_all_consumers()
    {
        var userHome = NewUserHome();
        var canonical = Path.Combine(userHome, ".codex", "auth.json");
        Write(canonical, "old-auth");
        var homes = new List<string>();
        for (var index = 0; index < 62; index++)
        {
            using var lease = TaskCleanContextStore.Acquire("codex", $"task-{index}", userHome, StoreRoot);
            homes.Add(lease.HomePath);
        }
        var replacement = canonical + ".next";
        Write(replacement, "new-auth");
        File.Move(replacement, canonical, overwrite: true);
        Assert.Contains(homes, home => File.ReadAllText(Path.Combine(home, "auth.json")) != "new-auth");

        Assert.Throws<InvalidOperationException>(() => TaskCleanContextStore.RebindProviderCredential(
            "codex", userHome, StoreRoot, () => false));
        var result = TaskCleanContextStore.RebindProviderCredential(
            "codex", userHome, StoreRoot, () => true);
        Assert.Equal(62, result);
        Assert.All(homes, home => Assert.Equal("new-auth", File.ReadAllText(Path.Combine(home, "auth.json"))));
    }

    [Fact]
    public void Rename_during_rebind_requires_retry_before_reporting_convergence()
    {
        var userHome = NewUserHome();
        var canonical = Path.Combine(userHome, ".codex", "auth.json");
        Write(canonical, "first-generation");
        var homes = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            using var lease = TaskCleanContextStore.Acquire("codex", $"race-{index}", userHome, StoreRoot);
            homes.Add(lease.HomePath);
        }

        var checks = 0;
        Assert.Throws<InvalidOperationException>(() => TaskCleanContextStore.RebindProviderCredential(
            "codex", userHome, StoreRoot, () =>
            {
                if (++checks == 3)
                {
                    var replacement = canonical + ".next";
                    Write(replacement, "second-generation");
                    File.Move(replacement, canonical, overwrite: true);
                }
                return true;
            }));

        Assert.Equal(4, TaskCleanContextStore.RebindProviderCredential(
            "codex", userHome, StoreRoot, () => true));
        Assert.All(homes, home => Assert.Equal("second-generation",
            File.ReadAllText(Path.Combine(home, "auth.json"))));
    }

    [Fact]
    public void Cleanup_DeletesOnlyExpiredHomesAndStaleIncompleteDirectories()
    {
        var userHome = NewUserHome();
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        using var expired = TaskCleanContextStore.Acquire(
            "codex",
            "project::expired",
            userHome,
            StoreRoot,
            baseline,
            TimeSpan.FromDays(30));
        using var current = TaskCleanContextStore.Acquire(
            "codex",
            "project::current",
            userHome,
            StoreRoot,
            baseline.AddDays(8),
            TimeSpan.FromDays(30));
        var incomplete = Path.Combine(StoreRoot, "codex", new string('f', 64));
        Directory.CreateDirectory(incomplete);
        Directory.SetLastWriteTimeUtc(incomplete, baseline.UtcDateTime);

        var result = TaskCleanContextStore.Cleanup(
            StoreRoot,
            baseline.AddDays(8),
            TimeSpan.FromDays(7));

        Assert.Equal(3, result.Scanned);
        Assert.Equal(2, result.Deleted);
        Assert.Empty(result.FailedPaths);
        Assert.False(Directory.Exists(expired.HomePath));
        Assert.False(Directory.Exists(incomplete));
        Assert.True(Directory.Exists(current.HomePath));
    }

    private string NewUserHome()
    {
        var path = Path.Combine(_root, "users", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }
}
