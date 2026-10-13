using AgentStudio.Projects;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the classifier compares only the supplied fixture instants.
public sealed class FlowEpisodeClassifierTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GoldenAgt3095_SplitsMachineRuntimeGateQueueAndOperatorTail()
    {
        static DateTime At(int day, int hour, int minute, int second, int ms = 0)
            => new(2026, 10, day, hour, minute, second, ms, DateTimeKind.Utc);
        var created = At(12, 20, 51, 49, 617);
        var claim = At(13, 0, 54, 45, 442);
        var delivered = At(13, 1, 31, 42, 643);
        var reviewClaim = At(13, 2, 35, 1, 900);
        var gateEnd = At(13, 2, 37, 41, 306);
        var aspectEnd = At(13, 2, 38, 29, 742);
        var pending = At(13, 2, 38, 32, 347);
        var firstStart = At(13, 3, 35, 20, 647);
        var firstEnd = At(13, 3, 43, 30, 348);
        var secondStart = At(13, 3, 46, 8, 755);
        var secondEnd = At(13, 5, 11, 49, 753);
        var queueEnd = secondStart.AddMilliseconds(3_242_843);
        var accepted = At(13, 6, 58, 14, 51);
        var events = new[]
        {
            Created(created, TaskStates.Ready),
            Lane(claim, TaskStates.Ready, TaskStates.Progress, "remote-runner:agent-runner-01", "claimed", "run_8b68"),
            Lane(delivered, TaskStates.Progress, TaskStates.AutoReview, "remote-runner:agent-runner-01", "delivered"),
            Claim(reviewClaim, "review_d2fb"),
            Step(gateEnd, "review_d2fb", PipelineCatalogue.BuildTestGateStepId, 146_905),
            Step(aspectEnd, "review_d2fb", "aspect-code-quality", 47_499),
            Lane(accepted, TaskStates.AutoReview, TaskStates.Completed, "human:local-default", "accepted"),
        };
        var evidence = new[]
        {
            Segment("resourceWait", null, "reviewSlot", aspectEnd, pending),
            Segment("resourceWait", null, "gateHost", pending, firstStart),
            Segment("active", "batchGateNoVerdict", null, firstStart, firstEnd),
            Segment("resourceWait", null, "gateHost", firstEnd, secondStart),
            Segment("resourceWait", null, "gateHost", secondStart, queueEnd),
            Segment("active", "batchGate", null, queueEnd, secondEnd),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(Task("AGT-3095", TaskStates.Completed),
            events, null, gateEvidence: evidence).Row!;
        var calls = new TaskTokenSummary { Entries =
        [
            new TaskTokenCall { Ts = At(13, 1, 30, 39, 687), ParticipantId = "agent:remote-runner:run_8b68:usage:scope",
                InputTokens = 6_000_000, EstimatedApiCostUsd = 3.2937m, ModelPriced = true },
            new TaskTokenCall { Ts = At(13, 2, 37, 51), ParticipantId = "support:remote-review:review_d2fb",
                CacheReadTokens = 1_502_399, EstimatedApiCostUsd = 0.9676m, ModelPriced = true },
        ] };
        row = FlowTokenAttribution.Apply(row, calls, events);
        Assert.InRange(row.EpisodeSeconds!.Value / 60, 606.40, 606.42);
        Assert.InRange(row.MachineCycleSeconds!.Value / 60, 499.9, 500.1);
        Assert.InRange(row.ActiveSeconds!.Value / 60, 80.1, 80.3);
        Assert.InRange(row.ResourceWaitSeconds!.Value / 60, 419.7, 419.9);
        Assert.InRange(Seconds(row.Flow!, "resourceWait", "gateHost") / 60, 113.4, 113.6);
        Assert.InRange(Seconds(row.Flow!, "humanWait", "operatorDecision") / 60, 106.3, 106.5);
        Assert.Equal(7_502_399, row.EpisodeTokens);
        Assert.Equal(4.2613m, row.EpisodeCostUsd);
        Assert.Equal(0, row.EpisodeUnpricedCalls);
        Assert.Equal(secondEnd, row.MachineDoneAt);
        Assert.Contains(row.Segments!, s => s.Step == "reviewAspects" && s.Executor == "review-host");
        AssertFlowPartition(row.Flow!);
        var results = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(results))
        {
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "golden-agt-3095.json"),
                System.Text.Json.JsonSerializer.Serialize(row,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }
        var list = row with { Flow = row.Flow!.WithoutSegments() };
        Assert.Empty(list.Segments!);
        Assert.Equal(row.MachineCycleSeconds, list.MachineCycleSeconds);
        Assert.Equal(row.EpisodeTokens, list.EpisodeTokens);
        Assert.Equal(row.LargestResource, list.LargestResource);
    }

    [Fact]
    public void CompletionRule_ExcludesReAcceptance_AndKeepsRework()
    {
        var rows = new[]
        {
            Lane(T0.AddHours(1), TaskStates.HumanReview, TaskStates.Completed),
            Lane(T0.AddHours(2), TaskStates.Completed, TaskStates.HumanReview),
            Lane(T0.AddHours(3), TaskStates.HumanReview, TaskStates.Completed),
            Lane(T0.AddHours(4), TaskStates.Completed, TaskStates.Ready),
            Lane(T0.AddHours(5), TaskStates.Ready, TaskStates.Progress),
            Lane(T0.AddHours(6), TaskStates.Progress, TaskStates.AutoReview),
            Lane(T0.AddHours(7), TaskStates.AutoReview, TaskStates.Completed),
        };
        var anchors = CompletionAnchors.Find(rows);
        Assert.Equal(new[] { "first", "after-rework" }, anchors.Real.Select(a => a.Kind));
        Assert.Equal(T0.AddHours(7), anchors.LastReal?.At);
        Assert.Equal(new[] { T0.AddHours(3) }, anchors.Artefacts);

        var task = Task("rework", TaskStates.Completed);
        var row = TaskCycleTimeAnalyzer.Analyze(task,
            [Created(T0, TaskStates.HumanReview), .. rows], null).Row!;
        Assert.Equal("after-rework", row.CompletionKind);
        Assert.Equal(1, row.ReAcceptanceArtefacts);
        Assert.Equal(2 * 3600, row.Stages!.CompletedBeforeReopen);
        Assert.Equal(row.LeadTimeSeconds, row.Stages.Sum, 1);
        Assert.Equal(T0.AddHours(4), row.Flow?.Start);
        AssertFlowPartition(row.Flow!);
    }

    [Fact]
    public void ReviewClaim_SplitsSlotWorkAndMachineTail()
    {
        var events = new[]
        {
            Created(T0, TaskStates.AutoReview),
            Claim(T0.AddMinutes(10), "review_one"),
            Step(T0.AddMinutes(20), "review_one", "aspect-code-quality", 10 * 60_000),
            Lane(T0.AddMinutes(30), TaskStates.AutoReview, TaskStates.HumanReview, "system"),
            Lane(T0.AddMinutes(40), TaskStates.HumanReview, TaskStates.Completed, "human:operator"),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(Task("claim", TaskStates.Completed), events, null).Row!;
        Assert.Equal(10 * 60, Seconds(row.Flow!, "resourceWait", "reviewSlot"));
        Assert.Equal(10 * 60, Seconds(row.Flow!, "systemHold", "machineTail"));
        Assert.Equal(10 * 60, Seconds(row.Flow!, "active", "reviewAspects"));
        AssertFlowPartition(row.Flow!);
    }

    [Fact]
    public void RetryAndCauseBreaker_AreSystemHolds()
    {
        var events = new[]
        {
            Created(T0, TaskStates.AutoReview),
            Claim(T0.AddMinutes(10), "review_one"),
            new TimelineEvent { Ts = T0.AddMinutes(15), Kind = TimelineEventKinds.ReviewInfrastructureRetryScheduled,
                RunId = "review_one", Details = new() { ["attemptId"] = "review_one", ["dueAtUtc"] = T0.AddMinutes(25).ToString("O") } },
            new TimelineEvent { Ts = T0.AddMinutes(25), Kind = TimelineEventKinds.CauseBreakerWaiting },
            Claim(T0.AddMinutes(35), "review_two"),
            Step(T0.AddMinutes(40), "review_two", "aspect-code-quality", 5 * 60_000),
            Lane(T0.AddMinutes(41), TaskStates.AutoReview, TaskStates.Completed),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(Task("holds", TaskStates.Completed), events, null).Row!;
        Assert.Equal(10 * 60, Seconds(row.Flow!, "systemHold", "infraRetryBackoff"));
        Assert.Equal(10 * 60, Seconds(row.Flow!, "systemHold", "causeBreaker"));
        Assert.Equal(2, row.ReviewAttempts);
        Assert.Equal(1, row.ReviewAttemptsWithoutVerdict);
        AssertFlowPartition(row.Flow!);
    }

    [Fact]
    public void LiveReviewAttempt_CreatesNoAttemptAndLeaseExpiryHolds()
    {
        var subject = new ReviewSubjectDto("subject", "repo", "sha", "run", "requirements", "policy", [], T0);
        var lease = new AttemptLeaseDto("lease", 1, 1, "review-host", "TUFI2",
            T0.AddMinutes(10), T0.AddMinutes(30), T0.AddMinutes(20));
        var authority = new ReviewAttemptDto("review_one", "CARD", "repo", "run", null, subject,
            AttemptLifecycleState.Failed, lease, 1, 1, T0.AddMinutes(5), T0.AddMinutes(30),
            ReviewTerminalOutcome.InfrastructureFailure, "LeaseExpired", null, null, []);
        var events = new[]
        {
            Created(T0, TaskStates.AutoReview),
            Claim(T0.AddMinutes(10), "review_one"),
            new TimelineEvent { Ts = T0.AddMinutes(30), Kind = TimelineEventKinds.ReviewInfrastructureRetryScheduled,
                RunId = "review_one", Details = new() { ["attemptId"] = "review_one",
                    ["dueAtUtc"] = T0.AddMinutes(35).ToString("O") } },
            Lane(T0.AddMinutes(40), TaskStates.AutoReview, TaskStates.Completed),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(Task("lease", TaskStates.Completed), events, null,
            reviewAuthority: [authority]).Row!;
        Assert.Equal(5 * 60, Seconds(row.Flow!, "systemHold", "noReviewAttempt"));
        Assert.Equal(10 * 60, Seconds(row.Flow!, "systemHold", "leaseExpiredTail"));
        Assert.Equal(10 * 60, Seconds(row.Flow!, "active", "reviewFailedAttempt"));
        AssertFlowPartition(row.Flow!);
    }

    [Fact]
    public void DependencyWait_EndsAtTargetCompletion_ReleaseOrEdgeRemoval()
    {
        var dependent = Task("dependent", TaskStates.Completed) with
        {
            References = new TaskReferences { DependsOn = [new TaskDependencyReference("TARGET", true)] },
        };
        var target = Task("target", TaskStates.Completed) with { Key = "TARGET" };
        var own = new[]
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(40), TaskStates.Ready, TaskStates.Progress),
        };
        var other = new[]
        {
            Created(T0, TaskStates.Progress),
            Lane(T0.AddMinutes(20), TaskStates.Progress, TaskStates.Completed),
            new TimelineEvent { Ts = T0.AddMinutes(30), Kind = TimelineEventKinds.TaskReleased },
        };
        var spans = DependencyFlowEvidence.Build(dependent, own,
            key => key == "TARGET" ? (target, other) : null);
        Assert.Equal(30 * 60, spans.Sum(s => s.Seconds));

        var withoutRelease = dependent with { References = new TaskReferences
            { DependsOn = [new TaskDependencyReference("TARGET")] } };
        var regular = DependencyFlowEvidence.Build(withoutRelease, own,
            key => key == "TARGET" ? (target, other) : null);
        Assert.Equal(20 * 60, regular.Sum(s => s.Seconds));

        var removed = dependent with { References = new TaskReferences() };
        var withRemoval = new[]
        {
            Created(T0, TaskStates.Ready),
            new TimelineEvent { Ts = T0.AddMinutes(15), Kind = TimelineEventKinds.DependencyChanged,
                Details = new() { ["added"] = "", ["removed"] = "TARGET" } },
            Lane(T0.AddMinutes(40), TaskStates.Ready, TaskStates.Progress),
        };
        var shortened = DependencyFlowEvidence.Build(removed, withRemoval,
            key => key == "TARGET" ? (target, other) : null);
        Assert.Equal(15 * 60, shortened.Sum(s => s.Seconds));
    }

    [Fact]
    public void LocalGateHeader_IsBounded_AndMalformedHeaderIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "cycle-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "post-steps");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "build-test-gate-1.log"),
                $"durationMs=0 startedAtUtc={T0:O} queueWaitMs=5880001\n" + new string('x', 8000));
            File.WriteAllText(Path.Combine(folder, "build-test-gate-2.log"), "bad header");
            var result = FlowGateLogEvidence.Read(root);
            Assert.True(result.Malformed);
            Assert.Single(result.Segments);
            Assert.Equal("gateHost", result.Segments[0].Resource);
            Assert.Equal(5880.001, result.Segments[0].Seconds, 3);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Tokens_KeepStoredCostAndExcludeCallsBeforeEpisode()
    {
        var events = new[]
        {
            Created(T0, TaskStates.Backlog),
            Lane(T0.AddMinutes(1), TaskStates.Backlog, TaskStates.Ready),
            Lane(T0.AddMinutes(2), TaskStates.Ready, TaskStates.Progress, "remote-runner:r", "claimed", "run_1"),
            Lane(T0.AddMinutes(5), TaskStates.Progress, TaskStates.AutoReview, "remote-runner:r", "delivered"),
            Lane(T0.AddMinutes(10), TaskStates.AutoReview, TaskStates.Completed),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(Task("tokens", TaskStates.Completed), events, null).Row!;
        var calls = new TaskTokenSummary { Entries =
        [
            new TaskTokenCall { Ts = T0, InputTokens = 100, EstimatedApiCostUsd = 1 },
            new TaskTokenCall { Ts = T0.AddMinutes(3), ParticipantId = "agent:remote-runner:run_1:usage:scope",
                InputTokens = 2_000_000, CacheReadTokens = 5_000_000, EstimatedApiCostUsd = 999m, ModelPriced = false },
            new TaskTokenCall { Ts = T0.AddMinutes(4), ParticipantId = "agent:remote-runner:run_1:usage:scope",
                OutputTokens = 100, EstimatedApiCostUsd = 3.29m, ModelPriced = true },
        ] };
        var attributed = FlowTokenAttribution.Apply(row, calls, events);
        Assert.Equal(7_000_100, attributed.EpisodeTokens);
        Assert.Equal(7_000_200, attributed.CardTokens);
        Assert.Equal(3.29m, attributed.EpisodeCostUsd);
        Assert.Equal(1, attributed.EpisodeUnpricedCalls);
        Assert.Contains(attributed.Segments!, s => s.Step == "coding" && s.Tokens == 7_000_100);
    }

    [Fact]
    public void Integration_AndHumanTail_UseTheirOwnClasses()
    {
        var events = new[]
        {
            Created(T0, TaskStates.AutoReview),
            Claim(T0.AddMinutes(5), "review_a"),
            Step(T0.AddMinutes(10), "review_a", "aspect-code-quality", 5 * 60_000),
            new TimelineEvent { Ts = T0.AddMinutes(20), Kind = TimelineEventKinds.IntegrationStarted },
            new TimelineEvent { Ts = T0.AddMinutes(30), Kind = TimelineEventKinds.IntegrationSucceeded,
                Details = new() { ["outcome"] = "Merged" } },
            Lane(T0.AddMinutes(40), TaskStates.AutoReview, TaskStates.Escalated),
            Lane(T0.AddMinutes(50), TaskStates.Escalated, TaskStates.HumanReview, "human:operator"),
            Lane(T0.AddMinutes(60), TaskStates.HumanReview, TaskStates.Completed, "human:operator"),
        };
        var flow = TaskCycleTimeAnalyzer.Analyze(Task("integration", TaskStates.Completed), events, null).Row!.Flow!;
        Assert.Equal(10 * 60, Seconds(flow, "resourceWait", "integrationLane"));
        Assert.Equal(10 * 60, Seconds(flow, "active", "integration"));
        Assert.Equal(10 * 60, Seconds(flow, "systemHold", "machineTail"));
        Assert.Equal(10 * 60, Seconds(flow, "humanWait", "escalated"));
        Assert.Equal(10 * 60, Seconds(flow, "humanWait", "humanReview"));
        AssertFlowPartition(flow);
    }

    [Fact]
    public void NoDeliveryCodingStay_AndUnclaimedHumanAcceptance()
    {
        var events = new[]
        {
            Created(T0, TaskStates.Ready),
            Lane(T0.AddMinutes(2), TaskStates.Ready, TaskStates.Progress),
            Lane(T0.AddMinutes(5), TaskStates.Progress, TaskStates.Ready, cause: "lease-recovery"),
            Lane(T0.AddMinutes(6), TaskStates.Ready, TaskStates.Progress),
            Lane(T0.AddMinutes(10), TaskStates.Progress, TaskStates.AutoReview, cause: "delivered"),
            Lane(T0.AddMinutes(20), TaskStates.AutoReview, TaskStates.Completed, "human:operator"),
        };
        var flow = TaskCycleTimeAnalyzer.Analyze(Task("queued", TaskStates.Completed), events, null).Row!.Flow!;
        Assert.Equal(3 * 60, Seconds(flow, "active", "codingNoDelivery"));
        Assert.Equal(4 * 60, Seconds(flow, "active", "coding"));
        Assert.Equal(10 * 60, Seconds(flow, "resourceWait", "reviewSlot"));
        Assert.Equal(0, Seconds(flow, "systemHold", "noReviewAttempt"));
        AssertFlowPartition(flow);
    }

    [Fact]
    public void DailyThroughput_UsesDifferentDaysForMachineAndAcceptance()
    {
        var d1 = T0;
        var d2 = T0.AddDays(1);
        var events = new[]
        {
            Created(d1, TaskStates.Progress),
            Lane(d1.AddMinutes(10), TaskStates.Progress, TaskStates.AutoReview, cause: "delivered"),
            Claim(d1.AddMinutes(12), "review_a"),
            Step(d1.AddMinutes(15), "review_a", "aspect-code-quality", 3 * 60_000),
            Lane(d2.AddMinutes(10), TaskStates.AutoReview, TaskStates.Completed, "human:operator"),
            Lane(d2.AddMinutes(20), TaskStates.Completed, TaskStates.HumanReview),
            Lane(d2.AddMinutes(30), TaskStates.HumanReview, TaskStates.Completed, "human:operator"),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(Task("daily", TaskStates.Completed), events, null).Row!;
        var analysis = new TaskCycleAnalysis(row, null) { Deliveries = [d1.AddMinutes(10)] };
        var days = FlowAggregates.BuildThroughputDays([analysis], d2.AddHours(1), d1);
        Assert.Equal((1, 1, 0, 0), (days[0].Delivered, days[0].MachineDone,
            days[0].Completed, days[0].ReAcceptanceArtefacts));
        Assert.Equal((0, 0, 1, 1), (days[1].Delivered, days[1].MachineDone,
            days[1].Completed, days[1].ReAcceptanceArtefacts));
    }

    [Theory]
    [InlineData("delivery-gate-failed")]
    [InlineData("OperatorOverride")]
    [InlineData("failed")]
    public void IntegrationOutcomeAfterRework_DoesNotReuseOldStart(string outcome)
    {
        var events = new[]
        {
            Created(T0, TaskStates.AutoReview),
            new TimelineEvent { Ts = T0.AddMinutes(1), Kind = TimelineEventKinds.IntegrationStarted },
            new TimelineEvent { Ts = T0.AddMinutes(2), Kind = TimelineEventKinds.IntegrationFailed,
                Details = new() { ["outcome"] = "failed" } },
            Lane(T0.AddMinutes(3), TaskStates.AutoReview, TaskStates.Ready),
            Lane(T0.AddMinutes(5), TaskStates.Ready, TaskStates.Progress, cause: "claimed"),
            Lane(T0.AddMinutes(10), TaskStates.Progress, TaskStates.AutoReview, cause: "delivered"),
            new TimelineEvent { Ts = T0.AddMinutes(12), Kind = TimelineEventKinds.IntegrationFailed,
                Details = new() { ["outcome"] = outcome } },
            Lane(T0.AddMinutes(15), TaskStates.AutoReview, TaskStates.Completed, "human:operator"),
        };
        var row = TaskCycleTimeAnalyzer.Analyze(Task("rework-integration", TaskStates.Completed), events, null).Row!;
        Assert.Equal(60, Seconds(row.Flow!, "active", "integration"));
        Assert.Equal(120, Seconds(row.Flow!, "resourceWait", "codingSlot"));
        Assert.Equal(300, Seconds(row.Flow!, "active", "coding"));
        Assert.Equal(row.LeadTimeSeconds, row.Stages!.Sum, 1);
        AssertFlowPartition(row.Flow!);
    }

    [Theory]
    [InlineData(false, "Merged", 60)]
    [InlineData(false, "delivery-gate-failed", 0)]
    [InlineData(true, "Merged", 0)]
    public void IntegrationPairing_SkipsRepeatsGateFailuresAndStartsInOtherStays(
        bool moveBeforeOutcome, string outcome, int seconds)
    {
        var events = new List<TimelineEvent>
        {
            Created(T0, TaskStates.AutoReview),
            new() { Ts = T0.AddMinutes(1), Kind = TimelineEventKinds.IntegrationStarted },
        };
        if (moveBeforeOutcome)
            events.Add(Lane(T0.AddMinutes(1.5), TaskStates.AutoReview, TaskStates.HumanReview));
        events.Add(new TimelineEvent { Ts = T0.AddMinutes(2), Kind = TimelineEventKinds.IntegrationSucceeded,
            Details = new() { ["outcome"] = outcome } });
        events.Add(new TimelineEvent { Ts = T0.AddMinutes(3), Kind = TimelineEventKinds.IntegrationSucceeded,
            Details = new() { ["outcome"] = outcome } });
        events.Add(Lane(T0.AddMinutes(5), moveBeforeOutcome ? TaskStates.HumanReview : TaskStates.AutoReview,
            TaskStates.Completed, "human:operator"));
        var row = TaskCycleTimeAnalyzer.Analyze(Task("pairing", TaskStates.Completed), events, null).Row!;
        Assert.Equal(seconds, Seconds(row.Flow!, "active", "integration"));
        AssertFlowPartition(row.Flow!);
    }

    private static void AssertFlowPartition(FlowEpisode flow)
    {
        Assert.Equal(flow.EpisodeSeconds, flow.Segments.Sum(s => s.Seconds), 1);
        Assert.Equal(flow.EpisodeSeconds, flow.ActiveSeconds + flow.ResourceWaitSeconds
            + flow.SystemHoldSeconds + flow.HumanWaitSeconds + flow.DependencyWaitSeconds, 1);
        Assert.Equal(flow.MachineCycleSeconds, flow.ActiveSeconds + flow.ResourceWaitSeconds, 1);
    }

    private static double Seconds(FlowEpisode flow, string @class, string id) => flow.Segments
        .Where(s => s.Class == @class && (s.Step ?? s.Resource ?? s.Kind) == id).Sum(s => s.Seconds);

    private static TaskInfo Task(string id, string state) => new()
    {
        Id = id, Key = id.ToUpperInvariant(), TaskKey = id.ToUpperInvariant(), State = state,
        Title = id, CreatedAt = T0, EnteredLaneAt = T0.AddHours(8),
    };

    private static TimelineEvent Created(DateTime at, string lane) => new()
    { Ts = at, Kind = TimelineEventKinds.PromptCreated, Details = new() { ["targetState"] = lane } };

    private static TimelineEvent Lane(DateTime at, string from, string to, string actor = "system",
        string? cause = null, string? attempt = null) => new()
    {
        Ts = at, Kind = TimelineEventKinds.LaneChanged, Actor = actor,
        Details = new() { ["from"] = from, ["to"] = to,
            ["cause"] = cause ?? "operator-move", ["attemptId"] = attempt ?? "" },
    };

    private static TimelineEvent Claim(DateTime at, string id) => new()
    { Ts = at, Kind = TimelineEventKinds.ReviewAttemptClaimed, RunId = id,
        Details = new() { ["attemptId"] = id, ["executorId"] = "review-host" } };

    private static TimelineEvent Step(DateTime at, string id, string step, long ms) => new()
    { Ts = at, Kind = TimelineEventKinds.PostStepFinished, RunId = id,
        Details = new() { ["attemptId"] = id, ["pipelineStepId"] = step, ["durationMs"] = ms.ToString() } };

    private static FlowSegment Segment(string @class, string? step, string? resource,
        DateTime start, DateTime end) => new(@class, step, resource, null,
            start, end, (end - start).TotalSeconds);
}
