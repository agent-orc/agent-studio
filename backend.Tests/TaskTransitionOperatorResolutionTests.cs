using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

// The transition resolves its integration branch through real Git probes.
[Trait("Category", "MachineBound")]
public sealed class TaskTransitionOperatorResolutionTests : IDisposable
{
    private const string Reason = "The external toolchain configuration was corrected and verified.";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "operator-resolution-" + Guid.NewGuid().ToString("N"));
    private readonly TimeProvider _clock = new FixedTimeProvider();
    private readonly string _project;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly FailureInterventionService _interventions;
    private readonly CauseBreakerService _breaker;
    private readonly RecordingRelease _release = new();

    public TaskTransitionOperatorResolutionTests()
    {
        _project = Path.Combine(_root, "projects", "demo");
        Directory.CreateDirectory(_project);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = _project,
            ["WatchPaths:0:RootPath"] = _project,
            ["DeliveryChain:Guarded"] = "true",
        }).Build();
        _scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(_project, "demo", DefaultWorkspace.Id);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        _mutations = new TaskMutationService(_scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, timeline);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var authority = new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance,
            utcNow: () => _clock.GetUtcNow().UtcDateTime);
        _interventions = new FailureInterventionService(_mutations, _scanner, timeline,
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance),
            NullLogger<FailureInterventionService>.Instance, prompts, time: _clock);
        _breaker = new CauseBreakerService(config, settings, _interventions, _scanner, authority, timeline,
            _release, new FileCauseWaitMarkerStore(NullLogger<FileCauseWaitMarkerStore>.Instance),
            NullLogger<CauseBreakerService>.Instance, time: _clock);
        _transitions = new TaskTransitionService(_scanner,
            new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance), _mutations,
            new GitService(NullLogger<GitService>.Instance, _scanner, config), settings,
            NullLogger<TaskTransitionService>.Instance, timeline: timeline, attemptAuthority: authority,
            timeProvider: _clock, configuration: config, failureInterventions: _interventions);
    }

    [Theory]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    public async Task InterventionCompletesWithReasonAndSweepClosesItsBreaker(string source)
    {
        var (cause, origin) = OpenBreaker();
        if (source != cause.State)
        {
            var staged = await _transitions.MoveAsync(cause.Id, source, _project,
                CancellationToken.None, suppressProductExecution: true);
            Assert.Equal(MoveJobStatus.Success, staged.Status);
        }

        var result = await Complete(cause, Reason);

        Assert.Equal(MoveJobStatus.Success, result.Status);
        var completed = _scanner.FindJob(cause.Id, _project)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.Equal(CompletionClaimBases.DeliverableWithoutCode, completed.CompletionClaim!.Basis);
        Assert.Equal(Reason, completed.CompletionClaim.Evidence);
        Assert.Equal(Reason, completed.CompletionClaim.Reason);
        Assert.Equal("human:operator", completed.CompletionClaim.Actor);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, completed.CompletionClaim.RecordedAtUtc);
        Assert.Equal(1, _breaker.Sweep());
        var closed = Assert.Single(_breaker.List());
        Assert.False(closed.IsOpen);
        Assert.Equal("cause-integrated", closed.CloseReason);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, closed.ClosedAt);
        Assert.Equal(origin.Id, Assert.Single(_release.Tasks));
        Assert.Null(CauseWaitMarker.TryRead(origin.FolderPath));
    }

    [Fact]
    public async Task LegacyInterventionMetadataCompletesWithLedgerEvidenceAndClosesBreaker()
    {
        var (cause, origin) = OpenBreaker();
        RestoreLegacyMetadata(cause);
        var legacy = _scanner.FindJob(cause.Id, _project)!;
        Assert.False(legacy.NoBranchExpected);
        Assert.Null(legacy.RequiresIntegration);
        Assert.True(AcceptanceIntegrationPolicy.IsIntegrationRequired(legacy));

        var result = await Complete(legacy, Reason);

        Assert.Equal(MoveJobStatus.Success, result.Status);
        var completed = _scanner.FindJob(cause.Id, _project)!;
        Assert.Equal(CompletionClaimBases.DeliverableWithoutCode, completed.CompletionClaim!.Basis);
        Assert.Equal(Reason, completed.CompletionClaim.Evidence);
        Assert.Equal(1, _breaker.Sweep());
        Assert.Equal("cause-integrated", Assert.Single(_breaker.List()).CloseReason);
        Assert.Equal(origin.Id, Assert.Single(_release.Tasks));
    }

    [Fact]
    public void ExistingInterventionRaiseNormalizesLegacyMetadata()
    {
        var (cause, origin) = OpenBreaker();
        RestoreLegacyMetadata(cause);
        var fingerprint = CauseFingerprintPolicy.Compute("ReviewInfra", "PreparationFailed",
            "npm unavailable", 127, "tool:npm");
        var evidence = new FailureCommandEvidence("PreparationFailed", "ReviewInfra", 127, 0,
            "npm unavailable", "", OccurredAt: _clock.GetUtcNow().UtcDateTime);

        var outcome = _breaker.Observe(origin, origin.Key ?? origin.Id, "attempt-next",
            fingerprint, evidence, "npm unavailable");

        Assert.False(outcome.Intervention!.Created);
        Assert.False(_scanner.FindJob(cause.Id, _project)!.RequiresIntegration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("ok")]
    public async Task InterventionWithoutUsableReasonStaysOpen(string? reason)
    {
        var (cause, _) = OpenBreaker();
        Assert.Equal(MoveJobStatus.IntegrationFailed, (await Complete(cause, reason)).Status);
        Assert.Equal(TaskStates.Preparation, _scanner.FindJob(cause.Id, _project)!.State);
        Assert.Equal(0, _breaker.Sweep());
        Assert.True(Assert.Single(_breaker.List()).IsOpen);
    }

    [Theory]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.AutoReview)]
    public async Task CodeCardWithoutDeliveryIsRefusedWithMissingDeliveryMessage(string source)
    {
        var card = CreateCard(source);
        var result = await Complete(card, Reason);

        Assert.Equal(MoveJobStatus.IntegrationFailed, result.Status);
        Assert.Contains("no delivery to integrate", result.Message!);
        Assert.DoesNotContain("could not be determined", result.Message!);
        Assert.Equal(source, _scanner.FindJob(card.Id, _project)!.State);
        Assert.Null(_scanner.FindJob(card.Id, _project)!.CompletionClaim);
    }

    [Fact]
    public async Task InterventionWithAttributedCommitStillRequiresIntegration()
    {
        var (cause, _) = OpenBreaker();
        Assert.True(_mutations.SetJobCommit(cause.Id,
            new TaskCommitInfo { Sha = new string('a', 40) }, _project));

        Assert.Equal(MoveJobStatus.IntegrationFailed, (await Complete(cause, Reason)).Status);
        Assert.Equal(0, _breaker.Sweep());
        Assert.True(Assert.Single(_breaker.List()).IsOpen);
    }

    private Task<MoveJobOutcome> Complete(TaskInfo card, string? reason)
        => _transitions.MoveAsync(card.Id, TaskStates.Completed, _project, CancellationToken.None,
            cause: "human:operator", reason: reason, operatorOverride: true);

    private (TaskInfo Cause, TaskInfo Origin) OpenBreaker()
    {
        var origin = CreateCard(TaskStates.AutoReview);
        var fingerprint = CauseFingerprintPolicy.Compute("ReviewInfra", "PreparationFailed",
            "npm unavailable", 127, "tool:npm");
        var evidence = new FailureCommandEvidence("PreparationFailed", "ReviewInfra", 127, 0,
            "npm unavailable", "", OccurredAt: _clock.GetUtcNow().UtcDateTime);
        for (var i = 0; i < 3; i++)
            _breaker.Observe(origin, origin.Key ?? origin.Id, "attempt-" + i, fingerprint, evidence, "npm unavailable");
        var opened = Assert.Single(_breaker.List(openOnly: true));
        return (_scanner.FindJob(opened.CauseTaskId!, _project)!, origin);
    }

    private TaskInfo CreateCard(string state)
    {
        var id = _mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Code delivery", WatchPath = _project, TargetState = state,
            PromptMarkdown = "Implement the requested code change.",
        });
        return _scanner.FindJob(id!, _project)!;
    }

    private static void RestoreLegacyMetadata(TaskInfo cause)
    {
        Assert.True(TaskJsonFile.RemoveField(cause.FolderPath, "noBranchExpected",
            NullLogger<TaskMutationService>.Instance));
        Assert.True(TaskJsonFile.RemoveField(cause.FolderPath, "requiresIntegration",
            NullLogger<TaskMutationService>.Instance));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingRelease : ICauseWaitRelease
    {
        public List<string> Tasks { get; } = [];
        public string? Release(TaskInfo task, string deliveryKey)
        {
            Tasks.Add(task.Id);
            return "review-after-resolution";
        }
    }
}
