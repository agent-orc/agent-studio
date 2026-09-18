using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2861: two Opus coding runs hit the 90-minute run timeout with a
/// buildable, salvaged worktree. The runner did the right thing, and the task
/// server then escalated the card as if nothing had been delivered, so an
/// operator had to read the journal for the salvage SHA and hand-write a
/// continuation prompt. A timed-out run with a salvage commit is the same shape
/// as an integration conflict: the delivery exists, it only lacks its finishing
/// round.
/// </summary>
public sealed class RunTimeoutSalvageContinuationTests : IDisposable
{
    private const string ProjectName = "demo";
    private const string JobId = "agt-2861-timeout";
    private const string SalvageBranch = "agent-studio/results/AGT-2858/run-f419c075";
    private const string SalvageSha = "521fd1e3a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;

    public RunTimeoutSalvageContinuationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-2861-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot,
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        _scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        _scanner.SetIndexCache(new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, config));
        _states = new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance);
        _mutations = new TaskMutationService(
            _scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, _scanner, config, prompts);
        _transitions = new TaskTransitionService(
            _scanner, _states, _mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        _timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ---- pure policy matrix -------------------------------------------------

    [Theory]
    [InlineData("unknown", true, 0, RunTimeoutSalvageAction.StartContinuation)]
    [InlineData("Unknown", true, 0, RunTimeoutSalvageAction.StartContinuation)]
    [InlineData("unknown", true, 1, RunTimeoutSalvageAction.Escalate)]
    [InlineData("unknown", true, 4, RunTimeoutSalvageAction.Escalate)]
    [InlineData("unknown", false, 0, RunTimeoutSalvageAction.Escalate)]
    [InlineData("blocked", true, 0, RunTimeoutSalvageAction.None)]
    [InlineData("needsinput", true, 0, RunTimeoutSalvageAction.None)]
    [InlineData("done", true, 0, RunTimeoutSalvageAction.None)]
    [InlineData("", true, 0, RunTimeoutSalvageAction.None)]
    public void Policy_decides_continuation_only_for_a_salvaged_non_terminal_outcome(
        string outcome,
        bool hasSalvage,
        int roundsUsed,
        RunTimeoutSalvageAction expected)
    {
        Assert.Equal(
            expected,
            RunTimeoutSalvageContinuationPolicy.Decide(outcome, hasSalvage, roundsUsed));
    }

    [Fact]
    public void Salvage_reference_needs_both_a_ref_and_a_commit_and_prefers_the_recovery_pair()
    {
        Assert.Null(RunSalvageReference.From(SalvageBranch, null, null, null));
        Assert.Null(RunSalvageReference.From(null, SalvageSha, null, null));
        Assert.Null(RunSalvageReference.From("  ", "  ", null, null));

        var canonical = RunSalvageReference.From(SalvageBranch, SalvageSha, null, null);
        Assert.Equal(SalvageBranch, canonical!.Branch);
        Assert.Equal(SalvageSha, canonical.CommitSha);

        var divergent = RunSalvageReference.From(
            SalvageBranch, SalvageSha, "recovery/branch", "f0915674a1b2c3d4e5f60718293a4b5c6d7e8f90");
        Assert.Equal("recovery/branch", divergent!.Branch);
        Assert.Equal("f0915674a1b2c3d4e5f60718293a4b5c6d7e8f90", divergent.CommitSha);
    }

    [Fact]
    public void Escalation_without_a_salvage_reads_exactly_as_it_did_before()
    {
        Assert.Equal(
            "The remote runner ended without a recognized terminal outcome: Timeout",
            RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason("Timeout", null, 0));
        Assert.Equal(
            "The remote runner ended without a recognized terminal outcome.",
            RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason("  ", null, 0));
    }

    [Fact]
    public void Escalation_with_a_salvage_names_the_ref_the_sha_and_the_spent_rounds()
    {
        var salvage = new RunSalvageReference(SalvageBranch, SalvageSha);

        var first = RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason("Timeout", salvage, 0);
        Assert.Contains("Timeout", first);
        Assert.Contains(SalvageBranch, first);
        Assert.Contains(SalvageSha, first);
        Assert.DoesNotContain("continuation round", first);

        var second = RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason("Timeout", salvage, 1);
        Assert.Contains(SalvageSha, second);
        Assert.Contains("1 automatic continuation round was already spent", second);
    }

    // ---- bounded side effects ----------------------------------------------

    [Fact]
    public async Task Timeout_with_a_salvage_starts_exactly_one_continuation_round()
    {
        WriteJob(TaskStates.Progress);
        var service = BuildService();
        var task = _scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(0, service.CountAutomaticRounds(task));

        var started = await service.StartAsync(
            task, new RunSalvageReference(SalvageBranch, SalvageSha), "Timeout", "attempt-1", Write(), default);

        Assert.True(started.Started, started.Reason);
        Assert.Equal(1, started.Round);

        var queued = _scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);

        // The next claim carries the finishing instruction: a saved intent plus
        // the prompt.md addendum the remote runner fetches verbatim.
        Assert.Equal(ContinueModes.Steer, queued.PendingIntent?.Mode);
        Assert.Equal(
            RunTimeoutSalvageContinuationPolicy.ContinuationReason,
            queued.PendingIntent?.SavedReason);
        var prompt = File.ReadAllText(Path.Combine(queued.FolderPath, "prompt.md"));
        Assert.Contains(SalvageBranch, prompt);
        Assert.Contains(SalvageSha, prompt);
        Assert.Contains("run timeout", prompt);
        Assert.Contains("results/status.md", prompt);

        // Same model as the timed-out round, clean context.
        Assert.Equal("claude", queued.CliType);
        Assert.Equal("claude-opus-5", queued.Model);
        Assert.Equal(CliContextModes.Clean, queued.ContextMode);

        var round = Assert.Single(
            _timeline.ReadAll(queued.FolderPath),
            evt => evt.Kind == TimelineEventKinds.ContinuationRoundStarted);
        Assert.Equal(TimelineActors.System, round.Actor);
        Assert.Equal("true", round.Details!["automatic"]);
        Assert.Equal(
            RunTimeoutSalvageContinuationPolicy.ContinuationReason,
            round.Details["reason"]);
        Assert.Equal(SalvageBranch, round.Details["salvageBranch"]);
        Assert.Equal(SalvageSha, round.Details["salvageCommitSha"]);
        Assert.Equal("1", round.Details["round"]);

        // The budget is spent for this delivery generation.
        Assert.Equal(1, service.CountAutomaticRounds(queued));
        Assert.Equal(
            RunTimeoutSalvageAction.Escalate,
            RunTimeoutSalvageContinuationPolicy.Decide(
                "unknown", hasSalvageCommit: true, service.CountAutomaticRounds(queued)));
    }

    [Fact]
    public async Task A_second_timeout_in_the_same_generation_escalates_with_the_salvage_sha()
    {
        WriteJob(TaskStates.Progress);
        var service = BuildService();
        var salvage = new RunSalvageReference(SalvageBranch, SalvageSha);
        Assert.True((await service.StartAsync(
            _scanner.FindJob(JobId, _watchPath)!, salvage, "Timeout", "attempt-1", Write(), default)).Started);

        // The continuation round times out again on the same delivery generation.
        var second = _scanner.FindJob(JobId, _watchPath)!;
        var roundsUsed = service.CountAutomaticRounds(second);
        Assert.Equal(
            RunTimeoutSalvageAction.Escalate,
            RunTimeoutSalvageContinuationPolicy.Decide("unknown", hasSalvageCommit: true, roundsUsed));

        var reason = RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason(
            "Timeout", salvage, roundsUsed);
        Assert.Contains("without a recognized terminal outcome: Timeout", reason);
        Assert.Contains(SalvageBranch, reason);
        Assert.Contains(SalvageSha, reason);
        Assert.Contains("1 automatic continuation round was already spent", reason);
    }

    [Fact]
    public async Task A_refused_lane_move_rolls_back_the_prepared_prompt_intent_and_context_mode()
    {
        WriteJob(TaskStates.Progress);
        var original = _scanner.FindJob(JobId, _watchPath)!;
        var promptPath = Path.Combine(original.FolderPath, "prompt.md");
        var originalPrompt = File.ReadAllText(promptPath);

        // A file at the destination path makes the state-machine move fail
        // without changing the source folder.
        File.WriteAllText(Path.Combine(_watchPath, TaskStates.Ready, JobId), "collision");

        var result = await BuildService().StartAsync(
            original,
            new RunSalvageReference(SalvageBranch, SalvageSha),
            "Timeout",
            "attempt-1",
            Write(),
            default);

        Assert.False(result.Started);
        Assert.Contains(nameof(MoveJobStatus.TargetFolderExists), result.Reason);

        var unchanged = _scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(TaskStates.Progress, unchanged.State);
        Assert.Equal(originalPrompt, File.ReadAllText(promptPath));
        Assert.Null(unchanged.PendingIntent);
        Assert.Null(unchanged.ContextMode);
        using var taskJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(unchanged.FolderPath, "task.json")));
        Assert.False(taskJson.RootElement.TryGetProperty("contextMode", out _));
        Assert.DoesNotContain(
            _timeline.ReadAll(unchanged.FolderPath),
            evt => evt.Kind == TimelineEventKinds.ContinuationRoundStarted);
    }

    [Fact]
    public async Task An_operator_requeue_opens_a_fresh_continuation_budget()
    {
        WriteJob(TaskStates.Progress);
        var service = BuildService();
        var salvage = new RunSalvageReference(SalvageBranch, SalvageSha);
        Assert.True((await service.StartAsync(
            _scanner.FindJob(JobId, _watchPath)!, salvage, "Timeout", "attempt-1", Write(), default)).Started);

        var queued = _scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(1, service.CountAutomaticRounds(queued));

        // The operator reopened the card, which opens a new review-attempt
        // epoch: the spent round belongs to the previous delivery generation.
        new OperatorReviewRequeueService(
                _workspaceRoot, NullLogger<OperatorReviewRequeueService>.Instance, _timeline)
            .Apply(
                queued.FolderPath, JobId, ProjectName,
                TaskStates.Escalated, TaskStates.Ready, "fresh assessment", "human");

        Assert.Equal(0, service.CountAutomaticRounds(_scanner.FindJob(JobId, _watchPath)!));
    }

    private RunTimeoutContinuationService BuildService()
        => new(
            _scanner,
            _mutations,
            _states,
            _transitions,
            _timeline,
            NullLogger<RunTimeoutContinuationService>.Instance);

    private static AttemptWriteReference Write()
        => new("attempt-1", 7, 1, "lane-completion:completion-1");

    private void WriteJob(string state)
    {
        var folder = Path.Combine(_watchPath, state, JobId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                new
                {
                    id = JobId,
                    key = "AGT-2858",
                    title = "Temp-root hygiene guard",
                    state,
                    order = 1,
                    agent = "claude",
                    cliType = "claude",
                    model = "claude-opus-5",
                    mode = TaskModes.Coding,
                    projectName = ProjectName,
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "# Original task\n\nDo the work.\n");
        _scanner.InvalidateCache();
    }
}
