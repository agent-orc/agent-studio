using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3337: the completed-push backstop ran a full <c>git fetch --prune
/// origin</c> for every completed card, before it checked whether the card was
/// already integrated, and its "already on the remote" shortcut read
/// <c>origin/main</c> instead of the project's integration branch. On the
/// Agent Studio checkout (6,604 remote refs) every one of those fetches timed
/// out and left ref locks behind.
/// </summary>
public sealed class CompletedPushFetchBudgetTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private readonly string _remoteRoot;
    private readonly string _seedSha;

    public CompletedPushFetchBudgetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-push-fetch-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        _remoteRoot = Path.Combine(_tempDir, "origin.git");
        Directory.CreateDirectory(_watchPath);
        Directory.CreateDirectory(_repoRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        RunGit(_tempDir, "init", "--bare", "-q", "--initial-branch=main", _remoteRoot);
        RunGit(_repoRoot, "init", "-q", "-b", "main");
        RunGit(_repoRoot, "config", "user.email", "test@example.com");
        RunGit(_repoRoot, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "seed");
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", "seed");
        RunGit(_repoRoot, "remote", "add", "origin", _remoteRoot);
        RunGit(_repoRoot, "push", "-q", "-u", "origin", "main");
        RunGit(_repoRoot, "checkout", "-q", "-b", "develop");
        RunGit(_repoRoot, "push", "-q", "-u", "origin", "develop");
        _seedSha = RunGitCapture(_repoRoot, "rev-parse", "HEAD");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort; a locked file must not fail the test.
            Console.Error.WriteLine($"cleanup of {_tempDir} failed: {ex.Message}");
        }
    }

    [Theory]
    [InlineData(IntegrationStatuses.Integrated, true)]
    [InlineData(IntegrationStatuses.MergedLocally, false)]
    [InlineData(IntegrationStatuses.Partial, false)]
    [InlineData(IntegrationStatuses.Pending, false)]
    [InlineData(IntegrationStatuses.NoBranch, false)]
    [InlineData(null, false)]
    public void IsIntegratedByLocalEvidence_OnlyTrustsAPublishedIntegrationVerdict(string? status, bool expected)
        => Assert.Equal(expected, CompletedPushSelectionPolicy.IsIntegratedByLocalEvidence(status));

    [Fact]
    public async Task Backstop_FetchesTheIntegrationBranchOncePerRepositoryNotOncePerCard()
    {
        for (var index = 0; index < 3; index++)
            WriteJob(TaskStates.Completed, $"card-{index}", CommitOnDevelopAndPublish($"card {index}"));
        // Promoted already, so no card needs a push (and no push-side fetch).
        RunGit(_repoRoot, "push", "-q", "origin", "develop:main");
        var deps = BuildDeps();

        var backstop = new CompletedPushBackstopHostedService(
            deps.Scanner, deps.Settings, deps.Transitions, deps.Config,
            NullLogger<CompletedPushBackstopHostedService>.Instance);
        await backstop.RunOnceAsync();

        Assert.Equal(3, deps.Scanner.ScanAllAutomationJobs().Count(job => job.State == TaskStates.Completed));
        var fetch = Assert.Single(deps.Fetches);
        Assert.Contains("+refs/heads/develop:refs/remotes/origin/develop", fetch);
        Assert.DoesNotContain("--prune", fetch);
    }

    [Fact]
    public async Task IntegratedCard_IsSkippedWithoutAnyGitNetworkCall()
    {
        var sha = CommitOnDevelopAndPublish("delivered");
        WriteJob(TaskStates.Completed, "integrated", sha);
        var deps = BuildDeps(status: _ => new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Integrated,
            IntegrationBranch = "develop",
        });
        // Any network call would now fail; none may be made.
        RunGit(_repoRoot, "remote", "set-url", "origin", Path.Combine(_tempDir, "unreachable.git"));
        var job = deps.Scanner.FindJob("integrated", _watchPath)!;

        var result = await deps.Transitions.PushCompletedJobCommitsDetailedAsync(job, AutoPushStrategies.OnCompleted);

        Assert.True(result.CardSkippedIntegrated);
        Assert.Equal(0, result.Pushed);
        Assert.Empty(deps.Fetches);
        Assert.Equal(_seedSha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task RemoteShortcut_ReadsTheIntegrationBranchNotOriginMain()
    {
        // The delivery reached the remote develop from elsewhere: the local
        // origin/develop mirror does not know it yet, and origin/main never will.
        var sha = CommitOnDevelopAndPublish("merged on develop", updateMirror: false);
        WriteJob(TaskStates.Completed, "merged", sha);
        var deps = BuildDeps(status: _ => new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.MergedLocally,
            IntegrationBranch = "develop",
        });
        var job = deps.Scanner.FindJob("merged", _watchPath)!;

        var result = await deps.Transitions.PushCompletedJobCommitsDetailedAsync(job, AutoPushStrategies.OnCompleted);

        Assert.True(result.CardSkippedIntegrated);
        Assert.Equal(0, result.Pushed);
        Assert.Single(deps.Fetches);
        // The old origin/main shortcut missed this card and pushed it to main.
        Assert.Equal(_seedSha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    private string CommitOnDevelopAndPublish(string content, bool updateMirror = true)
    {
        File.WriteAllText(Path.Combine(_repoRoot, "work.txt"), content);
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", $"feat: {content}");
        // Pushing to the URL instead of the remote name leaves origin/develop untouched.
        RunGit(_repoRoot, "push", "-q", updateMirror ? "origin" : _remoteRoot, "develop");
        return RunGitCapture(_repoRoot, "rev-parse", "HEAD");
    }

    private Deps BuildDeps(Func<TaskInfo, TaskIntegrationStatus?>? status = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _watchPath,
                ["DeliveryChain:Guarded"] = "false",
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetAutoPushStrategy(ProjectName, AutoPushStrategies.OnCompleted);
        settings.SetIntegrationBranch(ProjectName, "develop");
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var fetches = new List<string>();
        var inner = git.IntegrationFetchForTesting;
        git.IntegrationFetchForTesting = (start, timeout, ct) =>
        {
            lock (fetches) fetches.Add(string.Join(' ', start.ArgumentList));
            return inner(start, timeout, ct);
        };
        TaskIntegrationStatusService? integrationStatus = null;
        if (status is not null)
        {
            integrationStatus = new TaskIntegrationStatusService(
                git,
                settings,
                new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance),
                NullLogger<TaskIntegrationStatusService>.Instance,
                new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance))
            {
                TestStatusOverride = status,
            };
        }
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance,
            integrationStatus: integrationStatus,
            timeProvider: _clock,
            configuration: config);
        return new Deps(config, scanner, settings, transitions, fetches);
    }

    private void WriteJob(string state, string slug, string sha)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $$"""
            {
              "id": "{{slug}}",
              "title": "{{slug}}",
              "state": "{{state}}",
              "order": 1,
              "agent": "copilot",
              "commit": {
                "sha": "{{sha}}",
                "shortSha": "{{sha[..7]}}",
                "message": "feat: {{slug}}",
                "filesChanged": 1,
                "files": ["work.txt"],
                "at": "{{_clock.GetUtcNow().UtcDateTime:o}}"
              }
            }
            """);
    }

    private static void RunGit(string cwd, params string[] args) => RunGitCapture(cwd, args);

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // The fixture's origin is a bare repository; allow it per invocation.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout.Result} {stderr}");
        return stdout.Result.Trim();
    }

    private sealed record Deps(
        IConfiguration Config,
        TaskScannerService Scanner,
        ProjectSettingsService Settings,
        TaskTransitionService Transitions,
        List<string> Fetches);
}
