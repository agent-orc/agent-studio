using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2995: every <see cref="MergeIntoIntegrationOutcome.Error"/> the merge
/// runner produces names a typed failure code, and the
/// <c>merge-into-develop ... outcome=Error</c> log line carries it together with
/// a one-line reason. On 28.09.2026 QS-106 logged only <c>outcome=Error</c> for
/// eight hours while the real cause (<c>source-needs-rebase</c>) sat in
/// <c>pipeline-execution.json</c>. One test per Error site; each drives the real
/// runner against a throwaway repository.
/// </summary>
public sealed class MergeIntoDevelopRunnerFailureCodeTests : IDisposable
{
    private readonly string _tempDir;

    public MergeIntoDevelopRunnerFailureCodeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "merge-failure-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void RepositoryRootUnavailable_NamesItsCodeInResultStepAndLog()
    {
        // No registered watch path: neither the card nor a single-project
        // fallback names a repository.
        var config = new ConfigurationBuilder().Build();
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var jobFolder = BeginRun(log, "root-unavailable");
        var logger = new RecordingLogger<MergeIntoDevelopRunner>();
        var runner = new MergeIntoDevelopRunner(git, log, logger);

        var outcome = runner.Run("Fixture", "root-unavailable", jobFolder, watchPath: null, "develop");

        AssertTypedError(
            outcome, log, jobFolder, logger, AcceptedIntegrationFailureCodes.RepositoryRootUnavailable);
    }

    [Fact]
    public void StaleAttempt_NamesItsCodeInResultStepAndLog()
    {
        var repo = SeedRepo("stale-attempt");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b runner/agent-runner-01/AGT-STALE");
        File.WriteAllText(Path.Combine(repo, "stale.txt"), "superseded work");
        Commit(repo, "feat: superseded remote work");
        var staleSha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, "checkout -q develop");

        var authority = new AttemptAuthorityService(
            new ConfigurationBuilder().Build(),
            NullLogger<AttemptAuthorityService>.Instance);
        var staleRun = authority.AcquireRun(
            "AGT-STALE", "PROJ-FIXTURE", null,
            "agent-runner-01", "host-a", 60, "claim-stale").RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(
                staleRun.AttemptId, staleRun.LastFence, staleRun.AuthorityEpoch, "settle-stale"),
            Outcome = "done",
            ResultSha = staleSha,
        }).Accepted);
        authority.AcquireRun(
            "AGT-STALE", "PROJ-FIXTURE", staleRun.AttemptId,
            "local", "host-local", 60, "claim-current");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, "AGT-STALE");
        File.WriteAllText(Path.Combine(jobFolder, "task.json"), """{"id":"AGT-STALE","key":"AGT-STALE"}""");
        ReviewSubjectStore.Write(jobFolder, new ReviewSubjectRecord
        {
            TaskKey = "AGT-STALE",
            RunAttemptId = staleRun.AttemptId,
            Project = "Fixture",
            Repository = repo,
            ResultSha = staleSha,
            AttemptChainId = staleRun.Lease!.LeaseId,
            Executor = "agent-runner-01",
            LeaseId = staleRun.Lease.LeaseId,
            FencingToken = staleRun.LastFence,
            ResultRef = "runner/agent-runner-01/AGT-STALE",
            IntegrationBranch = "develop",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
        var logger = new RecordingLogger<MergeIntoDevelopRunner>();
        var runner = new MergeIntoDevelopRunner(git, log, logger, attemptAuthority: authority);

        var outcome = runner.Run("Fixture", "AGT-STALE", jobFolder, repo, "develop");

        AssertTypedError(outcome, log, jobFolder, logger, AcceptedIntegrationFailureCodes.StaleAttempt);
        Assert.Contains(staleRun.AttemptId, outcome.Error);
    }

    [Fact]
    public void WorktreeUnavailable_NamesItsCodeInResultStepAndLog()
    {
        var repo = SeedRepo("worktree-unavailable");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/wt");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");

        // Occupy every integration worktree slot with a plain file, so no
        // candidate can be registered.
        var temporaryRoot = Path.Combine(_tempDir, "integration-temp");
        foreach (var candidate in IntegrationWorktreePolicy.CandidatePaths(repo, temporaryRoot))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
            File.WriteAllText(candidate, "not a worktree");
        }

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, "wt");
        var logger = new RecordingLogger<MergeIntoDevelopRunner>();
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            logger,
            integrationWorktrees: new IntegrationWorktreeProvider(git, temporaryRoot: temporaryRoot));

        var outcome = runner.Run("Fixture", "wt", jobFolder, repo, "develop");

        AssertTypedError(outcome, log, jobFolder, logger, AcceptedIntegrationFailureCodes.WorktreeUnavailable);
        Assert.NotEqual(0, RunGit(repo, "merge-base --is-ancestor task/wt develop").Code);
    }

    [Fact]
    public void BranchSyncFailed_NamesItsCodeInResultStepAndLog()
    {
        var repo = SeedRepo("branch-sync");
        var origin = Path.Combine(_tempDir, "branch-sync-origin.git");
        RunGit(_tempDir, $"init --bare -q --initial-branch=main \"{origin}\"");
        RunGit(repo, $"remote add origin \"{origin}\"");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q origin main develop");

        // Somebody else advances origin/develop ...
        var other = Path.Combine(_tempDir, "branch-sync-other");
        RunGit(_tempDir, $"clone -q -b develop \"{origin}\" \"{other}\"");
        RunGit(other, "config user.email other@example.com");
        RunGit(other, "config user.name other");
        File.WriteAllText(Path.Combine(other, "remote.txt"), "remote-only work");
        Commit(other, "chore: remote-only develop work");
        RunGit(other, "push -q origin develop");

        // ... while local develop grows its own commit: the two diverged.
        File.WriteAllText(Path.Combine(repo, "local.txt"), "local-only work");
        Commit(repo, "chore: local-only develop work");
        RunGit(repo, "checkout -q -b task/sync");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        RunGit(repo, "checkout -q develop");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, "sync");
        var logger = new RecordingLogger<MergeIntoDevelopRunner>();
        var runner = new MergeIntoDevelopRunner(git, log, logger);

        var outcome = runner.Run("Fixture", "sync", jobFolder, repo, "develop");

        AssertTypedError(outcome, log, jobFolder, logger, AcceptedIntegrationFailureCodes.BranchSyncFailed);
        Assert.Contains("diverged", outcome.Error);
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());
    }

    [Fact]
    public async Task LineageBlocked_NamesItsCodeInResultStepAndLog()
    {
        var repo = SeedRepo("lineage-blocked");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "develop.txt"), "develop-only work");
        Commit(repo, "chore: advance develop");
        RunGit(repo, "checkout -q -b task/lineage");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "delivery work");
        Commit(repo, "feat: delivery work");
        RunGit(repo, "checkout -q main");
        File.WriteAllText(Path.Combine(repo, "main.txt"), "main-only work");
        Commit(repo, "chore: advance main independently");

        var (git, log) = Build(repo);
        var jobFolder = BeginRun(log, "lineage");
        var logger = new RecordingLogger<MergeIntoDevelopRunner>();
        var runner = new MergeIntoDevelopRunner(git, log, logger);

        var outcome = await runner.RunAsync(
            "Fixture", "lineage", jobFolder, repo, "main", CancellationToken.None);

        AssertTypedError(outcome, log, jobFolder, logger, AcceptedIntegrationFailureCodes.LineageBlocked);
        Assert.Contains("main is not an ancestor of develop", outcome.Error);
    }

    [Fact]
    public void RebaseAttributionFailed_NamesItsCodeInResultStepAndLog()
    {
        var repo = SeedRepo("rebase-attribution");
        RunGit(repo, "checkout -q -b develop");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "a\n");
        Commit(repo, "chore: shared base");

        // The delivery first makes the change develop also makes, then builds
        // on it. A direct three-way merge conflicts (base a, ours b, theirs c);
        // replaying commit by commit onto develop is clean.
        RunGit(repo, "checkout -q -b task/rebase");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "b\n");
        File.WriteAllText(Path.Combine(repo, "delivery-a.txt"), "delivery a");
        Commit(repo, "feat: delivery step a");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "c\n");
        Commit(repo, "feat: delivery step b");
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "b\n");
        File.WriteAllText(Path.Combine(repo, "integration.txt"), "integration work");
        Commit(repo, "chore: develop moved on");
        var developBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var (git, log) = Build(repo);
        // The job folder carries no persisted commit chain, so the mechanical
        // rebase attribution cannot be written and the merge is rolled back.
        var jobFolder = BeginRun(log, "rebase");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:Path"] = Path.Combine(_tempDir, "jobs"),
            ["TaskRepository"] = Path.Combine(_tempDir, "store"),
        }).Build();
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var logger = new RecordingLogger<MergeIntoDevelopRunner>();
        var runner = new MergeIntoDevelopRunner(git, log, logger, taskMutations: mutations);

        var outcome = runner.Run("Fixture", "rebase", jobFolder, repo, "develop");

        AssertTypedError(
            outcome, log, jobFolder, logger, AcceptedIntegrationFailureCodes.RebaseAttributionFailed);
        Assert.Contains("rolled back", outcome.Error);
        Assert.Equal(developBefore, RunGit(repo, "rev-parse develop").Out.Trim());
    }

    [Fact]
    public void OneLine_CollapsesAMultiLineDiagnosticIntoOneBoundedField()
    {
        Assert.Equal("none", MergeIntoDevelopRunner.OneLine(null));
        Assert.Equal(
            "first line second line",
            MergeIntoDevelopRunner.OneLine("first line\r\n  second line\n"));
        Assert.Equal(300, MergeIntoDevelopRunner.OneLine(new string('x', 1000)).Length);
    }

    private static void AssertTypedError(
        MergeIntoIntegrationResult outcome,
        PipelineExecutionLog log,
        string jobFolder,
        RecordingLogger<MergeIntoDevelopRunner> logger,
        string expectedCode)
    {
        Assert.Equal(MergeIntoIntegrationOutcome.Error, outcome.Outcome);
        Assert.Equal(expectedCode, outcome.FailureCode);

        var step = log.Read(jobFolder)!.Steps.Single(item => item.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal(PipelineStepStatus.Failed, step.Status);
        Assert.Equal(expectedCode, step.FailureCode);

        var line = Assert.Single(
            logger.Messages,
            message => message.StartsWith("merge-into-develop project=", StringComparison.Ordinal)
                       && message.Contains("outcome=Error", StringComparison.Ordinal));
        Assert.Contains($" failureCode={expectedCode} ", line);
        var reason = line[(line.IndexOf(" reason=", StringComparison.Ordinal) + " reason=".Length)..];
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.NotEqual("none", reason);
        Assert.DoesNotContain('\n', reason);
        Assert.Equal(MergeIntoDevelopRunner.OneLine(outcome.Error), reason);
    }

    private string BeginRun(PipelineExecutionLog log, string jobId)
    {
        var jobFolder = Path.Combine(_tempDir, "jobs", jobId);
        Directory.CreateDirectory(jobFolder);
        log.Begin(jobFolder, PipelineCatalogue.Standard, "Fixture", jobId);
        return jobFolder;
    }

    private (GitService Git, PipelineExecutionLog Log) Build(string repo)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return (
            new GitService(NullLogger<GitService>.Instance, scanner, config),
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance));
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        return repo;
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return (stdout, stderr, process.ExitCode);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }
}
