using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Pipeline;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2824 - the rail around <see cref="GateEnvironmentRetryPolicy"/>.
///
/// <para>Observed on 15.09.2026: AGT-2811/2812/2813 passed remote review, the
/// Windows merge gate failed with a gate environment failure, the card said
/// "will be retried", and nothing retried for over an hour. The accepted
/// integration backstop only sweeps accepted cards, so the only operator path
/// was a complete new remote review on a scarce review slot.</para>
///
/// <para>Every test drives the real timeline receipts and the real
/// <c>pipeline-execution.json</c> over a throwaway task folder, so the retry
/// budget, its reuse of the passed review, and the parked reason are proven
/// against the same durable evidence the running system reads.</para>
/// </summary>
public sealed class GateEnvironmentRetryServiceTests : IDisposable
{
    private const string DeliverySha = "1111111111111111111111111111111111111111";
    private const string GateReason =
        "The build gate blocked the merge into develop: Tool 'node' version v24.18.0 does not match .nvmrc. "
        + "develop was rolled back to abc1234 and nothing was pushed; "
        + "gate environment: the build/test gate failed before verification could run and will be retried.";

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly PipelineExecutionLog _pipeline = new(NullLogger<PipelineExecutionLog>.Instance);
    private readonly TimelineLog _timeline = new(NullLogger<TimelineLog>.Instance);

    public GateEnvironmentRetryServiceTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "gate-environment-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "best-effort temp cleanup"); }
    }

    [Fact]
    public async Task Sweep_HealedGate_ReplaysTheMergeAndReusesThePassedReview()
    {
        var job = SeedGateEnvironmentFailure("healed", failedAt: Now.AddMinutes(-10));
        var merges = new List<string>();
        var service = Build(
            job,
            reviewedSha: DeliverySha,
            integrate: task =>
            {
                // The merge must run against the delivery the passed review
                // describes; nothing re-materializes a subject for it.
                merges.Add(ReviewSubjectStore.Read(task.FolderPath)!.ResultSha);
                return MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Merged,
                    mergedSha: "deadbee");
            });

        var summary = await service.RunOnceAsync(Now);

        Assert.Equal(1, summary.Retried);
        Assert.Equal(1, summary.Integrated);
        Assert.Equal([DeliverySha], merges);

        var receipt = Assert.Single(Receipts(job, TimelineEventKinds.IntegrationRetryAttempted));
        Assert.Equal("1", receipt.Details!["attempt"]);
        Assert.Equal("3", receipt.Details["maxAttempts"]);
        Assert.Equal(DeliverySha, receipt.Details["deliverySha"]);
        Assert.Equal("true", receipt.Details["reviewReused"]);
        Assert.Equal(GateEnvironmentRetryReceipts.SweepSource, receipt.Details["source"]);
        // The card is never moved back into review: the retry costs no review slot.
        Assert.Equal(TaskStates.HumanReview, job.State);
        Assert.Empty(Receipts(job, TimelineEventKinds.IntegrationRetryExhausted));
    }

    [Fact]
    public async Task Sweep_BeforeTheFirstBackoffStep_DoesNotReplayTheMerge()
    {
        var job = SeedGateEnvironmentFailure("too-early", failedAt: Now.AddMinutes(-2));
        var merges = 0;
        var service = Build(job, DeliverySha, _ =>
        {
            merges++;
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
        });

        var summary = await service.RunOnceAsync(Now);

        Assert.Equal(0, summary.Retried);
        Assert.Equal(1, summary.Waiting);
        Assert.Equal(0, merges);
        Assert.Empty(Receipts(job, TimelineEventKinds.IntegrationRetryAttempted));
    }

    [Fact]
    public async Task Sweep_WhenTheLatestReviewDidNotPass_NeverReplaysTheMerge()
    {
        var job = SeedGateEnvironmentFailure("unreviewed", failedAt: Now.AddMinutes(-10));
        var merges = 0;
        var service = Build(job, reviewedSha: null, reviewPassed: false, integrate: _ =>
        {
            merges++;
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
        });

        var summary = await service.RunOnceAsync(Now);

        Assert.Equal(0, summary.Candidates);
        Assert.Equal(0, merges);
    }

    [Fact]
    public async Task Sweep_WhenThePassedReviewDescribesAnotherDelivery_NeverReplaysTheMerge()
    {
        var job = SeedGateEnvironmentFailure("superseded", failedAt: Now.AddMinutes(-10));
        var merges = 0;
        var service = Build(
            job,
            reviewedSha: "2222222222222222222222222222222222222222",
            integrate: _ =>
            {
                merges++;
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
            });

        Assert.Equal(0, (await service.RunOnceAsync(Now)).Candidates);
        Assert.Equal(0, merges);
    }

    [Fact]
    public async Task Sweep_StopsAfterThreeRetriesAndParksWithANamedReason()
    {
        var job = SeedGateEnvironmentFailure("stubborn", failedAt: Now.AddMinutes(-10));
        var merges = 0;
        var service = Build(job, DeliverySha, _ =>
        {
            merges++;
            // The gate host stays broken for the whole ladder.
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.GateEnvironmentFailure,
                error: GateReason);
        });

        // Five sweeps spread well past the 5/15/45-minute ladder: three retries,
        // one park, then nothing.
        var clock = Now;
        var parked = 0;
        for (var tick = 0; tick < 5; tick++)
        {
            parked += (await service.RunOnceAsync(clock)).Parked;
            clock = clock.AddHours(1);
        }

        Assert.Equal(GateEnvironmentRetryPolicy.MaxRetries, merges);
        Assert.Equal(3, Receipts(job, TimelineEventKinds.IntegrationRetryAttempted).Count);
        Assert.Equal(1, parked);

        var exhausted = Assert.Single(Receipts(job, TimelineEventKinds.IntegrationRetryExhausted));
        Assert.Equal("3", exhausted.Details!["attempts"]);
        Assert.Equal(
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            exhausted.Details["failureCode"]);

        // The card's own durable surfaces stop promising a retry and name the
        // environment failure instead.
        var step = _pipeline.Read(job.FolderPath)!.Steps
            .Last(entry => entry.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal(AcceptedIntegrationFailureCodes.GateEnvironmentFailure, step.FailureCode);
        Assert.Contains("Gate environment failure", step.Reason);
        Assert.Contains("5 min, 15 min, 45 min", step.Reason);
        Assert.Contains("does not match .nvmrc", step.Reason);

        var status = File.ReadAllText(Path.Combine(job.FolderPath, "status.md"));
        Assert.Contains("Acceptance integration", status);
        Assert.Contains("GateEnvironmentFailure", status);
        Assert.Contains("Retry integration", status);
    }

    [Fact]
    public async Task RetryNow_SkipsTheRemainingBackoffAndStillReusesThePassedReview()
    {
        var job = SeedGateEnvironmentFailure("operator", failedAt: Now.AddMinutes(-1));
        var merges = 0;
        var service = Build(job, DeliverySha, _ =>
        {
            merges++;
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
        });

        var result = await service.RetryNowAsync(job);

        Assert.True(result.Retried);
        Assert.True(result.Integrated);
        Assert.Equal(1, result.Attempt);
        Assert.Equal(1, merges);
        var receipt = Assert.Single(Receipts(job, TimelineEventKinds.IntegrationRetryAttempted));
        Assert.Equal(GateEnvironmentRetryReceipts.OperatorSource, receipt.Details!["source"]);
        Assert.Equal("true", receipt.Details["reviewReused"]);
    }

    [Fact]
    public async Task RetryNow_WhileASweepReplayIsRunning_DoesNotSpendASecondRung()
    {
        // An impatient second click must not cost a second rung of the ladder
        // for one physical replay.
        var job = SeedGateEnvironmentFailure("double-click", failedAt: Now.AddMinutes(-10));
        var merges = 0;
        var release = new TaskCompletionSource();
        var service = Build(job, DeliverySha, _ =>
        {
            merges++;
            release.Task.GetAwaiter().GetResult();
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
        });

        var first = Task.Run(() => service.RetryNowAsync(job));
        while (Volatile.Read(ref merges) == 0) await Task.Delay(5);
        var second = await service.RetryNowAsync(job);
        release.SetResult();

        Assert.True((await first).Retried);
        Assert.False(second.Retried);
        Assert.Contains("already running", second.Reason);
        Assert.Equal(1, merges);
        Assert.Single(Receipts(job, TimelineEventKinds.IntegrationRetryAttempted));
    }

    [Fact]
    public async Task RetryNow_OnANonEnvironmentFailure_IsRefusedWithAReason()
    {
        var job = SeedGateEnvironmentFailure(
            "conflicted",
            failedAt: Now.AddMinutes(-10),
            failureCode: AcceptedIntegrationFailureCodes.MergeConflict,
            verdict: "conflict");
        var merges = 0;
        var service = Build(job, DeliverySha, _ =>
        {
            merges++;
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
        });

        var result = await service.RetryNowAsync(job);

        Assert.False(result.Retried);
        Assert.Equal(0, merges);
        Assert.Contains("not a gate environment failure", result.Reason);
    }

    [Fact]
    public async Task Sweep_AfterTheDeliveryIsIntegrated_LeavesTheCardAlone()
    {
        var job = SeedGateEnvironmentFailure("landed", failedAt: Now.AddMinutes(-10));
        var merges = 0;
        var service = Build(
            job,
            DeliverySha,
            _ =>
            {
                merges++;
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
            },
            alreadyIntegrated: true);

        Assert.Equal(0, (await service.RunOnceAsync(Now)).Candidates);
        Assert.Equal(0, merges);
    }

    private GateEnvironmentRetryService Build(
        TaskInfo job,
        string? reviewedSha,
        Func<TaskInfo, MergeIntoIntegrationResult> integrate,
        bool reviewPassed = true,
        bool alreadyIntegrated = false)
        => new(
            new GateEnvironmentRetryHooks(
                () => [job],
                _ => alreadyIntegrated,
                _ => (reviewPassed, reviewedSha),
                _ => "develop",
                (task, _) => Task.FromResult(integrate(task))),
            _pipeline,
            _timeline,
            NullLogger<GateEnvironmentRetryService>.Instance);

    private TaskInfo SeedGateEnvironmentFailure(
        string id,
        DateTimeOffset failedAt,
        string failureCode = AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
        string verdict = "gate-environment-failure")
    {
        var folder = Path.Combine(_tempDir, "jobs", id);
        Directory.CreateDirectory(folder);
        var job = new TaskInfo
        {
            Id = id,
            Key = "AGT-" + id,
            TaskKey = _tempDir + "::" + id,
            State = TaskStates.HumanReview,
            ProjectName = "Fixture",
            WatchPath = _tempDir,
            FolderPath = folder,
        };

        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = job.TaskKey,
            RunAttemptId = "run-1",
            AttemptChainId = "chain-1",
            Project = job.ProjectName,
            Repository = "fixture",
            ResultSha = DeliverySha,
            Executor = "executor-1",
            LeaseId = "lease-1",
            FencingToken = 1,
            IntegrationBranch = "develop",
            CompletedAtUtc = failedAt.AddMinutes(-5),
        });

        _pipeline.EnsureRun(folder, PipelineCatalogue.Standard, job.ProjectName, job.Id);
        _pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = failedAt.UtcDateTime.AddMinutes(-3),
            CompletedAt = failedAt.UtcDateTime,
            Verdict = verdict,
            Reason = GateReason,
            FailureCode = failureCode,
        });
        return job;
    }

    private List<TimelineEvent> Receipts(TaskInfo job, string kind)
        => _timeline.ReadAll(job.FolderPath).Where(entry => entry.Kind == kind).ToList();
}
