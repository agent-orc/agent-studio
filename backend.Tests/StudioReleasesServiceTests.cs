using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Deploy;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StudioReleasesServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-release-service-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private string Repo => Path.Combine(_root, "repo");
    private string Watch => Path.Combine(_root, "cards");

    [Fact]
    public void Remembers_running_revisions_without_deploy_reports_and_enriches_waiting_cards()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Watch);
        Git("init", "-qb", "develop");
        Git("config", "user.name", "Fixture");
        Git("config", "user.email", "fixture@example.invalid");
        Git("config", "commit.gpgsign", "false");
        var baseline = Commit("baseline");
        var delivery = Commit("fix");
        var folder = Path.Combine(Watch, "6-completed", "fix");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new {
            id = "fix", key = "DEMO-1", title = "Original card title", taskType = "bug", state = "6-completed",
            createdAt = _clock.GetUtcNow(), agent = "codex",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "fixture");
        IntegrationVerificationStore.Write(folder, new IntegrationVerificationRecord {
            State = IntegrationVerificationStates.Verified, Sha = delivery, IntegrationBranch = "develop",
            DeliveryShas = [delivery],
            RecordedAtUtc = _clock.GetUtcNow().AddHours(-9),
        });
        var legacy = Path.Combine(Watch, "6-completed", "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "task.json"), JsonSerializer.Serialize(new {
            id = "legacy", key = "DEMO-2", title = "Older delivery with a later verification",
            state = "6-completed", createdAt = _clock.GetUtcNow(), agent = "codex",
        }));
        File.WriteAllText(Path.Combine(legacy, "prompt.md"), "fixture");
        IntegrationVerificationStore.Write(legacy, new IntegrationVerificationRecord {
            State = IntegrationVerificationStates.Verified, Sha = delivery, IntegrationBranch = "develop",
            RecordedAtUtc = _clock.GetUtcNow().AddHours(-1),
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["TaskRepository"] = Path.Combine(_root, "state"), ["InstanceDeploy:SourceProject"] = "demo",
            ["WatchPaths:0:Name"] = "demo", ["WatchPaths:0:Path"] = Watch,
            ["WatchPaths:0:RootPath"] = Repo, ["WatchPaths:0:RepositoryPath"] = Repo,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetIntegrationBranch("demo", "develop");
        var attribution = new ReleaseCardAttributionSource(scanner);
        var store = new InstanceDeployStore(config);
        var instance = new InstanceDeployService(store, git, scanner, attribution, config, _clock,
            NullLogger<InstanceDeployService>.Instance, settings);
        StudioReleasesService Service(string running) => new(instance, store, attribution, scanner, git, settings,
            Identity(running), config, _clock);
        var first = Service(baseline);
        first.Refresh();
        var waiting = Assert.Single(first.View!.Pending);
        Assert.Equal("Original card title", waiting.Title);
        Assert.Equal("bug", waiting.Type);
        Assert.Equal(_clock.GetUtcNow().AddHours(-9).UtcDateTime, waiting.IntegratedAtUtc);
        Assert.Equal("unknown", first.View.CardStates["DEMO-2"].State);
        Assert.True(Assert.Single(first.View.Releases).Observed);
        Assert.Empty(store.Read().Releases);
        // The older bootstrap used build time. A real observation is stronger time evidence.
        store.Save(new InstanceDeployState { Releases = [new InstanceRelease {
            Id = "baseline", Version = "1.0.0", CommitSha = baseline,
            CreatedAtUtc = _clock.GetUtcNow().AddHours(-3).UtcDateTime,
            LiveSinceUtc = _clock.GetUtcNow().AddHours(-3).UtcDateTime, FromVersionRoute = true,
        }] });
        first.Refresh();
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, Assert.Single(first.View!.Releases).AtUtc);
        _clock.Advance(TimeSpan.FromHours(1));
        var nextProcess = Service(delivery);
        nextProcess.Refresh();
        Assert.Equal(2, nextProcess.View!.Releases.Count);
        Assert.Empty(nextProcess.View.Pending);
        Assert.Equal("DEMO-1", Assert.Single(nextProcess.View.Releases[1].Cards).Key);
        Assert.Equal("live", nextProcess.View.CardStates["DEMO-1"].State);
        Assert.Equal("live", nextProcess.View.CardStates["DEMO-2"].State);
        Assert.Equal("live", nextProcess.Card("demo", "DEMO-1")!.State);
        Assert.Null(nextProcess.Card("another-project", "DEMO-1"));
        nextProcess.Refresh();
        Assert.Equal(2, nextProcess.View.Releases.Count);
        nextProcess.Unavailable("fixture unavailable");
        Assert.Empty(nextProcess.View.CardStates);
        Assert.Equal("fixture unavailable", nextProcess.View.Detail);
    }

    private BuildIdentity Identity(string commit)
    {
        var artifact = new ReleaseArtifactIdentity("fixture", "1.0.0", "v1.0.0", commit, "fixture");
        return new(1, "Agent Studio", "v1.0.0", "1.0.0", commit, false, _clock.GetUtcNow(), "fixture", artifact, artifact);
    }
    private string Commit(string message) { Git("commit", "--allow-empty", "-qm", message); return Git("rev-parse", "HEAD"); }
    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
