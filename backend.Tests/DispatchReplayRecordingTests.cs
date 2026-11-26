using System.Diagnostics;
using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class DispatchReplayRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dispatch-replay-git-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordedWaveUsesGitDiffAndOnlyPreStartIndexAndLedger()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        Git("config", "user.name", "Replay fixture");
        Git("config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(_root, "a.cs"), "before");
        File.WriteAllText(Path.Combine(_root, "deleted.cs"), "before");
        Git("add", ".");
        Git("commit", "-qm", "recorded base");
        var baseSha = Git("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_root, "a.cs"), "after");
        File.Delete(Path.Combine(_root, "deleted.cs"));
        File.WriteAllText(Path.Combine(_root, "notes.md"), "documentation");
        Git("add", ".");
        Git("commit", "-qm", "A: delivery");
        var resultSha = Git("rev-parse", "HEAD").Trim();
        var storage = Path.Combine(_root, "store");
        var store = new DispatchStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Dispatch:StoragePath"] = storage }).Build());
        var early = TouchForecast.BuildIndex(baseSha, Start.AddDays(-1), ["a.cs", "deleted.cs"], []);
        store.WriteIndex("project", early);
        store.WriteIndex("project", TouchForecast.BuildIndex(resultSha, Start.AddDays(1), ["a.cs", "future.cs"],
            Enumerable.Repeat<IReadOnlyList<string>>(["a.cs", "future.cs"], 3)));
        store.AppendConflict(new("earlier", "project", "A", "develop-sync", baseSha, resultSha,
            ["a.cs"], ["B"], Start.AddHours(-1)));
        store.AppendConflict(new("future", "project", "A", "develop-sync", baseSha, resultSha,
            ["future.cs"], ["C"], Start.AddHours(1)));
        var ready = new DispatchReadyCard("A", 999, Start, [], true, DispatchForecast.Unknown, []);
        var recording = new DispatchReplayRecording("project",
            [new("wave", ready, "a.cs", Start, baseSha, resultSha)]);
        var byId = DispatchReplayCommand.Build(recording, "project", _root, store, "wave", null, null);
        var byWindow = DispatchReplayCommand.Build(recording, "project", _root, store, null, Start, Start.AddDays(1));
        Assert.Equal(early.Revision, byId.IndexRevision);
        Assert.Equal(new[] { "a.cs", "deleted.cs" }, byId.Cards.Single().RealFiles);
        Assert.Equal(new[] { "a.cs" }, byId.Cards.Single().Ready.Forecast.Files);
        Assert.Equal(byId.Cards.Single().RealFiles, byWindow.Cards.Single().RealFiles);
        Assert.Equal(byId.Cards.Single().Ready.Forecast.Files, byWindow.Cards.Single().Ready.Forecast.Files);
        Assert.Single(byId.Ledger.CollidedPairs);
        Assert.Equal("B", byId.Ledger.CollidedPairs.Single().Second);
        Assert.DoesNotContain(byId.Ledger.HotModules, module => module.Module == "future.cs");
        Assert.Throws<ArgumentException>(() => DispatchReplayCommand.Build(recording, "project", _root, store,
            null, Start.AddDays(-1), Start));
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Fixture Git command timed out.");
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
