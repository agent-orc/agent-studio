using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Cycle-time aggregation: stage attribution per task from the ledger and the
/// pipeline record, robustness to partial data, percentile statistics, window
/// filtering, and the file-backed service path.
/// </summary>
public sealed class ProjectCycleTimeServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-cycle-time-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void GateLogAndBatchFacts_InvalidateOnlyTheAffectedTaskMemo()
    {
        var watchPath = Path.Combine(_root, "memo-tasks");
        var completed = T0.AddMinutes(90);
        SeedJob(watchPath, TaskStates.Completed, "memo-card", T0, completed,
        [
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(10), TaskStates.Ready, TaskStates.Progress),
            Lane(T0.AddMinutes(20), TaskStates.Progress, TaskStates.AutoReview),
            new TimelineEvent { Ts = T0.AddMinutes(25), Kind = TimelineEventKinds.ReviewAttemptClaimed,
                RunId = "review_memo", Details = new() { ["attemptId"] = "review_memo" } },
            PostStep(TimelineEventKinds.PostStepFinished, T0.AddMinutes(30),
                "aspect-code-quality", "aspect-code-quality", "review_memo", 60_000),
            Lane(completed, TaskStates.AutoReview, TaskStates.Completed),
        ], null);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var batch = new BatchGateStore(Path.Combine(_root, "batch-gates"));
        var gateReads = 0;
        var batchReads = 0;
        var service = new ProjectCycleTimeService(scanner,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<ProjectCycleTimeService>.Instance,
            batchGate: batch,
            gateLogReader: folder => { gateReads++; return FlowGateLogEvidence.Read(folder); },
            batchReader: (store, project) => { batchReads++; return BatchGateCycleEvidence.Load(store, project); });
        var now = T0.AddHours(2);
        var initial = service.BuildTask("Demo", "memo-card", now)!.Value.Response!.Task;
        Assert.Equal(0, initial.Segments!.Where(s => s.Resource == "gateHost").Sum(s => s.Seconds));
        service.BuildTask("Demo", "memo-card", now);
        Assert.Equal(1, gateReads);
        Assert.Equal(1, batchReads);

        var postSteps = Path.Combine(watchPath, TaskStates.Completed, "memo-card", "post-steps");
        Directory.CreateDirectory(postSteps);
        File.WriteAllText(Path.Combine(postSteps, "build-test-gate-1.log"),
            $"startedAtUtc={T0.AddMinutes(31):O} queueWaitMs=60000 durationMs=0\n");
        var afterLog = service.BuildTask("Demo", "memo-card", now)!.Value.Response!.Task;
        Assert.Equal(60, afterLog.Segments!.Where(s => s.Resource == "gateHost").Sum(s => s.Seconds));
        Assert.Equal(2, gateReads);
        Assert.Equal(1, batchReads);

        var execution = new BatchGateExecutionFact("Demo", null, "run-1", new string('a', 40),
            "Ok", "TUFI2", new DateTimeOffset(T0.AddMinutes(35)),
            new DateTimeOffset(T0.AddMinutes(45)), 300_000, 0, "review_memo/gate.log");
        var executionFile = Path.Combine(_root, "batch-gates", "executions", "memo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(executionFile)!);
        File.WriteAllText(executionFile, JsonSerializer.Serialize(execution,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var afterBatch = service.BuildTask("Demo", "memo-card", now)!.Value.Response!.Task;
        Assert.Equal(360, afterBatch.Segments!.Where(s => s.Resource == "gateHost").Sum(s => s.Seconds));
        Assert.Equal(2, batchReads);
    }

    // ---- per-task analysis ------------------------------------------------

    [Fact]
    public void RemoteFlow_WithBounceRound_AttributesEveryStage_AndStagesSumToLeadTime()
    {
        var task = Task("remote", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddHours(7));
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(10), TaskStates.Ready, TaskStates.Progress),
            Event(T0.AddMinutes(40), TimelineEventKinds.AgentRunFinished),
            Lane(T0.AddMinutes(40), TaskStates.Progress, TaskStates.AutoReview),
        };
        // First review attempt: waits 20 min, gate 780 s, aspects 60 s, review
        // fails; the lane change lands one second after the verdict row.
        var review1 = T0.AddMinutes(60);
        events.AddRange(GateSteps(review1, "review_1", 60, 120, 600));
        events.AddRange(AspectSteps(review1.AddSeconds(780), "review_1", 15));
        var review1End = review1.AddSeconds(780 + 60);
        events.Add(Integration(review1End, TimelineEventKinds.IntegrationFailed, "delivery-gate-failed", "pre-human-review"));
        var laneOut1 = review1End.AddSeconds(1);
        events.Add(Lane(laneOut1, TaskStates.AutoReview, TaskStates.HumanReview));
        // Operator requeue after 2 h in Human Review.
        var requeue = laneOut1.AddHours(2);
        events.Add(Lane(requeue, TaskStates.HumanReview, TaskStates.Ready));
        events.Add(Event(requeue, TimelineEventKinds.OperatorRequeued));
        events.Add(Lane(requeue.AddMinutes(5), TaskStates.Ready, TaskStates.Progress));
        events.Add(Lane(requeue.AddMinutes(30), TaskStates.Progress, TaskStates.AutoReview));
        // Second review attempt: waits 5 min, gate 300 s, aspects 40 s, merge 120 s.
        var review2 = requeue.AddMinutes(35);
        events.AddRange(GateSteps(review2, "review_2", 300));
        events.AddRange(AspectSteps(review2.AddSeconds(300), "review_2", 10));
        var mergeStart = review2.AddSeconds(340);
        var mergeEnd = mergeStart.AddSeconds(120);
        events.Add(Integration(mergeEnd, TimelineEventKinds.IntegrationSucceeded, "Merged", "pre-human-review"));
        var laneOut2 = mergeEnd.AddSeconds(1);
        events.Add(Lane(laneOut2, TaskStates.AutoReview, TaskStates.HumanReview));
        var completed = T0.AddHours(6);
        events.Add(Lane(completed, TaskStates.HumanReview, TaskStates.Completed));
        events.Add(Lane(T0.AddHours(7), TaskStates.Completed, TaskStates.Archive));

        var pipeline = new PipelineExecutionRecord
        {
            StartedAt = requeue.AddMinutes(5),
            Steps =
            [
                Step(PipelineCatalogue.MergeIntoDevelopStepId, mergeStart, mergeEnd),
                Step(PipelineCatalogue.MergeIntoDevelopPushStepId, mergeEnd, mergeEnd.AddSeconds(5)),
            ],
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, pipeline).Row;

        Assert.NotNull(row);
        Assert.Equal("ledger", row!.CompletionSource);
        Assert.Equal(6 * 3600, row.LeadTimeSeconds);
        Assert.Equal(6 * 3600 - 600, row.CycleTimeSeconds);
        Assert.Equal(2, row.CodingRuns);
        Assert.Equal(2, row.ReviewRounds);
        Assert.Equal(1, row.BounceRounds);
        Assert.Equal(1, row.IntegrationAttempts);
        Assert.Equal("Merged", row.IntegrationOutcome);
        Assert.Equal("pre-human-review", row.IntegrationStage);

        var s = row.Stages;
        Assert.Equal(0, s.Preparation);
        Assert.Equal(15 * 60, s.QueueWait);
        Assert.Equal(55 * 60, s.Coding);
        Assert.Equal(25 * 60, s.ReviewWait);
        Assert.Equal(780 + 300, s.TestGate);
        Assert.Equal(125, s.Integration); // merge 120 s + push 5 s
        // Aspects (60 s + 40 s) plus the one-second tail before each lane change.
        Assert.Equal(61 + 36, s.ReviewOther);
        var expectedHuman = (requeue - laneOut1).TotalSeconds + (completed - laneOut2).TotalSeconds;
        Assert.Equal(expectedHuman, s.HumanReview);
        Assert.Equal(0, s.Unattributed);
        Assert.Equal(row.LeadTimeSeconds, Math.Round(s.Sum, 1));
        Assert.Equal(841 + 461, row.ReviewRunSeconds);
        Assert.DoesNotContain("review-start-unknown", row.DataGaps);
    }

    [Fact]
    public void LocalFlow_UsesPipelineGateSteps_AcceptanceIntegration_AndQualityLoopBounce()
    {
        var task = Task("local", TaskStates.Completed, createdAt: T0, enteredLaneAt: T0.AddHours(5));
        var claim = T0.AddMinutes(2);
        var delivered = claim.AddMinutes(20);
        var reopened = delivered.AddMinutes(6);
        var claim2 = reopened.AddMinutes(1);
        var delivered2 = claim2.AddMinutes(15);
        var toHuman = delivered2.AddMinutes(8);
        var acceptStart = toHuman.AddHours(1);
        var acceptEnd = acceptStart.AddSeconds(90);
        var completed = acceptEnd.AddSeconds(10);

        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(claim, TaskStates.Ready, TaskStates.Progress),
            Event(claim, TimelineEventKinds.AgentRunStarted),
            Lane(delivered, TaskStates.Progress, TaskStates.AutoReview),
            Lane(reopened, TaskStates.AutoReview, TaskStates.Ready),
            Event(reopened, TimelineEventKinds.QualityLoopReopened),
            Lane(claim2, TaskStates.Ready, TaskStates.Progress),
            Lane(delivered2, TaskStates.Progress, TaskStates.AutoReview),
            Lane(toHuman, TaskStates.AutoReview, TaskStates.HumanReview),
            Event(acceptStart, TimelineEventKinds.IntegrationStarted, new() { ["outcome"] = "integrating" }),
            Integration(acceptEnd, TimelineEventKinds.IntegrationSucceeded, "Merged", null),
            Lane(completed, TaskStates.HumanReview, TaskStates.Completed),
        };
        var pipeline = new PipelineExecutionRecord
        {
            StartedAt = claim2,
            Attempt = 2,
            Steps =
            [
                Step(PipelineCatalogue.CoreAgentRunStepId, claim2, delivered2),
                Step(PipelineCatalogue.OrchestratorReviewStepId, delivered2.AddSeconds(30), delivered2.AddSeconds(30)),
                Step(PipelineCatalogue.BuildTestGateStepId, delivered2.AddSeconds(40), delivered2.AddSeconds(40 + 240)),
                Step("aspect-code-quality", delivered2.AddSeconds(300), delivered2.AddSeconds(330)),
            ],
            PreviousAttempts =
            [
                new PipelineExecutionRecord
                {
                    StartedAt = claim,
                    Attempt = 1,
                    Steps =
                    [
                        Step(PipelineCatalogue.CoreAgentRunStepId, claim, delivered),
                        Step(PipelineCatalogue.BuildTestGateStepId, delivered.AddSeconds(20), delivered.AddSeconds(20 + 180)),
                        Step(PipelineCatalogue.OrchestratorDecisionStepId, reopened, reopened),
                    ],
                },
            ],
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, pipeline).Row;

        Assert.NotNull(row);
        Assert.Equal(2, row!.CodingRuns);
        Assert.Equal(1, row.BounceRounds);
        Assert.Equal(2, row.ReviewRounds);
        Assert.Equal(1, row.IntegrationAttempts);
        Assert.Equal("Merged", row.IntegrationOutcome);
        Assert.Equal("acceptance", row.IntegrationStage);
        Assert.Equal(180 + 240, row.Stages.TestGate);
        Assert.Equal(90, row.Stages.Integration);
        Assert.Equal(20 + 30, row.Stages.ReviewWait); // first post step in each auto-review stay
        Assert.Equal((completed - toHuman).TotalSeconds - 90, row.Stages.HumanReview);
        Assert.Equal(35 * 60, row.Stages.Coding);
        Assert.Equal(3 * 60, row.Stages.QueueWait);
        Assert.Equal(row.LeadTimeSeconds, Math.Round(row.Stages.Sum, 1));
        Assert.Equal((completed - claim).TotalSeconds, row.CycleTimeSeconds);
    }

    [Fact]
    public void AutoReviewWithoutEvidence_IsWaiting_AndReported()
    {
        var task = Task("quiet", TaskStates.Completed, createdAt: T0, enteredLaneAt: T0.AddHours(2));
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(1), TaskStates.Ready, TaskStates.Progress),
            Lane(T0.AddMinutes(11), TaskStates.Progress, TaskStates.AutoReview),
            Lane(T0.AddMinutes(41), TaskStates.AutoReview, TaskStates.HumanReview, actor: "human"),
            Lane(T0.AddHours(2), TaskStates.HumanReview, TaskStates.Completed),
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, null).Row;

        Assert.NotNull(row);
        Assert.Equal(30 * 60, row!.Stages.ReviewWait);
        Assert.Equal(0, row.ReviewRunSeconds);
        Assert.Equal(0, row.Stages.TestGate);
        Assert.Contains("review-start-unknown", row.DataGaps);
        Assert.Null(row.IntegrationOutcome);
    }

    [Fact]
    public void MissingLedger_FallsBackToLaneEntry_AndReportsUnattributedLeadTime()
    {
        var completedAt = T0.AddHours(3);
        var task = Task("legacy", TaskStates.Completed, createdAt: T0, enteredLaneAt: completedAt);

        var row = TaskCycleTimeAnalyzer.Analyze(task, [], null).Row;

        Assert.NotNull(row);
        Assert.Equal("lane-entry", row!.CompletionSource);
        Assert.Equal(3 * 3600, row.LeadTimeSeconds);
        Assert.Equal(3 * 3600, row.Stages.Unattributed);
        Assert.Null(row.CycleTimeSeconds);
        Assert.Contains("no-ledger", row.DataGaps);
        Assert.Contains("completion-from-lane-entry", row.DataGaps);
    }

    [Fact]
    public void ArchivedWithoutCompletionEvent_InFlight_AndEpics_AreExcluded()
    {
        var archived = Task("archived", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddDays(1));
        var archivedEvents = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddDays(1), TaskStates.HumanReview, TaskStates.Archive),
        };
        Assert.Equal(TaskCycleAnalysis.ExcludedNoCompletion,
            TaskCycleTimeAnalyzer.Analyze(archived, archivedEvents, null).ExclusionReason);

        var inFlight = Task("open", TaskStates.AutoReview, createdAt: T0, enteredLaneAt: T0.AddHours(1));
        Assert.Equal(TaskCycleAnalysis.ExcludedNotCompleted,
            TaskCycleTimeAnalyzer.Analyze(inFlight, [], null).ExclusionReason);

        var epic = Task("epic", TaskStates.Completed, createdAt: T0, enteredLaneAt: T0.AddHours(1)) with { Kind = TaskKinds.Epic };
        Assert.Equal(TaskCycleAnalysis.ExcludedEpic,
            TaskCycleTimeAnalyzer.Analyze(epic, [Lane(T0.AddHours(1), TaskStates.HumanReview, TaskStates.Completed)], null).ExclusionReason);
    }

    [Fact]
    public void CreationAfterCompletion_IsClampedAndFlagged_AndReAcceptanceDoesNotMoveAnchor()
    {
        // task.json createdAt rewritten after completion (clock skew): lead time must not go negative.
        var skewed = Task("skew", TaskStates.Completed, createdAt: T0.AddHours(5), enteredLaneAt: T0.AddHours(4));
        var skewedRow = TaskCycleTimeAnalyzer.Analyze(skewed,
            [Lane(T0.AddHours(4), TaskStates.HumanReview, TaskStates.Completed)], null).Row;
        Assert.NotNull(skewedRow);
        Assert.Equal(0, skewedRow!.LeadTimeSeconds);
        Assert.Contains("clock-skew", skewedRow.DataGaps);

        // A fallback and re-acceptance without coding rework keep the first completion.
        var reverted = Task("revert", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddHours(10));
        var firstDone = T0.AddHours(2);
        var back = firstDone.AddHours(1);
        var finalDone = back.AddHours(3);
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.HumanReview),
            Lane(firstDone, TaskStates.HumanReview, TaskStates.Completed),
            Lane(back, TaskStates.Completed, TaskStates.HumanReview),
            Integration(back, TimelineEventKinds.IntegrationFailed, "GateFailed", null),
            Lane(finalDone, TaskStates.HumanReview, TaskStates.Completed),
            Lane(finalDone.AddHours(1), TaskStates.Completed, TaskStates.Archive),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(reverted, events, null).Row;
        Assert.NotNull(row);
        Assert.Equal(firstDone, row!.CompletedAt);
        Assert.Equal(2 * 3600, row.LeadTimeSeconds);
        Assert.Equal(2 * 3600, row.Stages.HumanReview);
        Assert.Equal(1, row.ReAcceptanceArtefacts);
        Assert.Equal("first", row.CompletionKind);
        Assert.Equal(0, row.BounceRounds);
        Assert.Null(row.IntegrationOutcome);
        Assert.Equal(0, row.IntegrationAttempts);
    }

    [Fact]
    public void GateDuration_FallsBackToStepTimestamps_WhenDurationDetailIsMissing()
    {
        var task = Task("nodur", TaskStates.Completed, createdAt: T0, enteredLaneAt: T0.AddHours(1));
        var start = T0.AddMinutes(10);
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(1), TaskStates.Ready, TaskStates.Progress),
            Lane(T0.AddMinutes(5), TaskStates.Progress, TaskStates.AutoReview),
            PostStep(TimelineEventKinds.PostStepStarted, start, "verify-1", PipelineCatalogue.BuildTestGateStepId, "r1", null),
            PostStep(TimelineEventKinds.PostStepFinished, start.AddSeconds(200), "verify-1", PipelineCatalogue.BuildTestGateStepId, "r1", null),
            Lane(start.AddSeconds(260), TaskStates.AutoReview, TaskStates.HumanReview),
            Lane(T0.AddHours(1), TaskStates.HumanReview, TaskStates.Completed),
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, null).Row;

        Assert.NotNull(row);
        Assert.Equal(200, row!.Stages.TestGate);
        Assert.Equal(60, row.Stages.ReviewOther);
        Assert.Equal(5 * 60, row.Stages.ReviewWait);
    }

    [Fact]
    public void RepeatedOutcomeRows_CountOnce_AndAMergeStepPairsWithOneOutcomeOnly()
    {
        // Two writers record the same failure 17 s apart, a retry loop repeats an
        // outcome every 5 s, and a later integration round starts with its own row.
        var task = Task("dupes", TaskStates.Completed, createdAt: T0, enteredLaneAt: T0.AddHours(3));
        var delivered = T0.AddMinutes(10);
        var mergeStart = delivered.AddMinutes(5);
        var mergeEnd = mergeStart.AddSeconds(100);
        var toHuman = mergeEnd.AddSeconds(2);
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(1), TaskStates.Ready, TaskStates.Progress),
            Lane(delivered, TaskStates.Progress, TaskStates.AutoReview),
            Integration(mergeEnd, TimelineEventKinds.IntegrationFailed, "Conflict", "pre-human-review"),
            Integration(mergeEnd.AddSeconds(1), TimelineEventKinds.IntegrationFailed, "Conflict", "pre-human-review"),
            Lane(toHuman, TaskStates.AutoReview, TaskStates.HumanReview),
        };
        // Burst of the same outcome after the lane change, every 5 s for a minute.
        for (var i = 0; i < 12; i++)
            events.Add(Integration(toHuman.AddSeconds(3 + i * 5), TimelineEventKinds.IntegrationFailed, "Conflict", "pre-human-review"));
        // A real acceptance round later: started row, its own outcome.
        var acceptStart = toHuman.AddHours(1);
        events.Add(Event(acceptStart, TimelineEventKinds.IntegrationStarted, new() { ["outcome"] = "integrating" }));
        events.Add(Integration(acceptStart.AddSeconds(60), TimelineEventKinds.IntegrationSucceeded, "Merged", null));
        events.Add(Lane(T0.AddHours(3), TaskStates.HumanReview, TaskStates.Completed));
        var pipeline = new PipelineExecutionRecord
        {
            StartedAt = T0.AddMinutes(1),
            Steps = [Step(PipelineCatalogue.MergeIntoDevelopStepId, mergeStart, mergeEnd)],
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, pipeline).Row;

        Assert.NotNull(row);
        // 1 failed merge (all repeats folded) + 1 acceptance merge.
        Assert.Equal(2, row!.IntegrationAttempts);
        // The merge step pairs once (100 s), the acceptance span is 60 s.
        Assert.Equal(160, row.Stages.Integration);
        Assert.Equal("Merged", row.IntegrationOutcome);
        Assert.Equal("acceptance", row.IntegrationStage);
        Assert.DoesNotContain("integration-duration-unknown", row.DataGaps);
        Assert.Equal(row.LeadTimeSeconds, Math.Round(row.Stages.Sum, 1));

        var collapsed = TaskCycleTimeAnalyzer.CollapseRepeatedOutcomes(events.OrderBy(e => e.Ts).ToList());
        Assert.Equal(2, collapsed.Count);
        Assert.Equal(mergeEnd, collapsed[0].Ts); // the first row of a repeat group keeps the attempt end
    }

    [Fact]
    public void AcceptanceOutcomeRecordedAfterTheCompletionMove_CountsAsAttempt_AndIsTheOutcome()
    {
        // Acceptance moves the card to 6-completed and records Merged a second
        // later; an earlier failed attempt must not remain the reported outcome.
        var task = Task("late", TaskStates.Archive, createdAt: T0, enteredLaneAt: T0.AddHours(3));
        var toHuman = T0.AddMinutes(30);
        var firstStart = toHuman.AddMinutes(10);
        var firstFail = firstStart.AddSeconds(20);
        var acceptStart = toHuman.AddHours(1);
        var completed = acceptStart.AddSeconds(45);
        var lateOutcome = completed.AddSeconds(1.1);
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(1), TaskStates.Ready, TaskStates.Progress),
            Lane(T0.AddMinutes(20), TaskStates.Progress, TaskStates.AutoReview),
            Lane(toHuman, TaskStates.AutoReview, TaskStates.HumanReview),
            Event(firstStart, TimelineEventKinds.IntegrationStarted, new() { ["outcome"] = "integrating" }),
            Integration(firstFail, TimelineEventKinds.IntegrationFailed, "Conflict", null),
            Event(acceptStart, TimelineEventKinds.IntegrationStarted, new() { ["outcome"] = "integrating" }),
            Lane(completed, TaskStates.HumanReview, TaskStates.Completed),
            Integration(lateOutcome, TimelineEventKinds.IntegrationSucceeded, "AlreadyMerged", null),
            Lane(completed.AddHours(1), TaskStates.Completed, TaskStates.Archive),
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, events, null).Row;

        Assert.NotNull(row);
        Assert.Equal(completed, row!.CompletedAt);
        Assert.Equal(2, row.IntegrationAttempts);
        Assert.Equal("AlreadyMerged", row.IntegrationOutcome);
        Assert.Equal("acceptance", row.IntegrationStage);
        // 20 s failed attempt + 45 s of the acceptance attempt up to the completion move.
        Assert.Equal(65, row.Stages.Integration);
        Assert.Equal((completed - toHuman).TotalSeconds - 65, row.Stages.HumanReview);
        Assert.Equal(row.LeadTimeSeconds, Math.Round(row.Stages.Sum, 1));
    }

    // ---- statistics ---------------------------------------------------------

    [Fact]
    public void Statistics_UseClassicMedian_NearestRankP90_AndIgnoreNonFiniteValues()
    {
        var aggregate = CycleTimeStatistics.Aggregate(
            CycleTimeStages.TestGate, "stage", "seconds", [40, 10, double.NaN, 30, 20, double.PositiveInfinity]);

        Assert.Equal(4, aggregate.Count);
        Assert.Equal(25, aggregate.P50);
        Assert.Equal(40, aggregate.P90); // ceil(0.9 * 4) = 4th value
        Assert.Equal(40, aggregate.Max);
        Assert.Equal(25, aggregate.Mean);
        Assert.Equal(100, aggregate.Total);
        Assert.True(aggregate.Highlighted);
        Assert.Equal("Build/test gate", aggregate.Label);

        var ten = CycleTimeStatistics.Aggregate(CycleTimeStages.QueueWait, "stage", "seconds",
            Enumerable.Range(1, 10).Select(i => (double)i * 10));
        Assert.Equal(55, ten.P50);
        Assert.Equal(90, ten.P90);
        Assert.False(ten.Highlighted);

        var empty = CycleTimeStatistics.Aggregate(CycleTimeStages.Integration, "stage", "seconds", []);
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.P50);
        Assert.Null(empty.P90);
        Assert.Null(empty.Max);
    }

    [Theory]
    [InlineData("7d", "7d", 7)]
    [InlineData("30D", "30d", 30)]
    [InlineData(" all ", "all", -1)]
    [InlineData(null, "7d", 7)]
    public void TryParseWindow_AcceptsDaysAndAll(string? raw, string expectedWindow, int expectedDays)
    {
        Assert.True(ProjectCycleTimeService.TryParseWindow(raw, out var window, out var span));
        Assert.Equal(expectedWindow, window);
        if (expectedDays < 0) Assert.Null(span);
        else Assert.Equal(TimeSpan.FromDays(expectedDays), span);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0d")]
    [InlineData("7h")]
    public void TryParseWindow_RejectsUnknownValues(string raw)
    {
        Assert.False(ProjectCycleTimeService.TryParseWindow(raw, out _, out _));
    }

    // ---- response building ------------------------------------------------

    [Fact]
    public void BuildResponse_FiltersByWindow_OrdersNewestFirst_AndCountsCoverage()
    {
        var now = T0.AddDays(10);
        var recent = Row("recent", completedAt: now.AddDays(-1), leadTime: 3600, testGate: 100, outcome: "Merged");
        var older = Row("older", completedAt: now.AddDays(-3), leadTime: 7200, testGate: 0, outcome: "Merged");
        var ancient = Row("ancient", completedAt: now.AddDays(-20), leadTime: 9000, testGate: 50, outcome: "Conflict");
        var analyses = new List<TaskCycleAnalysis>
        {
            new(older, null),
            new(recent, null),
            new(ancient, null),
            new(null, TaskCycleAnalysis.ExcludedNotCompleted),
            new(null, TaskCycleAnalysis.ExcludedNoCompletion),
            new(null, TaskCycleAnalysis.ExcludedEpic),
            new(null, TaskCycleAnalysis.ExcludedBeforeWindow),
        };

        var response = ProjectCycleTimeService.BuildResponse(
            "Demo", "PROJ-001", "DEM", "7d", now, now.AddDays(-7), analyses);

        Assert.Equal(new[] { "recent", "older" }, response.Tasks.Select(t => t.TaskId));
        Assert.Equal(7, response.Coverage.TasksInProject);
        Assert.Equal(5, response.Coverage.TasksTerminal);
        Assert.Equal(2, response.Coverage.TasksInWindow);
        Assert.Equal(1, response.Coverage.ExcludedNoCompletionTimestamp);
        Assert.Equal(1, response.Coverage.ExcludedInFlight);
        Assert.Equal(1, response.Coverage.ExcludedEpics);

        var lead = response.Aggregates.Single(a => a.Stage == CycleTimeStages.LeadTime);
        Assert.Equal("rollup", lead.Kind);
        Assert.Equal(2, lead.Count);
        Assert.Equal(5400, lead.P50);
        Assert.Equal(7200, lead.Max);

        var gate = response.Aggregates.Single(a => a.Stage == CycleTimeStages.TestGate);
        Assert.Equal(1, gate.Count); // occurrence-based: the older row had no gate
        Assert.Equal(100, gate.P50);

        var runs = response.Aggregates.Single(a => a.Stage == CycleTimeStages.CodingRuns);
        Assert.Equal("count", runs.Unit);
        Assert.Equal(2, runs.Count);

        Assert.Equal(CycleTimeStages.Additive, response.Aggregates.Take(CycleTimeStages.Additive.Count).Select(a => a.Stage));
        Assert.Single(response.IntegrationOutcomes);
        Assert.Equal(("Merged", 2), (response.IntegrationOutcomes[0].Outcome, response.IntegrationOutcomes[0].Count));

        var all = ProjectCycleTimeService.BuildResponse("Demo", null, null, "all", now, null, analyses);
        Assert.Equal(3, all.Tasks.Count);
        Assert.Equal(2, all.IntegrationOutcomes.Count);
    }

    // ---- file-backed service ------------------------------------------------

    [Fact]
    public void Service_ReadsLedgerAndPipelineFromDisk_MemoisesRows_AndResolvesProjectHandles()
    {
        var watchPath = Path.Combine(_root, "projects", "demo");
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watchPath, state));
        var now = DateTime.UtcNow;
        var created = now.AddHours(-6);
        var completed = now.AddHours(-1);
        SeedJob(watchPath, TaskStates.Completed, "done-task", created, completed,
        [
            Created(created, TaskStates.Ready),
            Lane(created.AddMinutes(5), TaskStates.Ready, TaskStates.Progress),
            Lane(created.AddMinutes(25), TaskStates.Progress, TaskStates.AutoReview),
            Lane(created.AddMinutes(40), TaskStates.AutoReview, TaskStates.HumanReview),
            Lane(completed, TaskStates.HumanReview, TaskStates.Completed),
        ], new PipelineExecutionRecord
        {
            StartedAt = created.AddMinutes(5),
            Steps = [Step(PipelineCatalogue.BuildTestGateStepId, created.AddMinutes(27), created.AddMinutes(32))],
        });
        SeedJob(watchPath, TaskStates.Archive, "old-task", created.AddDays(-30), created.AddDays(-29),
        [
            Created(created.AddDays(-30), TaskStates.Ready),
            Lane(created.AddDays(-29), TaskStates.HumanReview, TaskStates.Completed),
            Lane(created.AddDays(-28), TaskStates.Completed, TaskStates.Archive),
        ], null);
        SeedJob(watchPath, TaskStates.Ready, "open-task", created, created, [Created(created, TaskStates.Ready)], null);
        // Archived ten days ago without a recorded 6-completed entry: examined by
        // the 30d window (and counted as excluded), skipped by the 7d window.
        SeedJob(watchPath, TaskStates.Archive, "ghost-task", created.AddDays(-11), created.AddDays(-10),
        [
            Created(created.AddDays(-11), TaskStates.Ready),
            Lane(created.AddDays(-10), TaskStates.HumanReview, TaskStates.Archive),
        ], null);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = watchPath,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var service = new ProjectCycleTimeService(
            scanner,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<ProjectCycleTimeService>.Instance);

        var week = service.Build("Demo", "7d");
        Assert.NotNull(week);
        Assert.Equal("7d", week!.Window);
        Assert.Single(week.Tasks);
        var row = week.Tasks[0];
        Assert.Equal("done-task", row.TaskId);
        Assert.True(WatchPathComparison.PathsEqual(watchPath, row.WatchPath), row.WatchPath);
        Assert.Equal(5 * 60, row.Stages.QueueWait);
        Assert.Equal(20 * 60, row.Stages.Coding);
        Assert.Equal(2 * 60, row.Stages.ReviewWait);
        Assert.Equal(5 * 60, row.Stages.TestGate);
        Assert.Equal(8 * 60, row.Stages.ReviewOther);
        Assert.Equal(4, week.Coverage.TasksInProject);
        Assert.Equal(3, week.Coverage.TasksTerminal);
        Assert.Equal(1, week.Coverage.ExcludedInFlight);
        Assert.Equal(0, week.Coverage.ExcludedNoCompletionTimestamp); // ghost-task lies before the 7d window

        var month = service.Build("Demo", "30d");
        Assert.NotNull(month);
        Assert.Equal(2, month!.Tasks.Count);
        Assert.Equal(1, month.Coverage.ExcludedNoCompletionTimestamp);
        Assert.Equal(3, month.Coverage.TasksTerminal);

        // The 7d coverage must not change because a 30d run is cached.
        var weekAgain = service.Build("Demo", "7d");
        Assert.NotNull(weekAgain);
        Assert.Equal(0, weekAgain!.Coverage.ExcludedNoCompletionTimestamp);
        Assert.Single(weekAgain.Tasks);

        var all = service.Build("Demo", "all");
        Assert.NotNull(all);
        Assert.Equal(2, all!.Tasks.Count);
        Assert.Equal(new[] { "done-task", "old-task" }, all.Tasks.Select(t => t.TaskId));
        Assert.Equal(0, all.Coverage.TasksWithoutLedger);
        Assert.Equal(0, all.Coverage.TasksWithLaneEntryCompletion);
        Assert.Equal(1, all.Coverage.ExcludedNoCompletionTimestamp);

        // Second read of the same window is served from the memo and stays identical.
        var again = service.Build("demo", "all");
        Assert.NotNull(again);
        Assert.Equal(all.Tasks.Select(t => t.TaskId), again!.Tasks.Select(t => t.TaskId));

        Assert.Null(service.Build("Unknown", "7d"));
        Assert.Throws<ArgumentException>(() => service.Build("Demo", "nope"));
    }

    // ---- helpers ------------------------------------------------------------

    private static TaskInfo Task(string id, string state, DateTime createdAt, DateTime enteredLaneAt) => new()
    {
        Id = id,
        TaskKey = "DEMO::" + id,
        Key = "DEM-" + id,
        Title = id + " title",
        State = state,
        CreatedAt = createdAt,
        EnteredLaneAt = enteredLaneAt,
    };

    private static TaskCycleTime Row(string id, DateTime completedAt, double leadTime, double testGate, string? outcome) =>
        new(id, "DEM-" + id, id, TaskStates.Archive, @"C:\demo", completedAt.AddSeconds(-leadTime), completedAt.AddSeconds(-leadTime + 60),
            completedAt, "ledger",
            new CycleTimeStageSeconds { QueueWait = 60, Coding = leadTime - 60 - testGate, TestGate = testGate },
            testGate, leadTime, leadTime - 60, 1, 1, 0, outcome is null ? 0 : 1, outcome,
            outcome is null ? null : "pre-human-review", [], 0, []);

    private static TimelineEvent Event(DateTime at, string kind, Dictionary<string, string>? details = null) => new()
    {
        Ts = at,
        Kind = kind,
        Actor = TimelineActors.System,
        Summary = kind,
        Details = details,
    };

    private static TimelineEvent Created(DateTime at, string targetState) =>
        Event(at, TimelineEventKinds.PromptCreated, new() { ["targetState"] = targetState });

    private static TimelineEvent Lane(DateTime at, string from, string to, string actor = "system") => new()
    {
        Ts = at,
        Kind = TimelineEventKinds.LaneChanged,
        Actor = actor,
        Summary = $"{from} -> {to}",
        Details = new Dictionary<string, string> { ["from"] = from, ["to"] = to },
    };

    private static TimelineEvent Integration(DateTime at, string kind, string outcome, string? stage)
    {
        var details = new Dictionary<string, string> { ["outcome"] = outcome, ["integrationBranch"] = "develop" };
        if (stage is not null) details["stage"] = stage;
        return Event(at, kind, details);
    }

    private static TimelineEvent PostStep(string kind, DateTime at, string stepId, string pipelineStepId, string attemptId, long? durationMs)
    {
        var details = new Dictionary<string, string>
        {
            ["stepId"] = stepId,
            ["pipelineStepId"] = pipelineStepId,
            ["attemptId"] = attemptId,
            ["executionLocation"] = "remote",
        };
        if (durationMs is not null) details["durationMs"] = durationMs.Value.ToString();
        return new TimelineEvent
        {
            Ts = at,
            Kind = kind,
            Actor = TimelineActors.External,
            RunId = attemptId,
            Summary = $"{kind} {stepId}",
            Details = details,
        };
    }

    /// <summary>Remote gate steps (prepare/verify) back to back; each has started+finished rows with durationMs.</summary>
    private static IEnumerable<TimelineEvent> GateSteps(DateTime start, string attemptId, params int[] seconds)
    {
        var cursor = start;
        for (var i = 0; i < seconds.Length; i++)
        {
            var stepId = i == 0 ? "prepare-1" : $"verify-{i}";
            yield return PostStep(TimelineEventKinds.PostStepStarted, cursor, stepId, PipelineCatalogue.BuildTestGateStepId, attemptId, seconds[i] * 1000L);
            cursor = cursor.AddSeconds(seconds[i]);
            yield return PostStep(TimelineEventKinds.PostStepFinished, cursor, stepId, PipelineCatalogue.BuildTestGateStepId, attemptId, seconds[i] * 1000L);
        }
    }

    private static IEnumerable<TimelineEvent> AspectSteps(DateTime start, string attemptId, int secondsEach)
    {
        var cursor = start;
        foreach (var aspect in new[] { "aspect-requirement-fit", "aspect-code-quality", "aspect-documentation-impact", "aspect-tests-and-evidence" })
        {
            yield return PostStep(TimelineEventKinds.PostStepStarted, cursor, aspect, aspect, attemptId, secondsEach * 1000L);
            cursor = cursor.AddSeconds(secondsEach);
            yield return PostStep(TimelineEventKinds.PostStepFinished, cursor, aspect, aspect, attemptId, secondsEach * 1000L);
        }
    }

    private static PipelineStepExecution Step(string stepId, DateTime startedAt, DateTime completedAt) => new()
    {
        StepId = stepId,
        Status = PipelineStepStatus.Passed,
        StartedAt = startedAt,
        CompletedAt = completedAt,
        DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
    };

    private static void SeedJob(
        string watchPath,
        string state,
        string slug,
        DateTime createdAt,
        DateTime enteredLaneAt,
        IEnumerable<TimelineEvent> events,
        PipelineExecutionRecord? pipeline)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\",\"createdAt\":\"{createdAt:o}\",\"enteredLaneAt\":\"{enteredLaneAt:o}\"}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {slug}\n");
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        foreach (var evt in events) timeline.Append(dir, evt);
        if (pipeline is not null)
        {
            File.WriteAllText(Path.Combine(dir, PipelineExecutionLog.FileName),
                JsonSerializer.Serialize(pipeline, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
}
