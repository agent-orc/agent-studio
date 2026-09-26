using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderRejectionContinuationTests : IDisposable
{
    private const string ProjectName = "demo";
    private const string JobId = "agt-2874-provider-refusal";
    private const string SalvageBranch = "agent-studio/results/AGT-2874/run-one";
    private const string SalvageSha = "521fd1e3a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private static readonly ProviderRejectionModelFallback Fallback = new()
    {
        CliType = CliTypes.Codex,
        FromModel = ModelIds.Gpt6Astra,
        ToModel = ModelIds.Gpt56Sol,
        Reason = "Declared provider-refusal sibling.",
    };
    private static readonly ProviderRequestRejection Rejection = new(
        "unsupported_parameter",
        "access_programs.cyber",
        "The access_programs parameter is not enabled for this organization.",
        400);

    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;

    public ProviderRejectionContinuationTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-2874-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
            ["WatchPaths:0:RepositoryPath"] = _watchPath,
            ["TaskRepository"] = _workspaceRoot,
        }).Build();
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
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Policy_escalates_without_a_sibling_or_when_the_sibling_is_below_the_floor()
    {
        Assert.Equal(
            ProviderRejectionContinuationAction.Escalate,
            ProviderRejectionContinuationPolicy.Decide(true, true, null, true, 1, false).Action);
        Assert.Equal(
            ProviderRejectionContinuationAction.Escalate,
            ProviderRejectionContinuationPolicy.Decide(true, true, Fallback, false, 1, false).Action);
    }

    [Fact]
    public void Policy_keeps_first_fallback_run_scoped_and_pins_on_the_second_refusal()
    {
        var first = ProviderRejectionContinuationPolicy.Decide(true, true, Fallback, true, 1, false);
        var second = ProviderRejectionContinuationPolicy.Decide(true, true, Fallback, true, 2, false);

        Assert.Equal(ProviderRejectionContinuationAction.StartContinuation, first.Action);
        Assert.False(first.PinCard);
        Assert.Equal(ProviderRejectionContinuationAction.StartContinuation, second.Action);
        Assert.True(second.PinCard);
    }

    [Fact]
    public async Task First_refusal_queues_sibling_without_changing_the_card_model_and_records_fallback()
    {
        WriteJob();
        var task = _scanner.FindJob(JobId, _watchPath)!;
        var result = await BuildService().StartAsync(
            task,
            new RunSalvageReference(SalvageBranch, SalvageSha),
            Fallback,
            Rejection,
            "high",
            pinCard: false,
            "attempt-1",
            Write("attempt-1"),
            default);

        Assert.True(result.Started, result.Reason);
        var queued = _scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(TaskStates.Ready, queued.State);
        Assert.Equal(ModelIds.Gpt6Astra, queued.Model);
        Assert.Equal(ModelIds.Gpt56Sol, queued.PendingIntent!.ModelFallback!.To);
        Assert.Equal("high", queued.PendingIntent.ModelFallback.ThinkingLevel);
        Assert.Equal("provider-rejection", queued.PendingIntent.ModelFallback.Reason);
        var entry = Assert.Single(_timeline.ReadAll(queued.FolderPath), item =>
            item.Kind == TimelineEventKinds.ProviderRejectionContinuationStarted);
        Assert.Contains("continued on gpt-5.6-sol", entry.Summary, StringComparison.Ordinal);
        Assert.Equal("false", entry.Details!["cardPinned"]);
    }

    [Fact]
    public async Task Second_refusal_pins_the_card_to_the_sibling()
    {
        WriteJob();
        var result = await BuildService().StartAsync(
            _scanner.FindJob(JobId, _watchPath)!,
            new RunSalvageReference(SalvageBranch, SalvageSha),
            Fallback,
            Rejection,
            "high",
            pinCard: true,
            "attempt-2",
            Write("attempt-2"),
            default);

        Assert.True(result.Started, result.Reason);
        Assert.True(result.CardPinned);
        var queued = _scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(ModelIds.Gpt56Sol, queued.Model);
        Assert.Equal("high", queued.ThinkingLevel);
        Assert.Contains("Card pinned", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_lane_move_restores_card_but_keeps_refusal_and_provider_message()
    {
        WriteJob();
        var task = _scanner.FindJob(JobId, _watchPath)!;
        var originalPrompt = File.ReadAllBytes(Path.Combine(task.FolderPath, "prompt.md"));
        var originalTask = File.ReadAllBytes(Path.Combine(task.FolderPath, "task.json"));
        _timeline.Append(
            task.FolderPath,
            TimelineEventKinds.AgentRunFinished,
            TimelineActors.Agent,
            "Provider refused the request.",
            runId: "attempt-failed-move",
            details: new Dictionary<string, string>
            {
                ["typedOutcome"] = ExecutionOutcomeKind.ProviderRejectedRequest.ToString(),
                ["providerRejectionModel"] = ModelIds.Gpt6Astra,
                ["providerRejectionMessage"] = Rejection.Message,
            });
        var service = new ProviderRejectionContinuationService(
            _scanner,
            _mutations,
            _states,
            _transitions,
            _timeline,
            NullLogger<ProviderRejectionContinuationService>.Instance,
            (_, _, _) => Task.FromResult(new MoveJobOutcome(
                MoveJobStatus.Failure,
                "injected move failure")));

        var result = await service.StartAsync(
            task,
            new RunSalvageReference(SalvageBranch, SalvageSha),
            Fallback,
            Rejection,
            "high",
            pinCard: true,
            "attempt-failed-move",
            Write("attempt-failed-move"),
            default);

        Assert.False(result.Started);
        var unchanged = _scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(TaskStates.Progress, unchanged.State);
        Assert.Equal(ModelIds.Gpt6Astra, unchanged.Model);
        Assert.Equal("high", unchanged.ThinkingLevel);
        Assert.Null(unchanged.ContextMode);
        Assert.Null(unchanged.PendingIntent);
        Assert.Equal(originalPrompt, File.ReadAllBytes(Path.Combine(unchanged.FolderPath, "prompt.md")));
        Assert.Equal(originalTask, File.ReadAllBytes(Path.Combine(unchanged.FolderPath, "task.json")));
        Assert.Single(_timeline.ReadAll(unchanged.FolderPath), entry =>
            entry.Details?.GetValueOrDefault("typedOutcome")
            == ExecutionOutcomeKind.ProviderRejectedRequest.ToString());
        var escalation = ProviderRejectionContinuationPolicy.ComposeEscalationReason(Rejection, result.Reason);
        Assert.Contains(Rejection.Message, escalation, StringComparison.Ordinal);
        Assert.Contains("injected move failure", escalation, StringComparison.Ordinal);
    }

    private ProviderRejectionContinuationService BuildService() => new(
        _scanner,
        _mutations,
        _states,
        _transitions,
        _timeline,
        NullLogger<ProviderRejectionContinuationService>.Instance);

    private static AttemptWriteReference Write(string attemptId)
        => new(attemptId, 7, 1, $"lane-completion:{attemptId}");

    private void WriteJob()
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, JobId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = JobId,
            key = "AGT-2874",
            title = "Provider refusal fallback",
            state = TaskStates.Progress,
            order = 1,
            agent = CliTypes.Codex,
            cliType = CliTypes.Codex,
            model = ModelIds.Gpt6Astra,
            thinkingLevel = "high",
            taskType = TaskTypes.Bug,
            mode = TaskModes.Coding,
            projectName = ProjectName,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "# Original task\n\nFix the provider refusal.\n");
        _scanner.InvalidateCache();
    }
}
