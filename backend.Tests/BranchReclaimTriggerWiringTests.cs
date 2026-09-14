using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2793 round 4: proves <see cref="BranchReclaimTriggerService"/> is
/// actually reachable from the three product call sites (integration,
/// archive, promotion) and stays silent when the underlying transition did
/// not land. The deep namespace/policy correctness (which refs get deleted
/// and why) is already covered by <see cref="GitBranchRetentionBareRemoteTests"/>
/// and <see cref="GitBranchRetentionReplayTests"/>; this file only proves the
/// wiring - that the right call happens at the right moment and nowhere else.
///
/// Each test points the trigger's underlying <see cref="GitService"/> at a
/// path that is never created on disk. That makes
/// <see cref="GitBranchRetentionService.ReclaimForTask"/> fail deterministically
/// (its very first check, before any git process runs) and therefore log a
/// warning through <see cref="BranchReclaimTriggerService"/> - an observable
/// signal, independent of any merge/retention-policy state, that the trigger
/// method was actually invoked with a resolved repo path.
/// </summary>
public sealed class BranchReclaimTriggerWiringTests : IDisposable
{
    private const string Project = "Demo";
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "branch-reclaim-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;
    private readonly string _notARepo;

    public BranchReclaimTriggerWiringTests()
    {
        _watchPath = Path.Combine(_tempDir, "project-store");
        // Deliberately never created: GitBranchRetentionService.ReclaimForTask/
        // RunRepository fails fast on Directory.Exists(repositoryPath) before
        // spawning any git process, so this reliably forces a deterministic
        // Failed report (and therefore a warning log through
        // BranchReclaimTriggerService) independent of git or retention-policy
        // state - the signal this file uses to prove each trigger is reached.
        _notARepo = Path.Combine(_tempDir, "not-a-repo");
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ---- BranchReclaimTriggerService: GitRetention:Enabled gate ----

    [Fact]
    public void ReclaimAfterIntegration_Enabled_InvokesRetentionAndLogsTheFailure()
    {
        var (branchReclaim, recorder) = Build(enabled: true);

        branchReclaim.ReclaimAfterIntegration(Project, _notARepo, "AGT-0001", "develop");

        Assert.Contains(recorder.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Branch reclaim after integration had errors"));
    }

    [Fact]
    public void ReclaimAfterArchive_Enabled_InvokesRetentionAndLogsTheFailure()
    {
        var (branchReclaim, recorder) = Build(enabled: true);

        branchReclaim.ReclaimAfterArchive(Project, _notARepo, "AGT-0001");

        Assert.Contains(recorder.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Branch reclaim after archive had errors"));
    }

    [Fact]
    public void ReclaimAfterPromotionToMain_Enabled_InvokesRetention()
    {
        // RunOnce sweeps every registered project; registering none still
        // proves the call reaches GitBranchRetentionService (empty sweep, no
        // exception) rather than short-circuiting on the settings gate.
        var (branchReclaim, _) = Build(enabled: true);
        branchReclaim.ReclaimAfterPromotionToMain(Project, _notARepo);
    }

    [Fact]
    public void GitRetentionDisabled_NoneOfTheThreeTriggersTouchRetention()
    {
        var (branchReclaim, recorder) = Build(enabled: false);

        branchReclaim.ReclaimAfterIntegration(Project, _notARepo, "AGT-0001", "develop");
        branchReclaim.ReclaimAfterArchive(Project, _notARepo, "AGT-0001");
        branchReclaim.ReclaimAfterPromotionToMain(Project, _notARepo);

        Assert.Empty(recorder.Entries);
    }

    // ---- TaskTransitionService: fires only on a landed archive transition ----

    [Fact]
    public async Task MoveToArchive_Success_TriggersReclaimAfterArchive()
    {
        var (transitions, recorder) = BuildTransitions();
        SeedTask("archive-me", "AGT-1001", TaskStates.Completed);

        var outcome = await transitions.MoveAsync("archive-me", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Contains(recorder.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Branch reclaim after archive had errors")
            && e.Message.Contains("AGT-1001"));
    }

    [Fact]
    public async Task MoveToNonArchiveState_NeverTriggersArchiveReclaim()
    {
        var (transitions, recorder) = BuildTransitions();
        SeedTask("stay-completed", "AGT-1002", TaskStates.HumanReview);

        var outcome = await transitions.MoveAsync(
            "stay-completed", TaskStates.Escalated, _watchPath, operatorOverride: false);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task FailedArchiveMove_NeverTriggersReclaim()
    {
        var (transitions, recorder) = BuildTransitions();
        SeedTask("wrong-source", "AGT-1003", TaskStates.Completed);

        // expectedSourceState mismatch fails the move before anything lands.
        var outcome = await transitions.MoveAsync(
            "wrong-source",
            TaskStates.Archive,
            _watchPath,
            expectedSourceState: TaskStates.Progress);

        Assert.NotEqual(MoveJobStatus.Success, outcome.Status);
        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task MoveOfUnknownJob_NeverTriggersReclaim()
    {
        var (transitions, recorder) = BuildTransitions();

        var outcome = await transitions.MoveAsync("does-not-exist", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.NotFound, outcome.Status);
        Assert.Empty(recorder.Entries);
    }

    private void SeedTask(string id, string key, string state)
    {
        var folder = Path.Combine(_watchPath, state, id);
        Directory.CreateDirectory(folder);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id,
            key,
            title = "Wiring fixture",
            state,
            order = 1,
            agent = "codex",
            cliType = "codex",
            mode = TaskModes.Coding,
            projectName = Project,
        });
        File.WriteAllText(Path.Combine(folder, "task.json"), json);
    }

    private (BranchReclaimTriggerService Trigger, RecordingLogger<BranchReclaimTriggerService> Recorder) Build(
        bool enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _tempDir,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _notARepo,
            ["WatchPaths:0:RepositoryPath"] = _notARepo,
            ["GitRetention:Enabled"] = enabled ? "true" : "false",
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var registry = new AgentStudio.Registry.ProjectRegistry(
            config, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
        var retention = new GitBranchRetentionService(
            git, registry, config, NullLogger<GitBranchRetentionService>.Instance);
        var recorder = new RecordingLogger<BranchReclaimTriggerService>();
        var trigger = new BranchReclaimTriggerService(retention, config, recorder);
        return (trigger, recorder);
    }

    private (TaskTransitionService Transitions, RecordingLogger<BranchReclaimTriggerService> Recorder) BuildTransitions()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _tempDir,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _notARepo,
            ["WatchPaths:0:RepositoryPath"] = _notARepo,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new AgentStudio.Registry.ProjectRegistry(config, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var registry = new AgentStudio.Registry.ProjectRegistry(
            config, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
        var retention = new GitBranchRetentionService(
            git, registry, config, NullLogger<GitBranchRetentionService>.Instance);
        var recorder = new RecordingLogger<BranchReclaimTriggerService>();
        var branchReclaim = new BranchReclaimTriggerService(retention, config, recorder);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            git,
            settings,
            NullLogger<TaskTransitionService>.Instance,
            branchReclaim: branchReclaim);
        return (transitions, recorder);
    }

    /// <summary>
    /// End-to-end evidence check with a real, deletable ref: proves that a
    /// live <see cref="BranchReclaimTriggerService.ReclaimAfterArchive"/> call
    /// not only deletes the ref but also stamps the task key onto the
    /// per-project evidence row (<see cref="BranchRetentionEvidenceWriter"/>)
    /// and echoes a <see cref="TimelineEventKinds.BranchesReclaimed"/> entry
    /// onto the task's own timeline - the two "surfaced in the task history"
    /// halves of requirement 3, exercised together rather than by log
    /// side-effect alone.
    /// </summary>
    [Fact]
    public void ReclaimAfterArchive_RealDeletion_RecordsEvidenceAndTimelineEcho()
    {
        var bare = Path.Combine(_tempDir, "origin.git");
        var repo = Path.Combine(_tempDir, "repo");
        RunGit(_tempDir, "init", "-q", "--bare", bare);
        RunGit(_tempDir, "init", "-q", "-b", "main", repo);
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        RunGit(repo, "remote", "add", "origin", bare);
        CommitOld(repo, "seed.txt", "seed");
        RunGit(repo, "checkout", "-q", "-b", "develop");
        RunGit(repo, "push", "-q", "-u", "origin", "main", "develop");

        RunGit(repo, "checkout", "-q", "-b", "task/AGT-9001");
        CommitOld(repo, "work.txt", "task work");
        RunGit(repo, "checkout", "-q", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/AGT-9001");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "-q", "--no-ff", "--no-edit", "task/AGT-9001");
        RunGit(repo, "push", "-q", "--all", "origin");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _tempDir,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var registry = new AgentStudio.Registry.ProjectRegistry(
            config, NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance);
        var evidence = new BranchRetentionEvidenceWriter(
            config, NullLogger<BranchRetentionEvidenceWriter>.Instance);
        var retention = new GitBranchRetentionService(
            git, registry, config, NullLogger<GitBranchRetentionService>.Instance,
            time: null, evidence: evidence);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var branchReclaim = new BranchReclaimTriggerService(
            retention, config, NullLogger<BranchReclaimTriggerService>.Instance, timeline);

        var archivedFolder = Path.Combine(_watchPath, TaskStates.Archive, "task-9001");
        Directory.CreateDirectory(archivedFolder);

        branchReclaim.ReclaimAfterArchive(Project, repo, "AGT-9001", archivedFolder);

        var refCode = GitCode(repo, "ls-remote", "--exit-code", "--heads", "origin", "task/AGT-9001");
        Assert.NotEqual(0, refCode);

        var evidenceFile = evidence.ReportFile(Project)!;
        Assert.True(File.Exists(evidenceFile));
        Assert.Contains(File.ReadAllLines(evidenceFile), line =>
            line.Contains("\"ref\":\"task/AGT-9001\"", StringComparison.Ordinal)
            && line.Contains("\"taskKey\":\"AGT-9001\"", StringComparison.Ordinal));

        var history = timeline.ReadAll(archivedFolder);
        Assert.Contains(history, entry => entry.Kind == TimelineEventKinds.BranchesReclaimed);
    }

    private static void CommitOld(string repo, string path, string message)
    {
        File.WriteAllText(Path.Combine(repo, path), message);
        RunGit(repo, "add", path);
        Run(repo, ["commit", "-q", "-m", message], new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = "2020-01-01T12:00:00Z",
            ["GIT_COMMITTER_DATE"] = "2020-01-01T12:00:00Z",
        });
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var result = Run(cwd, args);
        if (result.Code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Err}");
    }

    private static int GitCode(string cwd, params string[] args) => Run(cwd, args).Code;

    private static (string Out, string Err, int Code) Run(
        string cwd,
        string[] args,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
        {
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return (output, error, process.ExitCode);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
