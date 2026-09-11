using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoPushStrategyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;
    private readonly string _remoteRoot;
    private readonly string _seedSha;
    private const string ProjectName = "demo";

    public AutoPushStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-auto-push-" + Guid.NewGuid().ToString("N"));
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
        _seedSha = RunGitCapture(_repoRoot, "rev-parse", "HEAD");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // PERF regression guard: the move-to-6-completed request used to await the
    // git fetch + git push inline, so a "move to complete" blocked for 2-3 s on
    // the network round-trip. With a CompletedPushQueue wired, MoveAsync only
    // enqueues a snapshot and returns; the push runs on CompletedPushWorker.
    // A 3 s pre-push hook simulates a slow remote: on the broken (synchronous)
    // code this test measured ~3700 ms; with the queue it returns in tens of ms.
    // MachineBound 19.07.: Wallclock-Latenzbudget (<1000ms) flakt unter Parallellast im Karten-Gate.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task MoveToCompleted_OffloadsSlowPushFromRequestPath()
    {
        InstallSlowPushHook(3);
        var sha = CommitLocalChange("slow push change");
        WriteJob(TaskStates.HumanReview, "slow-task", sha);
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await deps.Transitions.MoveAsync(
            "slow-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);
        sw.Stop();

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"move-to-completed took {sw.ElapsedMilliseconds}ms; the git push must be off the request path");
        // The push was queued, not performed inline.
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.Equal("slow-task", queued!.Job.Id);
    }

    // Companion to the latency guard: prove the queued push actually lands when
    // the CompletedPushWorker drains the queue, so offloading did not silently
    // drop the auto-push.
    [Fact]
    public async Task CompletedPushWorker_PushesQueuedCommitToMain()
    {
        var sha = CommitLocalChange("worker-pushed change");
        WriteJob(TaskStates.HumanReview, "worker-task", sha);
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);

        var outcome = await deps.Transitions.MoveAsync(
            "worker-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);
        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.NotEqual(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));

        var worker = new CompletedPushWorker(queue, deps.Transitions, NullLogger<CompletedPushWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        var pushed = await WaitUntilAsync(
            () => RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main") == sha,
            TimeSpan.FromSeconds(15));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(pushed, "worker did not push the queued completed commit within the timeout");
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task AlwaysImmediate_QueuesAutoCommitPushOffTransitionPath()
    {
        InstallSlowPushHook(3);
        WriteJobWithoutCommit(TaskStates.Progress, "immediate-task");
        var sessionLogs = Path.Combine(_watchPath, TaskStates.Progress, "immediate-task", "logs");
        Directory.CreateDirectory(sessionLogs);
        File.WriteAllText(Path.Combine(sessionLogs, "session-events.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new SessionEvent
            {
                Ts = DateTime.UtcNow.AddSeconds(-1), Kind = "start", Cli = "codex"
            }) + Environment.NewLine);
        File.WriteAllText(Path.Combine(_repoRoot, "immediate.txt"), "push me\n");
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);
        var remoteBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main");

        var outcome = await deps.Transitions.MoveAsync(
            "immediate-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.False(queued!.RequireCompletedState);
        var localHead = RunGitCapture(_repoRoot, "rev-parse", "HEAD");
        Assert.NotEqual(remoteBefore, localHead);
        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));

        var worker = new CompletedPushWorker(queue, deps.Transitions, NullLogger<CompletedPushWorker>.Instance);
        await worker.ProcessAsync(queued, CancellationToken.None);

        Assert.Equal(localHead, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task AlwaysImmediate_WithDevelopLine_DoesNotAdvanceMainWithRawCommit()
    {
        var remoteBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main");
        RunGit(_repoRoot, "checkout", "-q", "-b", "develop");
        RunGit(_repoRoot, "push", "-q", "-u", "origin", "develop");
        RunGit(_repoRoot, "checkout", "-q", "main");
        WriteJobWithoutCommit(TaskStates.Progress, "lineage-task");
        var sessionLogs = Path.Combine(_watchPath, TaskStates.Progress, "lineage-task", "logs");
        Directory.CreateDirectory(sessionLogs);
        File.WriteAllText(Path.Combine(sessionLogs, "session-events.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(new SessionEvent
            {
                Ts = DateTime.UtcNow.AddSeconds(-1), Kind = "start", Cli = "codex"
            }) + Environment.NewLine);
        File.WriteAllText(Path.Combine(_repoRoot, "lineage.txt"), "must integrate through develop\n");
        var queue = new CompletedPushQueue();
        var deps = BuildDeps(queue);

        var outcome = await deps.Transitions.MoveAsync(
            "lineage-task", TaskStates.AutoReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(queue.Reader.TryRead(out var queued));
        var rawCommit = RunGitCapture(_repoRoot, "rev-parse", "HEAD");
        Assert.NotEqual(remoteBefore, rawCommit);

        var worker = new CompletedPushWorker(queue, deps.Transitions, NullLogger<CompletedPushWorker>.Instance);
        await worker.ProcessAsync(queued!, CancellationToken.None);

        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/develop"));
    }

    private void InstallSlowPushHook(int seconds)
    {
        var hooksDir = Path.Combine(_repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hook = Path.Combine(hooksDir, "pre-push");
        File.WriteAllText(hook, $"#!/bin/sh\nsleep {seconds}\nexit 0\n");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task MoveToCompleted_PushesStampedCommitToMain()
    {
        var sha = CommitLocalChange("reviewed change");
        WriteJob(TaskStates.HumanReview, "reviewed-task", sha);
        var deps = BuildDeps();

        var outcome = await deps.Transitions.MoveAsync(
            "reviewed-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task MoveToCompleted_WhenStrategyNever_DoesNotPush()
    {
        var remoteBefore = RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main");
        var sha = CommitLocalChange("manual push later");
        WriteJob(TaskStates.HumanReview, "manual-task", sha);
        var deps = BuildDeps();
        deps.Settings.SetAutoPushStrategy(ProjectName, AutoPushStrategies.Never);

        var outcome = await deps.Transitions.MoveAsync(
            "manual-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(remoteBefore, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task Backstop_PushesCompletedCommitMissedByTransition()
    {
        var sha = CommitLocalChange("missed trigger");
        WriteJob(TaskStates.Completed, "completed-task", sha);
        var deps = BuildDeps();
        var backstop = new CompletedPushBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Transitions,
            deps.Config,
            NullLogger<CompletedPushBackstopHostedService>.Instance);

        var pushed = await backstop.RunOnceAsync();

        Assert.Equal(1, pushed);
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    // AGT-2761: QS-85 attributed 42 commits across 73 fences, but only the two
    // commits of the final delivery generation were ancestors of origin/main.
    // The backstop used to push every attributed commit unconditionally, so
    // GitHub rejected the other 40 as non-fast-forward every 15-minute cycle
    // forever. Selection must push only what is reachable from the card's
    // integrated result (here, the reviewed-result SHA) and permanently mark
    // everything else superseded.
    [Fact]
    public async Task PushJobCommitsDetailedAsync_SkipsSupersededCommitsNotReachableFromReviewedResult()
    {
        var supersededSha = CommitDivergentFromSeed("fence attempt 12 (superseded)");
        var finalSha = CommitDivergentFromSeed("fence attempt 13 (final)");
        WriteJobWithCommits(TaskStates.Completed, "qs85", [
            (supersededSha, "attempt 12", DateTime.UtcNow.AddMinutes(-2)),
            (finalSha, "attempt 13", DateTime.UtcNow.AddMinutes(-1)),
        ]);
        ReviewSubjectStore.Write(
            Path.Combine(_watchPath, TaskStates.Completed, "qs85"),
            new ReviewSubjectRecord
            {
                TaskKey = "qs85",
                RunAttemptId = "attempt-13",
                Project = ProjectName,
                Repository = _repoRoot,
                ResultSha = finalSha,
                AttemptChainId = "chain-1",
            });
        var deps = BuildDeps();
        var job = deps.Scanner.FindJob("qs85", _watchPath)!;

        var result = await deps.Transitions.PushCompletedJobCommitsDetailedAsync(job, AutoPushStrategies.AlwaysImmediate);

        Assert.Equal(1, result.Pushed);
        Assert.Equal(1, result.SkippedSuperseded);
        Assert.Equal(finalSha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));

        var persisted = deps.Scanner.FindJob("qs85", _watchPath)!;
        var superseded = persisted.Commits.Single(c => c.Sha == supersededSha);
        var pushedCommit = persisted.Commits.Single(c => c.Sha == finalSha);
        Assert.Equal(CommitPushStatuses.Superseded, superseded.PushStatus);
        Assert.Null(pushedCommit.PushStatus);

        // A second cycle must not re-attempt the permanently superseded commit
        // or re-push the already-remote final commit.
        var second = await deps.Transitions.PushCompletedJobCommitsDetailedAsync(persisted, AutoPushStrategies.AlwaysImmediate);
        Assert.Equal(0, second.Pushed);
        Assert.Equal(1, second.SkippedSuperseded);
    }

    [Fact]
    public async Task PushJobCommitsDetailedAsync_SkipsCardEntirelyWhenIntegratedResultAlreadyOnRemoteMain()
    {
        var sha = CommitLocalChange("already delivered and promoted");
        RunGit(_repoRoot, "push", "-q", "origin", "main");
        WriteJob(TaskStates.Completed, "already-integrated", sha);
        var deps = BuildDeps(withIntegrationStatus: true);
        var job = deps.Scanner.FindJob("already-integrated", _watchPath)!;

        var result = await deps.Transitions.PushJobCommitsDetailedAsync(
            job, AutoPushStrategies.AlwaysImmediate, requireCompletedState: true);

        Assert.True(result.CardSkippedIntegrated);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.SkippedSuperseded);
        Assert.Equal(0, result.Rejected);
    }

    [Fact]
    public async Task PushJobCommitsDetailedAsync_BacksOffThenPermanentlyRejectsNonFastForwardCommit()
    {
        var localSha = CommitLocalChange("local reviewed change");
        CommitFromSecondClone("remote operator change"); // diverges the remote so every push attempt is non-fast-forward
        WriteJob(TaskStates.Completed, "diverged-task", localSha);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var store = new AgentStudio.Bus.AgentMessageBusStore();
        var bus = new AgentStudio.Bus.AgentMessageBusBridge(
            store,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _watchPath }).Build(),
            NullLogger<AgentStudio.Bus.AgentMessageBusBridge>.Instance);
        var deps = BuildDeps(bus: bus, timeProvider: clock);

        // Rejections 1-3 back off with increasing delays and stay non-terminal.
        var expectedDelays = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6) };
        for (var i = 0; i < expectedDelays.Length; i++)
        {
            var job = deps.Scanner.FindJob("diverged-task", _watchPath)!;
            var before = clock.GetUtcNow();
            var result = await deps.Transitions.PushCompletedJobCommitsDetailedAsync(job, AutoPushStrategies.AlwaysImmediate);

            Assert.Equal(0, result.Pushed);
            Assert.Equal(0, result.Rejected);
            var commit = deps.Scanner.FindJob("diverged-task", _watchPath)!.Commit!;
            Assert.Equal(i + 1, commit.PushAttempts);
            Assert.Null(commit.PushStatus);
            Assert.Equal(before.UtcDateTime + expectedDelays[i], commit.PushNextRetryAtUtc);

            clock.Advance(expectedDelays[i] + TimeSpan.FromMinutes(1));
        }

        // The 4th rejection is terminal: permanently marked push-rejected and
        // never attempted again, with exactly one operator-feed event for the
        // whole sequence (not one per rejection).
        var terminalJob = deps.Scanner.FindJob("diverged-task", _watchPath)!;
        var terminalResult = await deps.Transitions.PushCompletedJobCommitsDetailedAsync(terminalJob, AutoPushStrategies.AlwaysImmediate);
        Assert.Equal(0, terminalResult.Pushed);
        Assert.Equal(1, terminalResult.Rejected);
        var terminalCommit = deps.Scanner.FindJob("diverged-task", _watchPath)!.Commit!;
        Assert.Equal(CommitPushStatuses.Rejected, terminalCommit.PushStatus);
        Assert.Equal(4, terminalCommit.PushAttempts);

        clock.Advance(TimeSpan.FromDays(2));
        var afterTerminalJob = deps.Scanner.FindJob("diverged-task", _watchPath)!;
        var afterTerminal = await deps.Transitions.PushCompletedJobCommitsDetailedAsync(afterTerminalJob, AutoPushStrategies.AlwaysImmediate);
        Assert.Equal(1, afterTerminal.Rejected);
        Assert.Equal(4, deps.Scanner.FindJob("diverged-task", _watchPath)!.Commit!.PushAttempts);

        var pushFailureEvents = store.Recent(_watchPath, project: ProjectName, limit: 50)
            .Where(m => m.Topic == "managed-repo-push-failed")
            .ToList();
        Assert.Single(pushFailureEvents);
    }

    [Fact]
    public async Task Backstop_CycleLogsOneSummaryLine()
    {
        var sha = CommitLocalChange("missed trigger");
        WriteJob(TaskStates.Completed, "completed-task", sha);
        var deps = BuildDeps();
        var logs = new List<string>();
        var backstop = new CompletedPushBackstopHostedService(
            deps.Scanner,
            deps.Settings,
            deps.Transitions,
            deps.Config,
            new CollectingLogger<CompletedPushBackstopHostedService>(logs));

        var pushed = await backstop.RunOnceAsync();

        Assert.Equal(1, pushed);
        Assert.Equal(sha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
        var summary = Assert.Single(logs, l => l.Contains("Completed auto-push backstop cycle", StringComparison.Ordinal));
        Assert.Contains("1 scanned", summary, StringComparison.Ordinal);
        Assert.Contains("1 pushed", summary, StringComparison.Ordinal);
    }

    private sealed class CollectingLogger<T>(List<string> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add(formatter(state, exception));
    }

    [Fact]
    public async Task MoveToCompleted_DoesNotForcePushDivergedRemote()
    {
        var localSha = CommitLocalChange("local reviewed change");
        var remoteSha = CommitFromSecondClone("remote operator change");
        WriteJob(TaskStates.HumanReview, "diverged-task", localSha);
        var deps = BuildDeps();

        var outcome = await deps.Transitions.MoveAsync(
            "diverged-task",
            TaskStates.Completed,
            _watchPath,
            suppressIntegrationTrigger: true);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(remoteSha, RunGitCapture(_remoteRoot, "rev-parse", "refs/heads/main"));
    }

    private Deps BuildDeps(
        CompletedPushQueue? pushQueue = null,
        bool withIntegrationStatus = false,
        AgentStudio.Bus.AgentMessageBusBridge? bus = null,
        TimeProvider? timeProvider = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var integrationStatus = withIntegrationStatus
            ? new TaskIntegrationStatusService(
                git,
                settings,
                new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
                NullLogger<TaskIntegrationStatusService>.Instance,
                new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance))
            : null;
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance,
            sessions: sessions,
            pushQueue: pushQueue,
            bus: bus,
            integrationStatus: integrationStatus,
            timeProvider: timeProvider);
        return new Deps(config, scanner, settings, transitions);
    }

    private string CommitLocalChange(string content)
    {
        File.WriteAllText(Path.Combine(_repoRoot, "work.txt"), content);
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", $"feat: {content}");
        return RunGitCapture(_repoRoot, "rev-parse", "HEAD");
    }

    private string CommitFromSecondClone(string content)
    {
        var clone = Path.Combine(_tempDir, "second-clone");
        RunGit(_tempDir, "clone", "-q", _remoteRoot, clone);
        RunGit(clone, "config", "user.email", "remote@example.com");
        RunGit(clone, "config", "user.name", "remote");
        File.WriteAllText(Path.Combine(clone, "remote.txt"), content);
        RunGit(clone, "add", "-A");
        RunGit(clone, "commit", "-q", "-m", $"feat: {content}");
        RunGit(clone, "push", "-q", "origin", "main");
        return RunGitCapture(clone, "rev-parse", "HEAD");
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
                "at": "{{DateTime.UtcNow:o}}"
              }
            }
            """);
    }

    /// <summary>Commits off the seed instead of the current tip, so the result is a sibling of - not a descendant of - whatever was committed before it.</summary>
    private string CommitDivergentFromSeed(string content)
    {
        RunGit(_repoRoot, "reset", "-q", "--hard", _seedSha);
        return CommitLocalChange(content);
    }

    private void WriteJobWithCommits(string state, string slug, IReadOnlyList<(string Sha, string Message, DateTime At)> commits)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var commitsJson = string.Join(",\n", commits.Select(c => $$"""
            {
              "sha": "{{c.Sha}}",
              "shortSha": "{{c.Sha[..7]}}",
              "message": "{{c.Message}}",
              "filesChanged": 1,
              "files": ["work.txt"],
              "at": "{{c.At:o}}"
            }
            """));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $$"""
            {
              "id": "{{slug}}",
              "title": "{{slug}}",
              "state": "{{state}}",
              "order": 1,
              "agent": "copilot",
              "commits": [
                {{commitsJson}}
              ]
            }
            """);
    }

    private void WriteJobWithoutCommit(string state, string slug)
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
              "agent": "copilot"
            }
            """);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var (stdout, stderr, code) = RunGitRaw(cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr}");
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var (stdout, stderr, code) = RunGitRaw(cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr}");
        return stdout.Trim();
    }

    private static (string Stdout, string Stderr, int Code) RunGitRaw(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // The fixture's "origin" is a bare repo; on hosts where git has
        // safe.bareRepository=explicit, plain `git -C <bare> ...` is refused.
        // Relax it per-invocation (subprocess-scoped, no config mutation).
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (stdout, stderr, p.ExitCode);
    }

    private sealed record Deps(
        IConfiguration Config,
        TaskScannerService Scanner,
        ProjectSettingsService Settings,
        TaskTransitionService Transitions);
}
