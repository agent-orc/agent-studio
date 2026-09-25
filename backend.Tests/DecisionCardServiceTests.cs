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
            new DecisionOption { Id = "a", Label = "Lock file", Consequences = "Reproducible installs", Effort = "S", Risk = "Lockfile churn" },
            new DecisionOption { Id = "b", Label = "No lock file", Consequences = "Simpler tree", Effort = "S", Risk = "Version drift" },
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
        Assert.Equal(DecisionStatuses.Pending, info.Decision!.Status);
        Assert.Equal(2, info.Decision.Options.Count);
        Assert.Equal("a", info.Decision.RecommendedOptionId);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(info.FolderPath);
        Assert.Contains(timeline, e => e.Kind == TimelineEventKinds.DecisionRequested);
        Assert.Contains(h.ActivityFeed.Read(_watchPath), e => e.Summary.StartsWith("Decision requested:"));
        var refused = h.States.MoveJob(jobId!, TaskStates.Ready, _watchPath);
        Assert.Equal(MoveJobStatus.Failure, refused.Status);
        Assert.Contains("Decision cards", refused.Message);
        Assert.Equal(MoveJobStatus.Failure,
            h.States.MoveJob(jobId!, TaskStates.Completed, _watchPath).Status);
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

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void CreateDecisionCard_RejectsOptionCountOutsideTwoToFour(int count)
    {
        var h = Build();
        var content = SampleContent() with
        {
            Options = Enumerable.Range(0, count)
                .Select(i => new DecisionOption { Id = $"o{i}", Label = $"Option {i}" }).ToList(),
            RecommendedOptionId = null,
        };
        Assert.Null(h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "bad-option-count", Title = "Bad options", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = content,
        }));
    }

    [Fact]
    public void CreateDecisionCard_WithRunnerTargetOrUnknownKind_IsRejected()
    {
        var h = Build();
        Assert.Null(h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "runner-decision", Title = "Invalid lane", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(), TargetState = TaskStates.Ready,
        }));
        Assert.Null(h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "unknown-kind", Title = "Invalid kind", WatchPath = _watchPath,
            Kind = "invented",
        }));
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
        Assert.Equal($"operations/decisions/{info.Key}.md", info.Decision.RecordPath);
        var recordPath = Path.Combine(_watchPath, "docs", info.Decision.RecordPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(recordPath));
        var record = File.ReadAllText(recordPath);
        Assert.Contains("# Decision", record);
        Assert.Contains("No lock file", record);
        Assert.Contains("Chosen option", record);
        Assert.Contains("Team prefers a simpler tree.", record);
        Assert.Contains("\"confirmedBy\": \"alice\"", record);
        Assert.Contains("\"selectedOptionIds\"", record);

        // decision_decided timeline event.
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(info.FolderPath);
        Assert.Contains(timeline, e => e.Kind == TimelineEventKinds.DecisionDecided);
        Assert.Contains(h.ActivityFeed.Read(_watchPath), e => e.Summary.StartsWith("Decision decided:"));
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
        Assert.Equal(DecisionStatuses.Pending, info.Decision!.Status);
        Assert.Equal(TaskStates.Preparation, info.State);
    }

    [Fact]
    public async Task Decide_RefusesAnotherNamedClient()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "named-decider", Title = "Named decider", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent() with { Decider = "alice" },
        })!;
        var outcome = await h.Decisions.DecideAsync(jobId, _watchPath,
            new DecideCardRequest { OptionId = "a" }, "bob");
        Assert.Equal(DecisionCardStatus.Forbidden, outcome.Status);
        Assert.Equal(DecisionStatuses.Pending, h.Scanner.FindJob(jobId, _watchPath)!.Decision!.Status);
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
    public async Task PendingDecision_BlocksRunnerLane_AndReopenReblocks()
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
            TargetState = TaskStates.Preparation,
        })!;
        h.Mutations.SetTaskReferences(implId,
            new TaskReferences { DependsOn = [new TaskDependencyReference(decisionKey)] }, _watchPath);
        var pending = h.Scanner.FindJob(implId, _watchPath)!;
        var waits = h.Scanner.GetReferenceIndex().EvaluateWaitsOn(pending);
        Assert.True(waits.Blocked);
        Assert.True(Assert.Single(waits.Items).PendingDecision);
        var refused = h.States.MoveJob(implId, TaskStates.Ready, _watchPath);
        Assert.Equal(MoveJobStatus.Failure, refused.Status);
        Assert.Contains(decisionKey, refused.Message);

        var decided = await h.Decisions.DecideAsync(decisionId, _watchPath,
            new DecideCardRequest { OptionId = "a", Rationale = "Reproducibility" }, "alice");
        Assert.Equal(DecisionCardStatus.Success, decided.Status);
        Assert.Equal(TaskStates.Preparation, h.Scanner.FindJob(implId, _watchPath)!.State);
        Assert.Equal(MoveJobStatus.Success, h.States.MoveJob(implId, TaskStates.Ready, _watchPath).Status);
        Assert.Equal(DecisionCardStatus.Success, (await h.Decisions.ReopenAsync(decisionId,
            _watchPath, new ReopenDecisionRequest { Note = "Revisit" }, "bob")).Status);
        Assert.True(h.Scanner.GetReferenceIndex().EvaluateWaitsOn(h.Scanner.FindJob(implId, _watchPath)!).Blocked);
        Assert.Equal(MoveJobStatus.Failure, h.States.MoveJob(implId, TaskStates.Progress, _watchPath).Status);
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
        Assert.Equal(DecisionStatuses.Pending, info.Decision!.Status);
        Assert.Null(info.Decision.ChosenOptionId);
        Assert.Equal("Costs changed", info.Decision.ReopenNote);
        Assert.Equal(2, info.Decision.History.Count);
        var recordPath = Path.Combine(_watchPath, "docs", info.Decision.RecordPath!.Replace('/', Path.DirectorySeparatorChar));
        var record = File.ReadAllText(recordPath);
        Assert.Contains("Chosen option: a", record);
        Assert.Contains("Costs changed", record);
        Assert.Contains("\"action\": \"reopened\"", record);

        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(info.FolderPath);
        Assert.Contains(timeline, e => e.Kind == TimelineEventKinds.DecisionReopened);
        Assert.Contains(h.ActivityFeed.Read(_watchPath), e => e.Summary.StartsWith("Decision reopened:"));
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
    public async Task Reopen_WithoutNote_IsInvalidRequest()
    {
        var h = Build();
        var jobId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Id = "note-required", Title = "Note required", WatchPath = _watchPath,
            Kind = TaskKinds.Decision, Decision = SampleContent(),
        })!;
        await h.Decisions.DecideAsync(jobId, _watchPath,
            new DecideCardRequest { OptionId = "a" }, "alice");
        var outcome = await h.Decisions.ReopenAsync(jobId, _watchPath,
            new ReopenDecisionRequest(), "alice");
        Assert.Equal(DecisionCardStatus.InvalidRequest, outcome.Status);
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
        DecisionCardService Decisions,
        TaskStateMachine States,
        OrchestratorLog ActivityFeed);

    private Harness Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        states.EnsureStateFoldersAndMigrate();
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var activityFeed = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline, activityFeed: activityFeed);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            new GitService(NullLogger<GitService>.Instance, scanner, config, prompts),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config),
            NullLogger<TaskTransitionService>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance);
        var records = new DecisionRecordService(docs);
        var decisions = new DecisionCardService(
            scanner, mutations, transitions, timeline, NullLogger<DecisionCardService>.Instance, records, activityFeed);
        return new Harness(scanner, mutations, decisions, states, activityFeed);
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
