using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end tests for the AGT-2795 decision card: kind validation on create,
/// the decide/reopen transitions, blocking of dependants, the ADR-style record,
/// and the timeline events.
/// </summary>
public class DecisionCardServiceTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public DecisionCardServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-decision-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private static DecisionContent SampleContent() => new()
    {
        Question = "Ship with a lock file, or without?",
        Options =
        [
            new DecisionOption { Id = "a", Label = "Lock file", Consequences = "Reproducible installs", Effort = "S", Risks = "Lockfile churn" },
            new DecisionOption { Id = "b", Label = "No lock file", Consequences = "Simpler tree", Effort = "S", Risks = "Version drift", Requirements = "Remove the committed lock file and document the policy." },
        ],
        RecommendedOptionId = "a",
        RecommendationReason = "Reproducibility outweighs churn.",
        Decider = DecisionDeciders.Operator,
    };

    [Fact]
    public void CreateDecisionCard_PersistsContent_LandsInPreparation_IsNoBranch()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "lockfile-decision",
            Title = "Stable release contract",
            WatchPath = _watchPath,
            Kind = TaskKinds.Decision,
            Decision = SampleContent(),
        });

        Assert.NotNull(jobId);
        var info = h.Scanner.FindJob(jobId!, _watchPath)!;
        Assert.Equal(TaskKinds.Decision, info.Kind);
        Assert.Equal(TaskStates.Preparation, info.State);
        Assert.True(info.NoBranchExpected);
        Assert.NotNull(info.Decision);
        Assert.Equal(DecisionStatuses.Requested, info.Decision!.Status);
        Assert.Equal(2, info.Decision.Options.Count);
        Assert.Equal("a", info.Decision.RecommendedOptionId);
    }

    [Fact]
    public void CreateDecisionCard_WithMalformedContent_IsRejected()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "bad-decision",
            Title = "Bad",
            WatchPath = _watchPath,
            Kind = TaskKinds.Decision,
            Decision = new DecisionContent { Question = "", Options = [] },
        });
        Assert.Null(jobId);
    }

    [Fact]
    public async Task Decide_RecordsChoice_WritesRecord_MovesToCompleted_AndEmitsTimeline()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "decide-me",
            Title = "Stable release contract",
            WatchPath = _watchPath,
            Kind = TaskKinds.Decision,
            Decision = SampleContent(),
        })!;

        var outcome = await h.Decisions.DecideAsync(
            jobId, _watchPath, new DecideCardRequest { OptionId = "b", Rationale = "Team prefers a simpler tree." }, "alice");

        Assert.Equal(DecisionCardStatus.Success, outcome.Status);
        Assert.Equal(TaskStates.Completed, outcome.TargetState);

        var info = h.Scanner.FindJob(jobId, _watchPath)!;
        Assert.Equal(TaskStates.Completed, info.State);
        Assert.Equal(DecisionStatuses.Decided, info.Decision!.Status);
        Assert.Equal("b", info.Decision.ChosenOptionId);
        Assert.Equal("Team prefers a simpler tree.", info.Decision.Rationale);
        Assert.Equal("alice", info.Decision.DecidedBy);
        Assert.NotNull(info.Decision.DecidedAt);

        // ADR-style record exists and is linkable.
        Assert.Equal(DecisionCardService.RecordFileName, info.Decision.RecordPath);
        var recordPath = Path.Combine(info.FolderPath, DecisionCardService.RecordFileName);
        Assert.True(File.Exists(recordPath));
        var record = File.ReadAllText(recordPath);
        Assert.Contains("# Decision", record);
        Assert.Contains("No lock file", record);
        Assert.Contains("chosen", record);
        Assert.Contains("Team prefers a simpler tree.", record);

        // decision_decided timeline event.
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(info.FolderPath);
        Assert.Contains(timeline, e => e.Kind == TimelineEventKinds.DecisionDecided);
    }

    [Fact]
    public async Task Decide_UnknownOption_IsInvalidRequest_AndCardStaysOpen()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "reject-me", Title = "T", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(),
        })!;

        var outcome = await h.Decisions.DecideAsync(
            jobId, _watchPath, new DecideCardRequest { OptionId = "zzz", Rationale = "n/a" }, "alice");

        Assert.Equal(DecisionCardStatus.InvalidRequest, outcome.Status);
        var info = h.Scanner.FindJob(jobId, _watchPath)!;
        Assert.Equal(DecisionStatuses.Requested, info.Decision!.Status);
        Assert.Equal(TaskStates.Preparation, info.State);
    }

    [Fact]
    public async Task Decide_Twice_IsConflict()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "once", Title = "T", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(),
        })!;

        await h.Decisions.DecideAsync(jobId, _watchPath, new DecideCardRequest { OptionId = "a", Rationale = "r" }, "alice");
        var second = await h.Decisions.DecideAsync(jobId, _watchPath, new DecideCardRequest { OptionId = "b", Rationale = "r" }, "bob");
        Assert.Equal(DecisionCardStatus.Conflict, second.Status);
    }

    [Fact]
    public async Task Decide_UnblocksDependent_MovesItToReady_AndEnrichesPrompt()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "gate", Title = "Gate decision", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(),
        })!;
        var decisionKey = h.Scanner.FindJob(decisionId, _watchPath)!.Key!;

        var implId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "impl", Title = "Implement the contract", WatchPath = _watchPath,
            TargetState = TaskStates.Preparation, PromptMarkdown = "Do the work.",
        })!;
        h.Mutations.SetTaskReferences(implId, new TaskReferences { DependsOn = [new TaskDependencyReference(decisionKey)] }, _watchPath);

        var outcome = await h.Decisions.DecideAsync(
            decisionId, _watchPath, new DecideCardRequest { OptionId = "b", Rationale = "simpler" }, "alice");

        Assert.Equal(DecisionCardStatus.Success, outcome.Status);
        var impl = h.Scanner.FindJob(implId, _watchPath)!;
        Assert.Equal(TaskStates.Ready, impl.State);
        Assert.Contains(impl.Key!, outcome.UnblockedKeys!);

        var prompt = File.ReadAllText(Path.Combine(impl.FolderPath, "prompt.md"));
        Assert.Contains("Do the work.", prompt);
        Assert.Contains("Decision", prompt);
        Assert.Contains("No lock file", prompt);
    }

    [Fact]
    public async Task Decide_WithNoDependent_SeedsImplementationCardFromChosenOption()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "seed", Title = "Seed decision", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(),
        })!;

        // Option "b" carries Requirements, so deciding it seeds one 2-ready card.
        var outcome = await h.Decisions.DecideAsync(
            decisionId, _watchPath, new DecideCardRequest { OptionId = "b", Rationale = "simpler" }, "alice");

        Assert.Equal(DecisionCardStatus.Success, outcome.Status);
        Assert.Single(outcome.CreatedKeys!);
        var createdKey = outcome.CreatedKeys![0];
        var created = h.Scanner.ScanAllJobs().Single(j => j.Key == createdKey);
        Assert.Equal(TaskStates.Ready, created.State);
        var prompt = File.ReadAllText(Path.Combine(created.FolderPath, "prompt.md"));
        Assert.Contains("Remove the committed lock file", prompt);
        Assert.Contains("No lock file", prompt);
    }

    [Fact]
    public async Task Reopen_ClearsChoice_ReturnsToPreparation_AndEmitsTimeline()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "reopen", Title = "T", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(),
        })!;
        await h.Decisions.DecideAsync(jobId, _watchPath, new DecideCardRequest { OptionId = "a", Rationale = "r" }, "alice");

        var outcome = await h.Decisions.ReopenAsync(jobId, _watchPath, new ReopenDecisionRequest { Note = "Costs changed" }, "bob");

        Assert.Equal(DecisionCardStatus.Success, outcome.Status);
        var info = h.Scanner.FindJob(jobId, _watchPath)!;
        Assert.Equal(TaskStates.Preparation, info.State);
        Assert.Equal(DecisionStatuses.Reopened, info.Decision!.Status);
        Assert.Null(info.Decision.ChosenOptionId);
        Assert.Equal("Costs changed", info.Decision.ReopenNote);

        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(info.FolderPath);
        Assert.Contains(timeline, e => e.Kind == TimelineEventKinds.DecisionReopened);
    }

    [Fact]
    public async Task Reopen_OnOpenCard_IsConflict()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "still-open", Title = "T", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(),
        })!;
        var outcome = await h.Decisions.ReopenAsync(jobId, _watchPath, null, "bob");
        Assert.Equal(DecisionCardStatus.Conflict, outcome.Status);
    }

    [Fact]
    public async Task Decide_OnNonDecisionCard_IsRejected()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "plain", Title = "Plain task", WatchPath = _watchPath,
        })!;
        var outcome = await h.Decisions.DecideAsync(jobId, _watchPath, new DecideCardRequest { OptionId = "a", Rationale = "r" }, "alice");
        Assert.Equal(DecisionCardStatus.NotDecision, outcome.Status);
    }

    // ---- harness ----

    private sealed record Harness(
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        DecisionCardService Decisions);

    private Harness Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        states.EnsureStateFoldersAndMigrate();
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            new GitService(NullLogger<GitService>.Instance, scanner, config, prompts),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config),
            NullLogger<TaskTransitionService>.Instance);
        var decisions = new DecisionCardService(
            scanner, mutations, transitions, timeline, NullLogger<DecisionCardService>.Instance);
        return new Harness(scanner, mutations, decisions);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
