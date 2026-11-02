using AgentStudio.State;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3401: machine-local state resolves to an absolute, existing folder,
/// an unusable folder stops the host at startup, and state that earlier
/// versions wrote relative to the working directory moves there once.
/// </summary>
public sealed class LocalStateStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "local-state-startup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_missing_folder_is_created_and_returned_as_an_absolute_path()
    {
        var missing = Path.Combine(_root, "home", ".local", "share");

        var resolved = LocalStateRoot.Ensure(missing);

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(Path.GetFullPath(missing), resolved);
        Assert.True(Directory.Exists(resolved));
    }

    [Fact]
    public void The_process_root_is_absolute_and_exists()
    {
        var resolved = LocalStateRoot.Resolve();

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.True(Directory.Exists(resolved));
        Assert.StartsWith(resolved, LocalStateRoot.Combine("agentstudio", "batch-gates"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/state")]
    public void An_empty_or_relative_location_is_refused(string? candidate)
    {
        var error = Assert.Throws<LocalStateRootUnavailableException>(() => LocalStateRoot.Ensure(candidate));

        Assert.Contains("HOME", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "relative")));
    }

    [Fact]
    public void A_location_that_cannot_be_created_is_refused_with_its_path()
    {
        var blocker = Path.Combine(_root, "blocker");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blocker, "a file, not a directory");
        var impossible = Path.Combine(blocker, "home", ".local", "share");

        var error = Assert.Throws<LocalStateRootUnavailableException>(() => LocalStateRoot.Ensure(impossible));

        Assert.Contains(impossible, error.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be created", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_api_stops_at_startup_when_the_location_cannot_be_created()
    {
        // GetFolderPath reads HOME and XDG_DATA_HOME only on Unix.
        if (OperatingSystem.IsWindows()) return;
        var blocker = Path.Combine(_root, "blocker");
        var workingDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(blocker, "a file, not a directory");
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = Path.Combine(blocker, "home"),
            ["XDG_DATA_HOME"] = null,
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["TaskRepository"] = Path.Combine(_root, "workspace"),
        };

        using var api = BuiltProcessLauncher.StartBuiltIn(
            RepositoryRoot.Find(), workingDirectory, "backend", "OrchestratorApi.dll", environment);
        await api.WaitForExitAsync(TimeSpan.FromSeconds(90));

        Assert.True(api.Process.ExitCode == 1, api.ToString());
        Assert.True(api.Contains("local-state-startup-failed"), api.ToString());
        Assert.True(api.Contains(Path.Combine(blocker, "home", ".local", "share")), api.ToString());
        Assert.False(Directory.Exists(Path.Combine(workingDirectory, "agentstudio")));
        Assert.False(Directory.Exists(Path.Combine(workingDirectory, "agent-taskboard")));
    }

    [Fact]
    public void Legacy_relative_state_moves_once_and_keeps_newer_state()
    {
        var legacyBase = Path.Combine(_root, "app");
        var localState = Path.Combine(_root, "home", ".local", "share");
        Write(Path.Combine(legacyBase, "agentstudio", "batch-gates", "batch-1", "state.json"), "batch");
        Write(Path.Combine(legacyBase, "agentstudio", "gate-results", "demo", "entry.json"), "old cache");
        Write(Path.Combine(legacyBase, "agent-taskboard", "model-routing-state.json"), "routing");
        Write(Path.Combine(localState, "agentstudio", "gate-results", "demo", "entry.json"), "new cache");

        var moves = LocalStateStartup.MigrateLegacy(localState, [legacyBase], NullLogger.Instance);

        Assert.Equal(
            new[]
            {
                Path.Combine(localState, "agent-taskboard", "model-routing-state.json"),
                Path.Combine(localState, "agentstudio", "batch-gates"),
            },
            moves.Select(move => move.Target).Order(StringComparer.Ordinal));
        Assert.Equal("batch", File.ReadAllText(Path.Combine(localState, "agentstudio", "batch-gates", "batch-1", "state.json")));
        Assert.Equal("routing", File.ReadAllText(Path.Combine(localState, "agent-taskboard", "model-routing-state.json")));
        Assert.Equal("new cache", File.ReadAllText(Path.Combine(localState, "agentstudio", "gate-results", "demo", "entry.json")));
        Assert.False(Directory.Exists(Path.Combine(legacyBase, "agent-taskboard")));
        // The conflicting entry stays at the old location; it never overwrites newer state.
        Assert.Equal("old cache", File.ReadAllText(Path.Combine(legacyBase, "agentstudio", "gate-results", "demo", "entry.json")));

        File.WriteAllText(Path.Combine(localState, "agent-taskboard", "model-routing-state.json"), "routing, later");
        var second = LocalStateStartup.MigrateLegacy(localState, [legacyBase], NullLogger.Instance);

        Assert.Empty(second);
        Assert.Equal("routing, later", File.ReadAllText(Path.Combine(localState, "agent-taskboard", "model-routing-state.json")));
    }

    [Fact]
    public void A_checkout_or_the_root_itself_is_never_migrated()
    {
        var legacyBase = Path.Combine(_root, "projects");
        var localState = Path.Combine(_root, "home", ".local", "share");
        Write(Path.Combine(legacyBase, "agent-taskboard", ".git", "HEAD"), "ref: refs/heads/main");
        Write(Path.Combine(localState, "agentstudio", "post-merge", "demo.json"), "{}");

        var moves = LocalStateStartup.MigrateLegacy(localState, [legacyBase, localState, null, ""], NullLogger.Instance);

        Assert.Empty(moves);
        Assert.True(File.Exists(Path.Combine(legacyBase, "agent-taskboard", ".git", "HEAD")));
        Assert.True(File.Exists(Path.Combine(localState, "agentstudio", "post-merge", "demo.json")));
    }

    [Theory]
    [InlineData(false, false, LegacyLocalStateFolderDecision.Migrate)]
    [InlineData(false, true, LegacyLocalStateFolderDecision.SkipCheckout)]
    [InlineData(true, false, LegacyLocalStateFolderDecision.NotLegacy)]
    [InlineData(true, true, LegacyLocalStateFolderDecision.NotLegacy)]
    public void Folder_decision_matrix(bool sameLocation, bool isCheckout, LegacyLocalStateFolderDecision expected)
        => Assert.Equal(expected, LegacyLocalStatePolicy.DecideFolder(sameLocation, isCheckout));

    [Theory]
    [InlineData(false, LegacyLocalStateEntryDecision.Move)]
    [InlineData(true, LegacyLocalStateEntryDecision.KeepTarget)]
    public void Entry_decision_matrix(bool targetExists, LegacyLocalStateEntryDecision expected)
        => Assert.Equal(expected, LegacyLocalStatePolicy.DecideEntry(targetExists));

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
