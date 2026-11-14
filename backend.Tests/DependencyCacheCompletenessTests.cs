using AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3748: a review host kept restoring a torn candidate entry (no
/// <c>.bin/ng</c>, fewer top-level modules) and every frontend build failed as
/// <c>ToolUnavailable</c>. These tests use generated install trees to lock in
/// the completeness marker on save, its check on restore, and quarantine.
/// </summary>
public sealed class DependencyCacheCompletenessTests : IDisposable
{
    private const string Repository = "fixture-repository";
    private static readonly ReviewDependencyScopeDto Scope = new("app", ["package-lock.json"]);
    private readonly string _root;

    public DependencyCacheCompletenessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentstudio-dep-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string CacheParent => Path.Combine(_root, "cache");

    private string EntryRoot => DependencyCacheSession.CachePath(CacheParent, Repository, "candidate");

    private string Workspace(string name, bool installed, int modules = 5, params string[] bins)
    {
        var workspace = Path.Combine(_root, name);
        var app = Path.Combine(workspace, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "package-lock.json"), "{\"lockfileVersion\":3}");
        if (!installed) return workspace;
        var modulesDir = Path.Combine(app, "node_modules");
        for (var index = 0; index < modules; index++)
            Directory.CreateDirectory(Path.Combine(modulesDir, $"module-{index}"));
        Directory.CreateDirectory(Path.Combine(modulesDir, ".bin"));
        foreach (var bin in bins.Length == 0 ? ["tool-a", "tool-b"] : bins)
            File.WriteAllText(Path.Combine(modulesDir, ".bin", bin), "#!/bin/sh\n");
        DependencyPreparationState.Stamp(
            app, DependencyPreparationState.ComputeLockHash(app, Scope.Lockfiles));
        return workspace;
    }

    private DependencyCacheSession Session(string workspace)
        => DependencyCacheSession.Create(CacheParent, Repository, workspace, [Scope], role: "candidate");

    private void SaveCompleteEntry()
    {
        var session = Session(Workspace("producer", installed: true));
        Assert.Null(session.RecordComplete());
        Assert.Contains(session.SaveComplete(), message => message.Contains("state=committed"));
    }

    private IEnumerable<string> QuarantinedEntries()
        => Directory.Exists(Path.Combine(EntryRoot, DependencyCacheSession.QuarantineDirectoryName))
            ? Directory.EnumerateDirectories(Path.Combine(EntryRoot, DependencyCacheSession.QuarantineDirectoryName))
            : [];

    [Fact]
    public void CompleteEntry_IsRestoredAndReused()
    {
        SaveCompleteEntry();
        var workspace = Workspace("consumer", installed: false);
        var session = Session(workspace);

        session.RestoreComplete();

        Assert.True(session.Restored);
        Assert.True(File.Exists(Path.Combine(workspace, "app", "node_modules", ".bin", "tool-a")));
        Assert.Equal("hit", DependencyPreparationState.Evaluate(Path.Combine(workspace, "app"), Scope).State);
        Assert.Null(session.RecordComplete());
        Assert.Contains(session.SaveComplete(), message => message.Contains("state=committed"));
        Assert.Empty(QuarantinedEntries());
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("modules")]
    public void TornEntry_IsNotRestored_IsQuarantined_AndRebuilt(string tear)
    {
        SaveCompleteEntry();
        var content = Path.Combine(EntryRoot, "content", "app", "node_modules");
        if (tear == "bin") File.Delete(Path.Combine(content, ".bin", "tool-a"));
        else Directory.Delete(Path.Combine(content, "module-3"), recursive: true);

        var workspace = Workspace("consumer", installed: false);
        var session = Session(workspace);
        var messages = session.RestoreComplete();

        Assert.False(session.Restored);
        Assert.Contains(messages, message => message.Contains("reason=completeness-mismatch"));
        Assert.False(Directory.Exists(Path.Combine(workspace, "app", "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(EntryRoot, "content")));
        var quarantined = Assert.Single(QuarantinedEntries());
        Assert.Contains("restore-completeness-mismatch",
            File.ReadAllText(Path.Combine(quarantined, "quarantine-reason.txt")));

        // The next prepare installs fresh and saves a complete entry again.
        var rebuilt = Session(Workspace("rebuild", installed: true));
        Assert.Null(rebuilt.RecordComplete());
        Assert.Contains(rebuilt.SaveComplete(), message => message.Contains("state=committed"));
        var next = Session(Workspace("next", installed: false));
        next.RestoreComplete();
        Assert.True(next.Restored);
    }

    [Fact]
    public void EntryWithoutCompletenessMarker_IsAMiss()
    {
        var legacy = Session(Workspace("legacy", installed: true));
        Assert.Contains(legacy.Save(), message => message.Contains("state=committed"));

        var session = Session(Workspace("consumer", installed: false));
        var messages = session.RestoreComplete();

        Assert.False(session.Restored);
        Assert.Contains(messages, message => message.Contains("reason=completeness-marker-missing"));
        Assert.Single(QuarantinedEntries());
    }

    [Fact]
    public void FailedInstall_IsNeverSaved()
    {
        // An install that dropped the stamp and died halfway leaves modules
        // without a matching stamp: nothing is recorded, nothing is saved.
        var workspace = Workspace("partial", installed: true);
        File.Delete(Path.Combine(workspace, "app", DependencyPreparationState.MarkerFileName));
        var session = Session(workspace);

        Assert.Equal("install-marker-missing", session.RecordComplete());
        Assert.Contains(session.SaveComplete(), message => message.Contains("state=refused"));
        Assert.False(Directory.Exists(Path.Combine(EntryRoot, "content")));
    }

    [Fact]
    public void ReviewStepThatRemovesModules_BlocksTheSave()
    {
        var workspace = Workspace("changed", installed: true);
        var session = Session(workspace);
        Assert.Null(session.RecordComplete());
        Directory.Delete(Path.Combine(workspace, "app", "node_modules", "module-1"), recursive: true);

        Assert.Contains(session.SaveComplete(),
            message => message.Contains("reason=workspace-changed-after-prepare"));
        Assert.False(Directory.Exists(Path.Combine(EntryRoot, "content")));
    }

    [Fact]
    public void MissingToolAfterRestore_QuarantinesTheEntryOnce()
    {
        SaveCompleteEntry();
        var workspace = Workspace("consumer", installed: false);
        var session = Session(workspace);
        session.RestoreComplete();
        Assert.Null(session.RecordComplete());

        session.Quarantine("missing-tool step=verify exit=127");
        var first = session.QuarantinedPath;
        session.Quarantine("missing-tool step=verify exit=127");

        Assert.NotNull(first);
        Assert.Equal(first, session.QuarantinedPath);
        var quarantined = Assert.Single(QuarantinedEntries());
        Assert.Equal(first, quarantined);
        Assert.True(Directory.Exists(Path.Combine(quarantined, "app", "node_modules")));
        Assert.Contains("missing-tool", File.ReadAllText(Path.Combine(quarantined, "quarantine-reason.txt")));
        Assert.Contains(session.SaveComplete(), message => message.Contains("reason=quarantined"));
        Assert.False(Directory.Exists(Path.Combine(EntryRoot, "content")));
    }

    [Fact]
    public async Task ConcurrentPrepares_OneRestoresTheEntry_TheOtherMissesCleanly()
    {
        SaveCompleteEntry();
        var sessions = Enumerable.Range(0, 4)
            .Select(index => Session(Workspace($"concurrent-{index}", installed: false)))
            .ToArray();

        await Task.WhenAll(sessions.Select(session => Task.Run(() => session.RestoreComplete())));

        Assert.Single(sessions, session => session.Restored);
        Assert.Empty(QuarantinedEntries());
        foreach (var session in sessions.Where(session => !session.Restored))
            Assert.Null(session.QuarantinedPath);
    }
}
