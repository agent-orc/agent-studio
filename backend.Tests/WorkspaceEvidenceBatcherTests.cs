using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Transition-Committer contract: enqueued lane transitions are
/// committed off the request path, debounced per workspace repo (idle window +
/// hard max-delay cap) on virtual time, scoped to the touched
/// <c>projects/&lt;name&gt;</c> data paths (repo-root runtime noise and scratch
/// globs excluded), with a boot catch-up, an index.lock retry, and fail-open
/// behaviour so a git problem never breaks the move that produced the evidence.
/// Real git on a temp repo (mirrors <see cref="WorkspaceArtifactCommitServiceTests"/>);
/// virtual time via <c>FakeTimeProvider</c> so there is no MachineBound sleep.
/// </summary>
public sealed class WorkspaceEvidenceBatcherTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchPath;
    private const string Project = "demo";

    public WorkspaceEvidenceBatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        RunGit(_root, "init", "-q", "-b", "main");
        RunGit(_root, "config", "user.name", "test");
        RunGit(_root, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_root, "README.md"), "seed\n");
        RunGit(_root, "add", "README.md");
        RunGit(_root, "commit", "-q", "-m", "seed");

        _watchPath = Path.Combine(_root, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ---- Debounce ----------------------------------------------------------

    [Fact]
    public void FlushDue_HoldsUntilIdleDebounceElapses_ThenCommitsOnce()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 15, maxDelay: 60);

        WriteEvidence("ASS-1", "run.md", "one\n");
        batcher.Ingest(Request("ASS-1", TaskStates.Progress, TaskStates.AutoReview));

        // Idle window not elapsed yet: nothing commits.
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Empty(batcher.FlushDue());
        Assert.Equal(1, CountCommits());

        // Cross the debounce threshold: exactly one evidence commit lands.
        time.Advance(TimeSpan.FromSeconds(2));
        var flushed = batcher.FlushDue();
        var result = Assert.Single(flushed);
        Assert.True(result.Result.DidCommit, result.Result.Error);
        Assert.Equal(2, CountCommits());

        // Drained: a second flush with nothing pending is a no-op.
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(batcher.FlushDue());
        Assert.Equal(2, CountCommits());

        Assert.Contains("evidence: 1 transition", LastCommitMessage());
        Assert.Contains(Project, LastCommitMessage());
        Assert.Contains("ASS-1", LastCommitMessage());
    }

    [Fact]
    public void FlushDue_ContinuousActivity_StillFlushesAtMaxDelayCap()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 15, maxDelay: 60);

        // Ingest every 14s (< 15s debounce) so the idle window never elapses,
        // yet the batch must still flush once the 60s hard cap is crossed.
        WriteEvidence("ASS-2", "run.md", "v0\n");
        batcher.Ingest(Request("ASS-2", TaskStates.Progress, TaskStates.AutoReview));
        for (var i = 1; i <= 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(14));
            WriteEvidence("ASS-2", "run.md", $"v{i}\n");
            batcher.Ingest(Request("ASS-2", TaskStates.Progress, TaskStates.AutoReview));
            Assert.Empty(batcher.FlushDue()); // idle 0, total < 60
        }

        // total now 56s; one more 14s tick crosses the cap even though we just
        // ingested (idle == 0).
        time.Advance(TimeSpan.FromSeconds(14));
        WriteEvidence("ASS-2", "run.md", "v5\n");
        batcher.Ingest(Request("ASS-2", TaskStates.Progress, TaskStates.AutoReview));

        var flushed = batcher.FlushDue();
        var result = Assert.Single(flushed);
        Assert.True(result.Result.DidCommit, result.Result.Error);
        Assert.Equal(6, result.TransitionCount);
        Assert.Equal(2, CountCommits());
    }

    // ---- Exclude / scope ---------------------------------------------------

    [Fact]
    public void FlushDue_StagesDataPaths_ExcludesScratchAndRepoRootNoise()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 5, maxDelay: 60);

        // Data evidence (should be committed).
        WriteEvidence("ASS-3", "code-review.md", "review\n");
        // Scratch inside the project (excluded by glob).
        WriteEvidence("ASS-3", "scratch.tmp", "junk\n");
        // Repo-root runtime noise (outside projects/<name>; excluded by scope).
        Directory.CreateDirectory(Path.Combine(_root, "identities"));
        File.WriteAllText(Path.Combine(_root, "identities", "agent.json"), "{\"n\":1}\n");

        batcher.Ingest(Request("ASS-3", TaskStates.Ready, TaskStates.Progress));
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(Assert.Single(batcher.FlushDue()).Result.DidCommit);

        var committed = CommittedFiles();
        Assert.Contains("projects/demo/3-progress/ASS-3/code-review.md", committed);
        Assert.DoesNotContain(committed, f => f.EndsWith("scratch.tmp", StringComparison.Ordinal));
        Assert.DoesNotContain(committed, f => f.Contains("identities/", StringComparison.Ordinal));

        // The repo-root noise is still there (untracked), just never staged by us.
        var status = RunGitCapture(_root, "status", "--porcelain=v1").Replace('\\', '/');
        Assert.Contains("identities/", status);
    }

    [Fact]
    public void FlushDue_ExcludesTrackedOrchestratorRuntime_NotJustUntracked()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 5, maxDelay: 60);

        // Per-project orchestrator runtime that is ALREADY TRACKED (committed),
        // mirroring the live workspace repo. This is the case the reset-based
        // exclusion silently failed to exclude: `git commit -- <pathspec>` uses
        // partial-commit semantics that take the working-tree content of tracked
        // paths regardless of what was unstaged.
        var orchDir = Path.Combine(_watchPath, ".orchestrator");
        Directory.CreateDirectory(Path.Combine(orchDir, "chat-attachments"));
        File.WriteAllText(Path.Combine(orchDir, "orchestrator.jsonl"), "orig\n");
        File.WriteAllText(Path.Combine(orchDir, "chat-attachments", "a.png"), "imgorig\n");
        RunGit(_root, "add", "-A");
        RunGit(_root, "commit", "-q", "-m", "seed orchestrator runtime");

        // Churn the tracked runtime AND write real evidence.
        File.WriteAllText(Path.Combine(orchDir, "orchestrator.jsonl"), "BIG-CHURN\n");
        File.WriteAllText(Path.Combine(orchDir, "chat-attachments", "a.png"), "imgnew\n");
        WriteEvidence("ASS-9", "code-review.md", "review\n");

        batcher.Ingest(Request("ASS-9", TaskStates.Ready, TaskStates.Progress));
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(Assert.Single(batcher.FlushDue()).Result.DidCommit);

        var committed = CommittedFiles();
        Assert.Contains("projects/demo/3-progress/ASS-9/code-review.md", committed);
        Assert.DoesNotContain(committed, f => f.Contains(".orchestrator/", StringComparison.Ordinal));

        // The tracked runtime churn was NOT swept into the commit: it stays
        // modified in the working tree.
        var status = RunGitCapture(_root, "status", "--porcelain=v1").Replace('\\', '/');
        Assert.Contains(".orchestrator/orchestrator.jsonl", status);
        Assert.Contains(".orchestrator/chat-attachments/a.png", status);
    }

    [Fact]
    public void FlushDue_ExcludesTrackedCliOutputAndRotationDuringLiveBatch()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 5, maxDelay: 60);
        var taskDir = Path.Combine(_watchPath, TaskStates.Progress, "ASS-10");
        var logsDir = Path.Combine(taskDir, "logs");
        Directory.CreateDirectory(logsDir);
        var active = Path.Combine(logsDir, "cli-output.log");
        var rotation = active + CliOutputLogFile.RotationSuffix;
        File.WriteAllText(active, "active-original\n");
        File.WriteAllText(rotation, "rotation-original\n");
        RunGit(_root, "add", "-A");
        RunGit(_root, "commit", "-q", "-m", "seed cli logs");

        File.WriteAllText(active, "active-current\n");
        File.WriteAllText(rotation, "rotation-current\n");
        File.WriteAllText(Path.Combine(taskDir, "code-review.md"), "review\n");
        batcher.Ingest(Request("ASS-10", TaskStates.Ready, TaskStates.Progress));
        time.Advance(TimeSpan.FromSeconds(6));

        Assert.True(Assert.Single(batcher.FlushDue()).Result.DidCommit);
        var committed = CommittedFiles();
        Assert.DoesNotContain("projects/demo/3-progress/ASS-10/logs/cli-output.log", committed);
        Assert.DoesNotContain(
            "projects/demo/3-progress/ASS-10/logs/cli-output.log.1",
            committed);
        Assert.Contains(
            "cli-output.log.1",
            RunGitCapture(_root, "status", "--porcelain=v1"),
            StringComparison.Ordinal);
    }

    // ---- Foreign-repo guard ------------------------------------------------

    [Fact]
    public void Ingest_WatchPathInForeignRepo_IsDropped_LocalRepoStillCommits()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceEvidence:Enabled"] = "true",
                ["WorkspaceEvidence:DebounceSeconds"] = "1",
                ["WorkspaceEvidence:MaxDelaySeconds"] = "60",
                ["WorkspaceEvidence:IndexLockRetryBackoffMs"] = "0",
                ["TaskRepository"] = _root,
            })
            .Build();
        var commit = new WorkspaceArtifactCommitService(
            config, NullLogger<WorkspaceArtifactCommitService>.Instance);
        var batcher = new WorkspaceEvidenceBatcher(
            commit, config, NullLogger.Instance, time, push: null);

        var foreign = Path.Combine(Path.GetTempPath(), "atp-evidence-foreign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreign);
        try
        {
            RunGit(foreign, "init", "-q", "-b", "main");
            RunGit(foreign, "config", "user.name", "t");
            RunGit(foreign, "config", "user.email", "t@example.com");
            var foreignWatch = Path.Combine(foreign, ".orchestrator", "jobs");
            Directory.CreateDirectory(foreignWatch);
            File.WriteAllText(Path.Combine(foreignWatch, "evidence.md"), "x\n");

            // Transition on a watch path that lives inside a foreign source repo.
            batcher.Ingest(new WorkspaceEvidenceRequest(foreignWatch, "foreign", "slug", "a", "b"));
            // And a legit transition on the task repo.
            WriteEvidence("ASS-8", "run.md", "one\n");
            batcher.Ingest(Request("ASS-8", TaskStates.Progress, TaskStates.AutoReview));

            time.Advance(TimeSpan.FromSeconds(30));
            var flushed = batcher.FlushDue();

            // Exactly one repo flushed: the task repo. The foreign repo was
            // dropped at ingest and never touched.
            var single = Assert.Single(flushed);
            Assert.Equal(_root, single.GitRoot, StringComparer.OrdinalIgnoreCase);
            Assert.True(single.Result.DidCommit, single.Result.Error);

            Assert.Equal("0", RunGitCapture(foreign, "rev-list", "--count", "--all").Trim());
            // The foreign repo was never touched: its evidence file is still
            // untracked (git collapses the untracked dir in porcelain output).
            var foreignStatus = RunGitCapture(foreign, "status", "--porcelain=v1").Replace('\\', '/');
            Assert.Contains(".orchestrator/", foreignStatus);
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(foreign, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(foreign, recursive: true);
            }
            catch { }
        }
    }

    // ---- Final flush on shutdown -------------------------------------------

    [Fact]
    public void FlushAll_CommitsPendingIgnoringDebounceWindow()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 15, maxDelay: 60);

        WriteEvidence("ASS-7", "run.md", "one\n");
        batcher.Ingest(Request("ASS-7", TaskStates.Progress, TaskStates.AutoReview));

        // Debounce not elapsed: the periodic path would defer.
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(batcher.FlushDue());
        Assert.Equal(1, CountCommits());

        // Shutdown flush commits it anyway.
        Assert.True(Assert.Single(batcher.FlushAll()).Result.DidCommit);
        Assert.Equal(2, CountCommits());

        // Drained: a second FlushAll with nothing pending is a no-op.
        Assert.Empty(batcher.FlushAll());
    }

    // ---- Catch-up ----------------------------------------------------------

    [Fact]
    public void CatchUp_CommitsPreexistingDriftWithCatchupMessage()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 15, maxDelay: 60);

        // Drift that accumulated "while the backend was down" — never enqueued.
        WriteEvidence("ASS-4", "pipeline-execution.json", "{\"attempt\":1}\n");

        var flushed = batcher.CatchUp(new[] { _watchPath });
        var result = Assert.Single(flushed);
        Assert.True(result.Result.DidCommit, result.Result.Error);
        Assert.Contains("evidence: catch-up nach neustart", LastCommitMessage());
        Assert.Contains(
            "projects/demo/3-progress/ASS-4/pipeline-execution.json",
            CommittedFiles());

        // Idempotent: a second catch-up with nothing dirty commits nothing.
        Assert.False(Assert.Single(batcher.CatchUp(new[] { _watchPath })).Result.DidCommit);
    }

    [Fact]
    public void FlushExcludesClassCWhileCommittingSmallEvidence()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 1, maxDelay: 60);
        Directory.CreateDirectory(Path.Combine(_watchPath, TaskStates.Progress, "ASS-HEAVY", "logs"));
        WriteEvidence("ASS-HEAVY", "logs/cli-output.log", "live heavy output\n");
        WriteEvidence("ASS-HEAVY", "status.md", "small evidence\n");
        batcher.Ingest(new WorkspaceEvidenceRequest(_watchPath, Project, "ASS-HEAVY", "a", "b"));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(Assert.Single(batcher.FlushDue()).Result.DidCommit);
        var files = CommittedFiles();
        Assert.Contains("projects/demo/3-progress/ASS-HEAVY/status.md", files);
        Assert.DoesNotContain("projects/demo/3-progress/ASS-HEAVY/logs/cli-output.log", files);
    }

    // ---- index.lock retry --------------------------------------------------

    [Fact]
    public void RunWithIndexLockRetry_RetriesOnLockContention_ThenSucceeds()
    {
        var calls = 0;
        WorkspaceArtifactCommitService.GitProcessResult Run()
        {
            calls++;
            return calls < 3
                ? new WorkspaceArtifactCommitService.GitProcessResult(
                    "", "fatal: Unable to create '/repo/.git/index.lock': File exists.", 128)
                : new WorkspaceArtifactCommitService.GitProcessResult("ok", "", 0);
        }

        var result = WorkspaceArtifactCommitService.RunWithIndexLockRetry(Run, attempts: 5, backoff: null);

        Assert.Equal(0, result.Code);
        Assert.Equal(3, calls); // two contended attempts, third succeeds
    }

    [Fact]
    public void RunWithIndexLockRetry_GivesUpAfterAttempts_ReturnsLastFailure()
    {
        var calls = 0;
        WorkspaceArtifactCommitService.GitProcessResult Run()
        {
            calls++;
            return new WorkspaceArtifactCommitService.GitProcessResult(
                "", "Another git process seems to be running in this repository", 128);
        }

        var result = WorkspaceArtifactCommitService.RunWithIndexLockRetry(Run, attempts: 3, backoff: null);

        Assert.Equal(128, result.Code);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(0, "whatever", false)]
    [InlineData(128, "fatal: Unable to create '.git/index.lock': File exists.", true)]
    [InlineData(1, "Another git process seems to be running", true)]
    [InlineData(1, "merge conflict in foo", false)]
    public void IsIndexLockContention_ClassifiesGitFailures(int code, string err, bool expected)
        => Assert.Equal(expected, WorkspaceArtifactCommitService.IsIndexLockContention(code, err));

    // ---- Fail-open ---------------------------------------------------------

    [Fact]
    public void TryCommitEvidence_BadGitRoot_ReturnsSkippedWithoutThrowing()
    {
        var commit = new WorkspaceArtifactCommitService(
            EmptyConfig(), NullLogger<WorkspaceArtifactCommitService>.Instance);

        var result = commit.TryCommitEvidence(
            Path.Combine(_root, "does-not-exist"),
            new[] { _watchPath },
            Array.Empty<string>(),
            "evidence: x\n");

        Assert.True(result.Success);      // fail-open: not an error for the board
        Assert.False(result.DidCommit);
        Assert.Equal("workspace-missing", result.Error);
    }

    [Fact]
    public void Ingest_WatchPathOutsideAnyRepo_IsDroppedSilently()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var batcher = BuildBatcher(time, debounce: 1, maxDelay: 60);

        var nonRepo = Path.Combine(Path.GetTempPath(), "atp-evidence-norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nonRepo);
        try
        {
            batcher.Ingest(new WorkspaceEvidenceRequest(nonRepo, "x", "y", "a", "b"));
            time.Advance(TimeSpan.FromSeconds(30));
            Assert.Empty(batcher.FlushDue());
        }
        finally
        {
            try { Directory.Delete(nonRepo, recursive: true); } catch { }
        }
    }

    // ---- TaskStateMachine hook: transition never blocked -------------------

    [Fact]
    public void MoveJob_EnqueuesEvidence_AndStillSucceeds()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        var dir = Path.Combine(_watchPath, TaskStates.AutoReview, "hook-task");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            "{\"id\":\"hook-task\",\"title\":\"t\",\"state\":\"4-auto-review\",\"order\":1,\"agent\":\"claude\"}");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var queue = new WorkspaceEvidenceQueue();
        var machine = new TaskStateMachine(
            scanner, NullLogger<TaskStateMachine>.Instance, evidenceQueue: queue);

        var outcome = machine.MoveJob("hook-task", TaskStates.HumanReview, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(queue.Reader.TryRead(out var req));
        Assert.Equal(_watchPath, req!.WatchPath);
        Assert.Equal("hook-task", req.Slug);
        Assert.Equal(TaskStates.AutoReview, req.FromState);
        Assert.Equal(TaskStates.HumanReview, req.ToState);
    }

    // ---- helpers -----------------------------------------------------------

    private WorkspaceEvidenceBatcher BuildBatcher(FakeTimeProvider time, int debounce, int maxDelay)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceEvidence:Enabled"] = "true",
                ["WorkspaceEvidence:DebounceSeconds"] = debounce.ToString(),
                ["WorkspaceEvidence:MaxDelaySeconds"] = maxDelay.ToString(),
                ["WorkspaceEvidence:IndexLockRetryBackoffMs"] = "0",
            })
            .Build();
        var commit = new WorkspaceArtifactCommitService(
            config, NullLogger<WorkspaceArtifactCommitService>.Instance);
        return new WorkspaceEvidenceBatcher(
            commit, config, NullLogger.Instance, time, push: null);
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private WorkspaceEvidenceRequest Request(string slug, string from, string to)
        => new(_watchPath, Project, slug, from, to);

    private void WriteEvidence(string slug, string file, string content)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
    }

    private int CountCommits() =>
        RunGitCapture(_root, "rev-list", "--count", "HEAD").Trim() is var s && int.TryParse(s, out var n) ? n : -1;

    private string LastCommitMessage() => RunGitCapture(_root, "log", "-1", "--format=%B");

    private List<string> CommittedFiles() =>
        RunGitCapture(_root, "show", "--name-only", "--pretty=format:", "HEAD")
            .Replace('\\', '/')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static void RunGit(string cwd, params string[] args)
        => Assert.Equal(0, RunGitResult(cwd, args).Code);

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var r = RunGitResult(cwd, args);
        Assert.Equal(0, r.Code);
        return r.Out;
    }

    private static (string Out, string Err, int Code) RunGitResult(string cwd, params string[] args)
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
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (stdout, stderr, p.ExitCode);
    }
}
