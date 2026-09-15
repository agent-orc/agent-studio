using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2824 - the rail that closes the gap the 15.09.2026 incident opened:
/// AGT-2811/2812/2813 passed remote review, the Windows merge gate died with a
/// gate environment failure, and nothing retried them for over an hour because
/// the accepted-integration backstop only covers accepted cards. These tests
/// pin the automatic retry, the reuse of the passed review, the stop after the
/// bound, and the operator action that shares all of it.
/// </summary>
public sealed class GateEnvironmentIntegrationRetryServiceTests : IDisposable
{
    private const string Project = "proj";
    private const string DeliverySha = "1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset FailedAt = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agt2824-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;
    private readonly List<RemoteDeliveryIntegrationRequest> _requests = [];
    private readonly PipelineExecutionLog _pipeline = new(NullLogger<PipelineExecutionLog>.Instance);
    private readonly TimelineLog _timeline = new(NullLogger<TimelineLog>.Instance);

    public GateEnvironmentIntegrationRetryServiceTests()
    {
        _watchPath = Path.Combine(_root, "watch");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp cleanup is best effort */ }
    }

    // --- automatic retry ---

    [Fact]
    public async Task Advance_WhenTheWindowIsOpen_ReplaysTheIntegrationForTheReviewedDelivery()
    {
        var job = SeedGateEnvironmentFailure();
        var service = BuildService(MergeIntoIntegrationOutcome.GateEnvironmentFailure);

        var result = await service.AdvanceAsync(job, FailedAt.AddMinutes(5));

        Assert.True(result.Retried);
        Assert.Equal(1, result.Attempt);
        var request = Assert.Single(_requests);
        Assert.Equal(job.Id, request.JobId);
        Assert.Equal("backstop 1/3", request.RetryAttempt);
        // The replay must not reuse the original delivery timestamp: the
        // coordinator coalesces identical delivery keys and would hand back the
        // very failure this retry exists to redo.
        Assert.Equal(FailedAt.AddMinutes(5), request.DeliveredAtUtc);
        Assert.Equal(1, ReadLedger(job)!.Attempts);
        Assert.Equal(IntegrationRetryLedger.BackstopTrigger, ReadLedger(job)!.LastAttemptTrigger);
    }

    [Fact]
    public async Task Advance_BeforeTheWindowOpens_DoesNotTouchTheIntegration()
    {
        var job = SeedGateEnvironmentFailure();
        var service = BuildService(MergeIntoIntegrationOutcome.GateEnvironmentFailure);

        var result = await service.AdvanceAsync(job, FailedAt.AddMinutes(4));

        Assert.False(result.Retried);
        Assert.Equal(GateEnvironmentRetryAction.Wait, result.Decision.Action);
        Assert.Empty(_requests);
        Assert.Null(ReadLedger(job));
    }

    [Fact]
    public async Task Advance_NeverStartsANewReview_AndReusesThePassedVerdictForTheSameSha()
    {
        // The whole point of the card: the only operator path used to be
        // /move 4-auto-review, a full 30+ minute remote review of work that had
        // already passed. The rail must consume the existing verdict instead.
        var job = SeedGateEnvironmentFailure();
        var reviewLookups = 0;
        var service = BuildService(
            MergeIntoIntegrationOutcome.GateEnvironmentFailure,
            review: _ =>
            {
                reviewLookups++;
                return new GateEnvironmentIntegrationRetryService.ReviewGrade(true, DeliverySha);
            });

        await service.AdvanceAsync(job, FailedAt.AddMinutes(5));

        Assert.Equal(1, reviewLookups);
        Assert.Equal(DeliverySha, ReviewSubjectStore.Read(job.FolderPath)!.ResultSha);
        // The card stays where it was; no lane move back into review happened.
        Assert.Equal(TaskStates.HumanReview, job.State);
    }

    [Fact]
    public async Task Advance_WithoutAPassedReview_NeverIntegrates()
    {
        var job = SeedGateEnvironmentFailure();
        var service = BuildService(
            MergeIntoIntegrationOutcome.GateEnvironmentFailure,
            review: _ => new GateEnvironmentIntegrationRetryService.ReviewGrade(false, DeliverySha));

        var result = await service.AdvanceAsync(job, FailedAt.AddHours(2));

        Assert.False(result.Retried);
        Assert.Equal(GateEnvironmentRetryAction.Ignore, result.Decision.Action);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Advance_SuccessfulRetry_ClosesTheBudget()
    {
        var job = SeedGateEnvironmentFailure();
        var service = BuildService(MergeIntoIntegrationOutcome.Merged);

        var result = await service.AdvanceAsync(job, FailedAt.AddMinutes(5));

        Assert.True(result.Integrated);
        Assert.Null(ReadLedger(job));
    }

    [Fact]
    public async Task Advance_DecidedNonEnvironmentOutcome_HandsTheCardToTheNormalRails()
    {
        // A conflict is a real, delivery-owned failure. Keeping the environment
        // budget open on it would spend two more merges on a card that needs a
        // rebase round.
        var job = SeedGateEnvironmentFailure();
        var service = BuildService(MergeIntoIntegrationOutcome.Conflict);

        var result = await service.AdvanceAsync(job, FailedAt.AddMinutes(5));

        Assert.Equal(MergeIntoIntegrationOutcome.Conflict, result.Outcome);
        Assert.Null(ReadLedger(job));
    }

    // --- the bound ---

    [Fact]
    public async Task Advance_StopsAfterThreeAttemptsAndParksWithANamedReason()
    {
        var job = SeedGateEnvironmentFailure();
        var service = BuildService(MergeIntoIntegrationOutcome.GateEnvironmentFailure);

        var first = await service.AdvanceAsync(job, FailedAt.AddMinutes(5));
        var second = await service.AdvanceAsync(job, FailedAt.AddMinutes(5 + 15));
        var third = await service.AdvanceAsync(job, FailedAt.AddMinutes(5 + 15 + 45));
        var fourth = await service.AdvanceAsync(job, FailedAt.AddHours(6));
        var fifth = await service.AdvanceAsync(job, FailedAt.AddDays(1));

        Assert.Equal([1, 2, 3], new[] { first.Attempt, second.Attempt, third.Attempt });
        Assert.Equal(3, _requests.Count);
        Assert.Equal(["backstop 1/3", "backstop 2/3", "backstop 3/3"], _requests.Select(r => r.RetryAttempt));

        Assert.Equal(GateEnvironmentRetryAction.Park, fourth.Decision.Action);
        Assert.False(fourth.Retried);
        // The park is recorded once; every later sweep is a no-op.
        Assert.Equal(GateEnvironmentRetryAction.Ignore, fifth.Decision.Action);
        Assert.Equal(3, _requests.Count);

        var ledger = ReadLedger(job)!;
        Assert.True(ledger.Parked);
        Assert.Equal(3, ledger.Attempts);
        Assert.Contains("gate environment failure", ledger.ParkedReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry integration", ledger.ParkedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advance_Park_WritesAnOperatorVisibleTimelineRow()
    {
        var job = SeedGateEnvironmentFailure(attempts: 3, lastAttemptAt: FailedAt.AddMinutes(65));
        var service = BuildService(MergeIntoIntegrationOutcome.GateEnvironmentFailure);

        await service.AdvanceAsync(job, FailedAt.AddHours(6));

        var parkRow = _timeline.ReadAll(job.FolderPath)
            .Single(row => row.Kind == TimelineEventKinds.IntegrationFailed);
        Assert.Equal(
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            parkRow.Details!["outcome"]);
        Assert.Equal("parked 3/3", parkRow.Details["retryAttempt"]);
        Assert.Contains("passed review still stands", parkRow.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // --- operator action ---

    [Fact]
    public async Task OperatorRetry_OnAParkedCard_IntegratesImmediatelyAndGrantsAFreshWindow()
    {
        var job = SeedGateEnvironmentFailure(attempts: 3, lastAttemptAt: FailedAt.AddMinutes(65), parked: true);
        var service = BuildService(MergeIntoIntegrationOutcome.GateEnvironmentFailure);

        var result = await service.RetryOnOperatorRequestAsync(job, FailedAt.AddHours(3));

        Assert.True(result.Retried);
        Assert.Equal(1, result.Attempt);
        Assert.Equal("operator 1/3", Assert.Single(_requests).RetryAttempt);
        var ledger = ReadLedger(job)!;
        Assert.False(ledger.Parked);
        Assert.Equal(1, ledger.Attempts);
        Assert.Equal(IntegrationRetryLedger.OperatorTrigger, ledger.LastAttemptTrigger);
    }

    [Fact]
    public async Task OperatorRetry_IgnoresTheBackoffWindow()
    {
        var job = SeedGateEnvironmentFailure();
        var service = BuildService(MergeIntoIntegrationOutcome.Merged);

        var result = await service.RetryOnOperatorRequestAsync(job, FailedAt.AddSeconds(1));

        Assert.True(result.Integrated);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task OperatorRetry_OnADifferentFailure_IsRefusedWithTheReason()
    {
        var job = SeedGateEnvironmentFailure(failureVerdict: "conflict");
        var service = BuildService(MergeIntoIntegrationOutcome.Merged);

        var result = await service.RetryOnOperatorRequestAsync(job, FailedAt.AddHours(1));

        Assert.False(result.Retried);
        Assert.Equal(GateEnvironmentRetryAction.Ignore, result.Decision.Action);
        Assert.Contains("gate environment failure", result.Decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_requests);
    }

    // --- sweep ---

    [Fact]
    public async Task Sweep_DrivesOnlyTheHumanReviewCardsThatAreDue()
    {
        var due = SeedGateEnvironmentFailure(slug: "due");
        SeedGateEnvironmentFailure(slug: "not-due", failedAt: FailedAt.AddMinutes(4));
        SeedGateEnvironmentFailure(slug: "auto-review", state: TaskStates.AutoReview);
        var service = BuildService(MergeIntoIntegrationOutcome.GateEnvironmentFailure);
        var sweep = new GateEnvironmentIntegrationRetryHostedService(
            BuildScanner(),
            service,
            EmptyConfig(),
            NullLogger<GateEnvironmentIntegrationRetryHostedService>.Instance);

        var retried = await sweep.RunOnceAsync(FailedAt.AddMinutes(5));

        Assert.Equal(1, retried);
        Assert.Equal(due.Id, Assert.Single(_requests).JobId);
    }

    // --- fixture ---

    private GateEnvironmentIntegrationRetryService BuildService(
        MergeIntoIntegrationOutcome outcome,
        Func<string, GateEnvironmentIntegrationRetryService.ReviewGrade>? review = null)
    {
        var settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance,
            EmptyConfig());
        settings.SetIntegrationBranch(Project, "develop");
        return new GateEnvironmentIntegrationRetryService(
            BuildScanner(),
            settings,
            _pipeline,
            _timeline,
            request =>
            {
                _requests.Add(request);
                RecordMergeStep(
                    request.JobFolderPath,
                    outcome == MergeIntoIntegrationOutcome.GateEnvironmentFailure
                        ? "gate-environment-failure"
                        : outcome.ToString().ToLowerInvariant());
                return Task.FromResult(MergeIntoIntegrationResult.Of(
                    outcome,
                    mergedSha: outcome.IsSuccessfulIntegration() ? DeliverySha : null,
                    error: outcome.IsSuccessfulIntegration() ? null : "gate host is broken"));
            },
            review ?? (_ => new GateEnvironmentIntegrationRetryService.ReviewGrade(true, DeliverySha)),
            NullLogger<GateEnvironmentIntegrationRetryService>.Instance);
    }

    private TaskScannerService BuildScanner()
    {
        var config = EmptyConfig();
        return new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
    }

    private IConfiguration EmptyConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskRepository"] = _root,
            })
            .Build();

    private TaskInfo SeedGateEnvironmentFailure(
        string slug = "card",
        string state = TaskStates.HumanReview,
        string failureVerdict = "gate-environment-failure",
        DateTimeOffset? failedAt = null,
        int attempts = 0,
        DateTimeOffset? lastAttemptAt = null,
        bool parked = false)
    {
        var folder = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = slug,
                ["title"] = "Gate environment card",
                ["state"] = state,
                ["order"] = 1,
                ["agent"] = "claude",
            }));

        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = slug,
            RunAttemptId = "run-" + slug,
            AttemptChainId = "chain-" + slug,
            Project = Project,
            Repository = "repo",
            ResultSha = DeliverySha,
            CompletedAtUtc = FailedAt.AddMinutes(-10),
        });

        _pipeline.EnsureRun(folder, PipelineCatalogue.Standard, Project, slug);
        RecordMergeStep(folder, failureVerdict, failedAt ?? FailedAt);

        if (attempts > 0 || parked)
        {
            IntegrationRetryLedger.Write(folder, new IntegrationRetryLedgerRecord
            {
                FailureCode = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
                DeliverySha = DeliverySha,
                Attempts = attempts,
                LastAttemptAtUtc = lastAttemptAt,
                LastAttemptTrigger = IntegrationRetryLedger.BackstopTrigger,
                Parked = parked,
                ParkedAtUtc = parked ? lastAttemptAt : null,
                ParkedReason = parked ? GateEnvironmentRetryPolicy.ParkedReason(null) : null,
            });
        }

        return new TaskInfo
        {
            Id = slug,
            Key = slug,
            TaskKey = slug,
            State = state,
            ProjectName = Project,
            WatchPath = _watchPath,
            FolderPath = folder,
        };
    }

    private void RecordMergeStep(string folder, string verdict, DateTimeOffset? at = null)
    {
        var stamp = (at ?? FailedAt).UtcDateTime;
        _pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = verdict is "merged" or "already-merged"
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
            StartedAt = stamp,
            CompletedAt = stamp,
            Verdict = verdict,
            Reason = "The build gate blocked the merge into develop: "
                     + "Tool 'node' version v24.18.0 does not match .nvmrc.",
            FailureCode = verdict == "gate-environment-failure"
                ? AcceptedIntegrationFailureCodes.GateEnvironmentFailure
                : null,
        });
    }

    private static IntegrationRetryLedgerRecord? ReadLedger(TaskInfo job)
        => IntegrationRetryLedger.Read(job.FolderPath);
}
