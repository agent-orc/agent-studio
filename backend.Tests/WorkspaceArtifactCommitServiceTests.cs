using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkspaceArtifactCommitServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceArtifactCommitService _service;

    public WorkspaceArtifactCommitServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-workspace-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        RunGit(_root, "init", "-q", "-b", "main");
        RunGit(_root, "config", "user.name", "test");
        RunGit(_root, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_root, "README.md"), "seed\n");
        RunGit(_root, "add", "README.md");
        RunGit(_root, "commit", "-q", "-m", "seed");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        _service = new WorkspaceArtifactCommitService(
            config,
            NullLogger<WorkspaceArtifactCommitService>.Instance);
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

    [Fact]
    public void RunBoundaryCommit_StagesOnlyTheJobFolder()
    {
        var job = JobFolder("ASS-1");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        File.WriteAllText(Path.Combine(job, "code-review.md"), "review\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Warn"));
        File.WriteAllText(Path.Combine(job, "logs", "session-events.jsonl"), "{}\n");

        var foreign = JobFolder("ASS-2");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "status.md"), "foreign\n");
        RunGit(_root, "add", Relative(Path.Combine(foreign, "status.md")));

        var result = _service.TryCommitRunBoundary(
            _root,
            "ASS-1",
            beforeMoveFolderPath: null,
            afterMoveFolderPath: job,
            ReviewDecisionKind.Reissue);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.Equal(1, result.RunIndex);
        Assert.Equal("aspect-code-quality=warn", result.Steps);

        var committed = RunGitCapture(_root, "show", "--name-only", "--pretty=format:", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Assert.Contains(Relative(Path.Combine(job, "code-review.md")), committed);
        Assert.Contains(Relative(Path.Combine(job, "pipeline-execution.json")), committed);
        Assert.DoesNotContain(Relative(Path.Combine(foreign, "status.md")), committed);

        var status = RunGitCapture(_root, "status", "--porcelain=v1");
        Assert.Contains("ASS-2/status.md", status.Replace('\\', '/'));

        var message = RunGitCapture(_root, "log", "-1", "--format=%B");
        Assert.Contains("Run-Index: 1", message);
        Assert.Contains("Verdict: reissue", message);
        Assert.Contains("Steps: aspect-code-quality=warn", message);
    }

    [Fact]
    public void RunBoundaryCommit_TwoRunsProduceTwoFollowHistoryEntries()
    {
        var job = JobFolder("ASS-7");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        var codeReview = Path.Combine(job, "code-review.md");
        var sessions = Path.Combine(job, "logs", "session-events.jsonl");

        File.WriteAllText(codeReview, "run 1 review\n");
        File.WriteAllText(sessions, "{}\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Warn"));
        var first = _service.TryCommitRunBoundary(_root, "ASS-7", null, job, ReviewDecisionKind.Reissue);
        Assert.True(first.Success, first.Error);
        Assert.True(first.DidCommit);

        File.WriteAllText(codeReview, "run 2 review\n");
        File.AppendAllText(sessions, "{}\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Pass"));
        var second = _service.TryCommitRunBoundary(_root, "ASS-7", job, job, ReviewDecisionKind.AcceptAsDone);
        Assert.True(second.Success, second.Error);
        Assert.True(second.DidCommit);

        var history = RunGitCapture(_root, "log", "--follow", "--format=%B%x00", "--", Relative(codeReview))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.Contains("Run-Index:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, m => m.Contains("Run-Index: 1") && m.Contains("Verdict: reissue"));
        Assert.Contains(history, m => m.Contains("Run-Index: 2") && m.Contains("Verdict: accept"));
        Assert.Contains(history, m => m.Contains("Steps: aspect-code-quality=pass"));
    }

    [Fact]
    public void RunBoundaryCommit_UsesPipelineAttemptForRunIndex()
    {
        var job = JobFolder("ASS-8");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        File.WriteAllText(Path.Combine(job, "code-review.md"), "review\n");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), PipelineJson("aspect-code-quality", "Pass", attempt: 2));
        File.WriteAllText(Path.Combine(job, "logs", "session-events.jsonl"), "{}\n{}\n{}\n{}\n");

        var result = _service.TryCommitRunBoundary(
            _root,
            "ASS-8",
            beforeMoveFolderPath: null,
            afterMoveFolderPath: job,
            ReviewDecisionKind.AcceptAsDone);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.Equal(2, result.RunIndex);

        var message = RunGitCapture(_root, "log", "-1", "--format=%B");
        Assert.Contains("Run-Index: 2", message);
        Assert.Contains("Verdict: accept", message);
    }

    [Fact]
    public void RunBoundaryCommit_ParsesNumericPipelineStatusesForStepsTrailer()
    {
        var job = JobFolder("ASS-9");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), NumericPipelineJson());

        var result = _service.TryCommitRunBoundary(
            _root,
            "ASS-9",
            beforeMoveFolderPath: null,
            afterMoveFolderPath: job,
            ReviewDecisionKind.Reissue);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.Equal(
            "pre-loop-guard=passed,aspect-code-quality=warn,post-orchestrator-decision=skipped",
            result.Steps);

        var message = RunGitCapture(_root, "log", "-1", "--format=%B");
        Assert.Contains("Steps: pre-loop-guard=passed,aspect-code-quality=warn,post-orchestrator-decision=skipped", message);
    }

    [Fact]
    public void RunBoundaryCommit_EnqueuesEveryCommitForImmediatePush()
    {
        var queue = new WorkspaceArtifactPushQueue();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var service = new WorkspaceArtifactCommitService(
            config, NullLogger<WorkspaceArtifactCommitService>.Instance, queue);
        var job = JobFolder("ASS-PUSH");
        Directory.CreateDirectory(job);

        File.WriteAllText(Path.Combine(job, "status.md"), "one\n");
        Assert.True(service.TryCommitArtifactUpload(_root, "ASS-PUSH", job, ["status.md"]).DidCommit);
        File.AppendAllText(Path.Combine(job, "status.md"), "two\n");
        Assert.True(service.TryCommitArtifactUpload(_root, "ASS-PUSH", job, ["status.md"]).DidCommit);

        Assert.True(queue.Reader.TryRead(out var first));
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal("ASS-PUSH", first!.JobId);
        Assert.Equal("ASS-PUSH", second!.JobId);
    }

    [Fact]
    public async Task WorkspacePushWorker_FailureIsToleratedAfterBoundedRetries()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0"
            })
            .Build();
        var store = new AgentStudio.Bus.AgentMessageBusStore();
        var bus = new AgentStudio.Bus.AgentMessageBusBridge(
            store, config, NullLogger<AgentStudio.Bus.AgentMessageBusBridge>.Instance);
        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config,
            bus);

        var pushed = await worker.ProcessAsync(
            new WorkspaceArtifactPushRequest(_root, "ASS-OFFLINE"), default);

        Assert.False(pushed);
        var pushFailure = Assert.Single(store.Recent(_root, project: null, limit: 10));
        Assert.Equal("managed-repo-push-failed", pushFailure.Topic);
        Assert.Equal("error", pushFailure.Kind);
        Assert.Equal("ASS-OFFLINE", pushFailure.JobId);
    }

    [Fact]
    public async Task WorkspacePushWorker_PushesTheRequestedShaToTheRequestedBranch()
    {
        var firstSha = RunGitCapture(_root, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_root, "later.txt"), "later\n");
        RunGit(_root, "add", "later.txt");
        RunGit(_root, "commit", "-q", "-m", "later");
        var remote = Path.Combine(_root, "remote.git");
        RunGit(_root, "init", "-q", "--bare", remote);
        RunGit(_root, "remote", "add", "origin", remote);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
            })
            .Build();
        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config);

        var pushed = await worker.ProcessAsync(
            new WorkspaceArtifactPushRequest(
                _root,
                "service-mutation",
                TargetBranch: "develop",
                Sha: firstSha,
                Project: "Project"),
            default);

        Assert.True(pushed);
        Assert.Equal(
            firstSha,
            RunGitCapture(_root, $"--git-dir={remote}", "rev-parse", "refs/heads/develop").Trim());
    }

    [Fact]
    public async Task WorkspacePushWorker_UsesCatchUpBudgetAfterTimeout()
    {
        var pushTimeouts = new List<TimeSpan>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
                ["WorkspaceArtifacts:PushTimeoutSeconds"] = "3",
                ["WorkspaceArtifacts:CatchUpPushTimeoutSeconds"] = "120",
            })
            .Build();
        Task<WorkspaceArtifactPushWorker.PushGitResult> Run(
            string _, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken __)
        {
            if (args[0] != "push")
                return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(0, "1", "", GitProcessFailureKind.None));
            pushTimeouts.Add(timeout);
            return Task.FromResult(pushTimeouts.Count == 1
                ? new WorkspaceArtifactPushWorker.PushGitResult(-1, "", "timed out", GitProcessFailureKind.TimedOut)
                : new WorkspaceArtifactPushWorker.PushGitResult(0, "", "", GitProcessFailureKind.None));
        }

        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config,
            bus: null,
            advisories: null,
            time: TimeProvider.System,
            runGit: Run);

        Assert.True(await worker.ProcessAsync(new WorkspaceArtifactPushRequest(_root, "ASS-CATCHUP"), default));
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(120)], pushTimeouts);
    }

    [Fact]
    public void WorkspacePushWorker_SchedulesConfiguredRepositoryForStartupCatchUp()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config);

        var request = Assert.IsType<WorkspaceArtifactPushRequest>(worker.StartupCatchUpRequest());
        Assert.Equal(Path.GetFullPath(_root), Path.GetFullPath(request.RepositoryRoot));
        Assert.Equal("workspace-startup-catch-up", request.JobId);
    }

    [Fact]
    public async Task WorkspacePushWorker_PersistsFinalAdvisoryWithAheadCount()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
            })
            .Build();
        var advisories = new AgentStudio.State.SupervisorAdvisoryStore();
        Task<WorkspaceArtifactPushWorker.PushGitResult> Run(
            string _, IReadOnlyList<string> args, TimeSpan __, CancellationToken ___)
        {
            if (args.Contains("--count"))
                return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(0, "73\n", "", GitProcessFailureKind.None));
            if (args.Contains("--disk-usage"))
                return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(0, "1024\n", "", GitProcessFailureKind.None));
            return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(1, "", "offline", GitProcessFailureKind.None));
        }
        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config,
            bus: null,
            advisories: advisories,
            time: TimeProvider.System,
            runGit: Run);

        Assert.False(await worker.ProcessAsync(
            new WorkspaceArtifactPushRequest(_root, "ASS-ADVISORY", Project: "demo"), default));

        var advisory = Assert.Single(advisories.Snapshot(_root, "demo"));
        Assert.Equal("workspace-repository-push-failed", advisory.Topic);
        Assert.Contains(_root, advisory.Message);
        Assert.Contains("ahead: 73", advisory.Message);
    }

    [Fact]
    public async Task WorkspacePushWorker_DrainsSimulatedTwoThousandCommitBacklogWithOnePush()
    {
        var pushes = 0;
        var config = new ConfigurationBuilder().Build();
        Task<WorkspaceArtifactPushWorker.PushGitResult> Run(
            string _, IReadOnlyList<string> args, TimeSpan __, CancellationToken ___)
        {
            if (args.Contains("--count"))
                return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(0, "2000\n", "", GitProcessFailureKind.None));
            if (args.Contains("--disk-usage"))
                return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(0, "2147483648\n", "", GitProcessFailureKind.None));
            pushes++;
            return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(0, "", "", GitProcessFailureKind.None));
        }
        var worker = new WorkspaceArtifactPushWorker(
            new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance,
            config,
            bus: null,
            advisories: null,
            time: TimeProvider.System,
            runGit: Run);

        Assert.True(await worker.ProcessAsync(new WorkspaceArtifactPushRequest(_root, "backlog"), default));
        Assert.Equal(1, pushes);
    }

    [Fact]
    public void ArtifactCommit_RefusesOversizedFileBeforeStaging()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WorkspaceArtifacts:MaximumFileBytes"] = (1024 * 1024).ToString(),
            })
            .Build();
        var service = new WorkspaceArtifactCommitService(
            config, NullLogger<WorkspaceArtifactCommitService>.Instance);
        var job = JobFolder("ASS-LARGE");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "small.txt"), "evidence\n");
        using (var stream = File.Create(Path.Combine(job, "large.bin")))
            stream.SetLength(2 * 1024 * 1024);

        var result = service.TryCommitArtifactUpload(_root, "ASS-LARGE", job, ["small.txt", "large.bin"]);

        Assert.True(result.DidCommit, result.Error);
        Assert.Contains("small.txt", RunGitCapture(_root, "show", "--name-only", "--format=", "HEAD"));
        Assert.DoesNotContain("large.bin", RunGitCapture(_root, "show", "--name-only", "--format=", "HEAD"));
        Assert.Contains("large.bin", RunGitCapture(_root, "status", "--short"));
    }

    [Fact]
    public void TrackedSweep_CommitsOrdinaryDriftButLeavesRuntimeStateUnstaged()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs", "bus"));
        File.WriteAllText(Path.Combine(_root, "logs", "bus", "events.jsonl"), "one\n");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "one\n");
        RunGit(_root, "add", "logs/bus/events.jsonl", "tracked.txt");
        RunGit(_root, "commit", "-q", "-m", "tracked files");
        File.AppendAllText(Path.Combine(_root, "logs", "bus", "events.jsonl"), "two\n");
        File.AppendAllText(Path.Combine(_root, "tracked.txt"), "two\n");

        var result = _service.TryCommitTrackedSweep(_root);

        Assert.True(result.DidCommit, result.Error);
        var committed = RunGitCapture(_root, "show", "--name-only", "--format=", "HEAD");
        Assert.Contains("tracked.txt", committed);
        Assert.DoesNotContain("logs/bus", committed);
        Assert.Contains("logs/bus/events.jsonl", RunGitCapture(_root, "status", "--short").Replace('\\', '/'));
    }

    private string JobFolder(string id) =>
        Path.Combine(_root, "projects", "agent-taskboard", "tasks", "001", id);

    private string Relative(string path) =>
        Path.GetRelativePath(_root, path).Replace('\\', '/');

    private static string PipelineJson(string stepId, string verdict, int? attempt = null)
    {
        var attemptLine = attempt.HasValue
            ? $",\n  \"attempt\": {attempt.Value}"
            : string.Empty;
        return
            "{\n" +
            "  \"steps\": [\n" +
            $"    {{ \"stepId\": \"{stepId}\", \"status\": \"Passed\", \"verdict\": \"{verdict}\" }}\n" +
            "  ]" +
            attemptLine + "\n" +
            "}\n";
    }

    private static string NumericPipelineJson() =>
        """
        {
          "steps": [
            { "stepId": "pre-loop-guard", "status": 2 },
            { "stepId": "core-agent-run", "status": 1 },
            { "stepId": "aspect-code-quality", "status": 2, "verdict": "Warn" },
            { "stepId": "post-orchestrator-decision", "status": 4 },
            { "stepId": "post-git-commit-attribution", "status": 5 }
          ],
          "attempt": 1
        }
        """;

    private static void RunGit(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
        return result.Out;
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
