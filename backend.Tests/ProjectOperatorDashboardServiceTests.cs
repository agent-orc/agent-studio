using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectOperatorDashboardServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "project-operator-dashboard-" + Guid.NewGuid().ToString("N"));

    public ProjectOperatorDashboardServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Throughput_UsesRealCompletionLaneEvent_AndOnlyCurrentLegacyFallback()
    {
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
        var archivedWithHistory = Task("archived-history", TaskStates.Archive, now.AddHours(-1));
        var archivedLegacy = Task("archived-legacy", TaskStates.Archive, now.AddHours(-2));
        var currentLegacy = Task("current-legacy", TaskStates.Completed, now.AddHours(-3));
        var completedTwice = Task("completed-twice", TaskStates.Archive, now.AddDays(-6));
        var tooOld = Task("too-old", TaskStates.Completed, now.AddDays(-8));
        var reopened = Task("reopened", TaskStates.Ready, now.AddMinutes(-10));
        var retiredParent = Task("old-parent", TaskStates.Archive, now.AddHours(-1)) with { Kind = "epic" };
        var tasks = new[] { archivedWithHistory, archivedLegacy, currentLegacy, completedTwice, tooOld, reopened, retiredParent };

        var events = new Dictionary<string, IReadOnlyList<TimelineEvent>>(StringComparer.Ordinal)
        {
            [archivedWithHistory.Id] = [LaneChange(now.AddHours(-2), TaskStates.Completed)],
            [archivedLegacy.Id] = [],
            [currentLegacy.Id] = [],
            [completedTwice.Id] =
            [
                LaneChange(now.AddDays(-20), TaskStates.Completed),
                LaneChange(now.AddDays(-6), TaskStates.Completed),
            ],
            [tooOld.Id] = [LaneChange(now.AddDays(-8), TaskStates.Completed)],
            [reopened.Id] = [LaneChange(now.AddMinutes(-10), TaskStates.Completed)],
            [retiredParent.Id] = [LaneChange(now.AddHours(-1), TaskStates.Completed)],
        };

        var result = ProjectThroughputService.BuildSummary(
            "Demo", tasks, task => events[task.Id], now);

        Assert.Equal(now, result.CapturedAt);
        Assert.Equal(2, result.CompletedLast24h);
        Assert.Equal(2, result.CompletedLast7d);
        Assert.Equal(
            new[] { "archived-history", "current-legacy" },
            result.RecentCompletions.Select(item => item.TaskId));
        Assert.DoesNotContain(result.RecentCompletions, item => item.TaskId == archivedLegacy.Id);
        Assert.DoesNotContain(result.RecentCompletions, item => item.TaskId == reopened.Id);
        Assert.Equal(1, result.ReAcceptanceArtefactsLast7d);
    }

    [Fact]
    public void DeploymentSummary_MissingHistory_IsQuietAndUnavailable()
    {
        var (service, _, _) = BuildDeploymentStack(createRepo: false);

        var result = service.Build("Demo");

        Assert.NotNull(result);
        Assert.False(result!.Available);
        Assert.Null(result.LastDeployment);
        Assert.Null(result.PendingCount);
        Assert.Empty(result.PendingCommits);
        Assert.Empty(result.History);
        Assert.Equal(ProjectDeploymentSummaryService.SourceName, result.Source);
    }

    [Fact]
    public void DeploymentSummary_ProjectsLatestRestartIntoDeployedAndPendingRanges()
    {
        var (service, workspace, repo) = BuildDeploymentStack(createRepo: true);
        var headBefore = RevParse(repo, "HEAD");
        Commit(repo, "deployed.txt", "deployed", "Deploy payload");
        var headAfter = RevParse(repo, "HEAD");

        var logs = Path.Combine(workspace, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "stable-restarts.jsonl"), JsonSerializer.Serialize(new
        {
            ts = "2026-07-11T08:42:11Z",
            @event = "restart",
            status = "ok",
            jobsSinceLastRestart = 3,
            headBefore,
            headAfter,
            durationSeconds = 47,
            reviewCountAfter = 14,
        }) + Environment.NewLine);

        Commit(repo, "pending-one.txt", "one", "Pending one");
        Commit(repo, "pending-two.txt", "two", "Pending two");

        var result = service.Build("Demo");

        Assert.NotNull(result);
        Assert.True(result!.Available, result.Reason);
        Assert.Null(result.Reason);
        Assert.NotNull(result.LastDeployment);
        Assert.Equal("ok", result.LastDeployment!.Status);
        Assert.Equal(headBefore, result.LastDeployment.HeadBefore);
        Assert.Equal(headAfter, result.LastDeployment.HeadAfter);
        Assert.Equal(47, result.LastDeployment.DurationSeconds);
        Assert.Equal(3, result.LastDeployment.JobsSinceLastRestart);
        Assert.Equal(14, result.LastDeployment.ReviewCountAfter);
        Assert.Single(result.LastDeployment.Commits);
        Assert.Equal("Deploy payload", result.LastDeployment.Commits[0].Subject);
        Assert.Equal(2, result.PendingCount);
        Assert.Equal(new[] { "Pending two", "Pending one" }, result.PendingCommits.Select(c => c.Subject));
        Assert.Single(result.History);
        Assert.Same(result.LastDeployment, result.History[0]);
    }

    [Fact]
    public void DeploymentSummary_ReturnsBoundedNewestFirstHistory_WithoutPerRowGitEnrichment()
    {
        var (service, workspace, repo) = BuildDeploymentStack(createRepo: true);
        var headBefore = RevParse(repo, "HEAD");
        Commit(repo, "deployed.txt", "deployed", "Deploy payload");
        var headAfter = RevParse(repo, "HEAD");
        var logs = Path.Combine(workspace, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "stable-restarts.jsonl"),
        [
            JsonSerializer.Serialize(new { ts = "2026-07-10T08:00:00Z", @event = "restart", status = "ok", headBefore, headAfter, durationSeconds = 20, jobsSinceLastRestart = 1 }),
            JsonSerializer.Serialize(new { ts = "2026-07-11T08:00:00Z", @event = "restart", status = "failed", headBefore, headAfter, durationSeconds = 30, jobsSinceLastRestart = 2 }),
        ]);

        var result = service.Build("Demo");

        Assert.NotNull(result);
        Assert.Equal(2, result!.History.Count);
        Assert.Equal("failed", result.History[0].Status);
        Assert.Equal("ok", result.History[1].Status);
        Assert.NotEmpty(result.History[0].Commits);
        Assert.Empty(result.History[1].Commits);
    }

    [Fact]
    public void DeploymentHistoryReader_SkipsTornRows_AndSelectsLatestTimestamp()
    {
        var path = Path.Combine(_root, "stable-restarts.jsonl");
        File.WriteAllLines(path,
        [
            "{torn",
            "{\"ts\":123,\"event\":42,\"headBefore\":false,\"headAfter\":[]}",
            "{\"ts\":\"2026-07-11T10:00:00Z\",\"event\":\"probe\",\"headBefore\":\"a\",\"headAfter\":\"b\"}",
            "{\"ts\":\"2026-07-11T11:00:00Z\",\"event\":\"restart\",\"status\":\"failed\",\"headBefore\":\"c\",\"headAfter\":\"d\"}",
            "{\"ts\":\"2026-07-11T09:00:00Z\",\"event\":\"restart\",\"status\":\"ok\",\"headBefore\":\"e\",\"headAfter\":\"f\"}",
        ]);

        var row = ProjectDeploymentSummaryService.ReadLatestRestart(path);

        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("c", row.HeadBefore);
        Assert.Equal("d", row.HeadAfter);
    }

    [Fact]
    public void DeploymentSummary_MissingBeforeRevision_IsUnavailable()
    {
        var (service, workspace, repo) = BuildDeploymentStack(createRepo: true);
        var logs = Path.Combine(workspace, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "stable-restarts.jsonl"), JsonSerializer.Serialize(new
        {
            ts = "2026-07-11T08:42:11Z",
            @event = "restart",
            status = "ok",
            headBefore = new string('a', 40),
            headAfter = RevParse(repo, "HEAD"),
            durationSeconds = 47,
        }) + Environment.NewLine);

        var result = service.Build("Demo");

        Assert.NotNull(result);
        Assert.False(result!.Available);
        Assert.Contains("does not belong", result.Reason);
        Assert.Null(result.LastDeployment);
    }

    [Fact]
    public void DeploymentSummary_DerivesRunnableDescriptorWithoutHistory()
    {
        var (service, _, repo) = BuildDeploymentStack(createRepo: true);
        var descriptor = Path.Combine(repo, "docs", "deployments", "docs-site");
        Directory.CreateDirectory(descriptor);
        File.WriteAllText(Path.Combine(descriptor, "deployment.json"), """
            {
              "schemaVersion": 1,
              "id": "docs-site",
              "title": "Docs site",
              "kind": "template",
              "template": "caddy-site",
              "summary": "Deploy docs over SSH.",
              "command": "bash scripts/deploy-docs.sh --host {{host}} --branch {{branch}}",
              "targetHostId": "agent-orchestrator-web",
              "parameters": [
                { "name": "host", "type": "secret-ref", "required": true },
                { "name": "branch", "type": "branch", "required": true }
              ]
            }
            """);

        var result = service.Build("Demo");

        Assert.NotNull(result);
        Assert.False(result!.Available);
        var target = Assert.Single(result.Targets);
        Assert.Equal("docs-site", target.Id);
        Assert.True(target.Runnable);
        Assert.Equal("agent-orchestrator-web", target.TargetHostId);
        Assert.Equal(new[] { "secret-ref", "branch" }, target.Parameters.Select(parameter => parameter.Type));
    }

    private (ProjectDeploymentSummaryService Service, string Workspace, string Repo) BuildDeploymentStack(bool createRepo)
    {
        var workspace = Path.Combine(_root, Guid.NewGuid().ToString("N"), "workspace");
        var watchPath = Path.Combine(workspace, "projects", "demo");
        var repo = Path.Combine(_root, Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(repo);
        if (createRepo)
        {
            Git(repo, "init", "-b", "develop");
            Git(repo, "config", "user.email", "test@example.com");
            Git(repo, "config", "user.name", "Test User");
            Git(repo, "config", "commit.gpgsign", "false");
            Commit(repo, "initial.txt", "initial", "Initial");
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var service = new ProjectDeploymentSummaryService(
            config, scanner, git, settings, NullLogger<ProjectDeploymentSummaryService>.Instance);
        return (service, workspace, repo);
    }

    private static TaskInfo Task(string id, string state, DateTime enteredLaneAt) => new()
    {
        Id = id,
        TaskKey = "DEMO::" + id,
        Key = "DEM-" + id.Length,
        Title = id,
        State = state,
        EnteredLaneAt = enteredLaneAt,
    };

    private static TimelineEvent LaneChange(DateTime at, string target) => new()
    {
        Ts = at,
        Kind = TimelineEventKinds.LaneChanged,
        Actor = TimelineActors.System,
        Summary = "lane changed",
        Details = new Dictionary<string, string> { ["to"] = target },
    };

    private static void Commit(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git(repo, "add", "--", file);
        Git(repo, "commit", "-m", message);
    }

    private static string RevParse(string repo, string value) => Git(repo, "rev-parse", value).Trim();

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15_000), $"git {string.Join(' ', args)} timed out");
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        return output;
    }
}
