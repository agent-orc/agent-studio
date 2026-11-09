using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Lane transition extraction and classification from the ledger, plus the
/// project-level matrix, dwell, bounce-cause, and loop aggregation.
/// </summary>
public sealed class CycleTimeLaneTransitionTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Extract_ClassifiesForwardBackwardAndManualMoves_WithDwellAndRework()
    {
        var claim = T0.AddMinutes(10);
        var delivered = claim.AddMinutes(30);
        var reopened = delivered.AddMinutes(5);
        var claim2 = reopened.AddMinutes(2);
        var leaseLost = claim2.AddMinutes(3);
        var claim3 = leaseLost.AddSeconds(20);
        var delivered2 = claim3.AddMinutes(20);
        var toHuman = delivered2.AddMinutes(15);
        var requeue = toHuman.AddHours(2);
        var claim4 = requeue.AddMinutes(1);
        var delivered3 = claim4.AddMinutes(10);
        var escalated = delivered3.AddMinutes(5);
        var decided = escalated.AddHours(1);
        var completed = decided.AddMinutes(30);
        var archived = completed.AddHours(3);

        var ledger = new List<TimelineEvent>
        {
            Event(T0, TimelineEventKinds.PromptCreated, new() { ["targetState"] = TaskStates.Ready }),
            Lane(claim, TaskStates.Ready, TaskStates.Progress, "remote-runner:agent-runner-01", new() { ["attemptId"] = "run_1" }),
            Lane(delivered, TaskStates.Progress, TaskStates.AutoReview, "remote-runner-completion:agent-runner-01"),
            Lane(reopened, TaskStates.AutoReview, TaskStates.Ready, "system"),
            Event(reopened.AddMilliseconds(400), TimelineEventKinds.QualityLoopReopened, new() { ["cause"] = "build-test-gate-fail" }),
            Lane(claim2, TaskStates.Ready, TaskStates.Progress, "remote-runner:agent-runner-01"),
            Lane(leaseLost, TaskStates.Progress, TaskStates.Ready, "remote-runner-lease-recovery:agent-runner-01"),
            Lane(claim3, TaskStates.Ready, TaskStates.Progress, "remote-runner:agent-runner-01"),
            Lane(delivered2, TaskStates.Progress, TaskStates.AutoReview, "remote-runner-completion:agent-runner-01"),
            Event(toHuman.AddSeconds(-1), TimelineEventKinds.IntegrationFailed, new() { ["outcome"] = "delivery-gate-failed" }),
            Lane(toHuman, TaskStates.AutoReview, TaskStates.HumanReview, "remote-review:review_1"),
            Lane(requeue, TaskStates.HumanReview, TaskStates.Ready, "human:operator"),
            Event(requeue.AddSeconds(1), TimelineEventKinds.OperatorRequeued, new() { ["reason"] = "Fresh assessment." }),
            Lane(claim4, TaskStates.Ready, TaskStates.Progress, "remote-runner:agent-runner-01"),
            Lane(delivered3, TaskStates.Progress, TaskStates.AutoReview, "remote-runner-completion:agent-runner-01"),
            Lane(escalated, TaskStates.AutoReview, TaskStates.Escalated, "system"),
            Event(escalated.AddSeconds(1), TimelineEventKinds.OrchestratorEscalated, null, "gate failed twice"),
            Lane(decided, TaskStates.Escalated, TaskStates.HumanReview, "human:operator"),
            Lane(completed, TaskStates.HumanReview, TaskStates.Completed, "human:operator"),
            Lane(archived, TaskStates.Completed, TaskStates.Archive, "human:operator"),
        };

        var transitions = LaneTransitionExtractor.Extract(ledger.OrderBy(e => e.Ts).ToList(), T0);

        Assert.Equal(15, transitions.Count);
        Assert.Equal(
            new[]
            {
                TransitionCauses.Claimed, TransitionCauses.Delivered, TransitionCauses.GateFailure,
                TransitionCauses.Claimed, TransitionCauses.LeaseRecovery, TransitionCauses.Claimed,
                TransitionCauses.Delivered, TransitionCauses.ReviewVerdict, TransitionCauses.OperatorRequeue,
                TransitionCauses.Claimed, TransitionCauses.Delivered, TransitionCauses.Escalated,
                TransitionCauses.OperatorDecision, TransitionCauses.Accepted, TransitionCauses.Archived,
            },
            transitions.Select(t => t.Cause));
        Assert.Equal(
            new[] { "forward", "forward", "backward", "forward", "backward", "forward", "forward", "forward", "backward", "forward", "forward", "forward", "lateral", "forward", "forward" },
            transitions.Select(t => t.Direction));

        var first = transitions[0];
        Assert.Equal(600, first.DwellSeconds);
        Assert.Equal("runner", first.ActorKind);
        Assert.Equal("run_1", first.AttemptId);

        var gate = transitions[2];
        Assert.Equal("build-test-gate-fail", gate.CauseDetail);
        Assert.Equal(5 * 60, gate.DwellSeconds);
        Assert.Equal((delivered2 - reopened).TotalSeconds, gate.ReworkSeconds);
        Assert.Equal("system", gate.ActorKind);

        var lease = transitions[4];
        Assert.Equal("runner", lease.ActorKind);
        Assert.Equal((claim3 - leaseLost).TotalSeconds, lease.ReworkSeconds);

        var verdict = transitions[7];
        Assert.Equal("review", verdict.ActorKind);
        Assert.Equal("delivery-gate-failed", verdict.CauseDetail);

        var operatorRequeue = transitions[8];
        Assert.Equal("human", operatorRequeue.ActorKind);
        Assert.Equal("Fresh assessment.", operatorRequeue.CauseDetail);
        Assert.Equal(2 * 3600, operatorRequeue.DwellSeconds);
        Assert.Equal((escalated - requeue).TotalSeconds, operatorRequeue.ReworkSeconds);

        Assert.Equal("gate failed twice", transitions[11].CauseDetail);
        Assert.Null(transitions[12].ReworkSeconds);
        Assert.Equal(3 * 3600, transitions[14].DwellSeconds);
    }

    [Fact]
    public void Extract_ClassifiesIntegrationRecovery_EscalationRequeue_CompletedReopen_AndUnknownCauses()
    {
        var a = T0.AddHours(1);
        var ledger = new List<TimelineEvent>
        {
            Event(T0, TimelineEventKinds.PromptCreated, new() { ["targetState"] = TaskStates.AutoReview }),
            Event(a.AddSeconds(-2), TimelineEventKinds.IntegrationFailed, new() { ["outcome"] = "AgentRoundRequired" }),
            Event(a.AddSeconds(-1), TimelineEventKinds.IntegrationRecoveryQueued, new() { ["automatic"] = "true" }),
            Lane(a, TaskStates.AutoReview, TaskStates.Ready, "system"),
            Lane(a.AddMinutes(1), TaskStates.Ready, TaskStates.Progress, "remote-runner:r"),
            Lane(a.AddMinutes(5), TaskStates.Progress, TaskStates.Escalated, "system"),
            Lane(a.AddMinutes(30), TaskStates.Escalated, TaskStates.Ready, "human:op", new() { ["reason"] = "Infrastructure repaired" }),
            Lane(a.AddMinutes(31), TaskStates.Ready, TaskStates.Progress, "remote-runner:r"),
            Lane(a.AddMinutes(40), TaskStates.Progress, TaskStates.HumanReview, "human:runner-host"),
            Lane(a.AddMinutes(50), TaskStates.HumanReview, TaskStates.Completed, "human:op"),
            Lane(a.AddMinutes(55), TaskStates.Completed, TaskStates.HumanReview, "system"),
            Event(a.AddMinutes(55).AddSeconds(1), TimelineEventKinds.IntegrationFailed, new() { ["outcome"] = "GateFailed" }),
            Lane(a.AddMinutes(70), TaskStates.HumanReview, TaskStates.Completed, "human:op"),
            Lane(a.AddMinutes(80), TaskStates.Completed, TaskStates.Backlog, "human:op", new() { ["reason"] = "Reopen for follow-up" }),
            Lane(a.AddMinutes(85), TaskStates.Backlog, TaskStates.Ready, "human:op"),
            Lane(a.AddMinutes(86), TaskStates.Ready, TaskStates.Progress, "system"),
            Lane(a.AddMinutes(90), TaskStates.Progress, TaskStates.Ready, "system"),
            Event(a.AddMinutes(91), TimelineEventKinds.ReviewAttemptSuperseded, null),
            Lane(a.AddMinutes(91).AddSeconds(2), TaskStates.Ready, TaskStates.Progress, "system"),
            Lane(a.AddMinutes(95), TaskStates.Progress, TaskStates.Ready, "system"),
            Lane(a.AddMinutes(100), TaskStates.Ready, TaskStates.AutoReview, "remote-runner-completion:r"),
            Lane(a.AddMinutes(101), TaskStates.AutoReview, TaskStates.Preparation, "system"),
        };

        var transitions = LaneTransitionExtractor.Extract(ledger.OrderBy(e => e.Ts).ToList(), T0);
        var causes = transitions.ToDictionary(t => t.At, t => t);

        Assert.Equal(TransitionCauses.IntegrationRecovery, causes[a].Cause);
        Assert.Equal("AgentRoundRequired", causes[a].CauseDetail);
        Assert.Equal(TransitionCauses.Escalated, causes[a.AddMinutes(5)].Cause);
        Assert.Equal(TransitionCauses.EscalationRequeue, causes[a.AddMinutes(30)].Cause);
        Assert.Equal("Infrastructure repaired", causes[a.AddMinutes(30)].CauseDetail);
        Assert.Equal(TransitionCauses.OperatorMove, causes[a.AddMinutes(40)].Cause);
        Assert.Equal(TransitionCauses.Accepted, causes[a.AddMinutes(50)].Cause);
        Assert.Equal(TransitionCauses.AcceptanceIntegrationFailed, causes[a.AddMinutes(55)].Cause);
        Assert.Equal("GateFailed", causes[a.AddMinutes(55)].CauseDetail);
        Assert.Equal(TransitionCauses.CompletedReopen, causes[a.AddMinutes(80)].Cause);
        Assert.Equal(TransitionCauses.Promoted, causes[a.AddMinutes(85)].Cause);
        Assert.Equal(TransitionCauses.Claimed, causes[a.AddMinutes(86)].Cause);
        // A claimed task handed back by the system without any cause row is a runner requeue.
        Assert.Equal(TransitionCauses.RunnerRequeue, causes[a.AddMinutes(95)].Cause);
        // The fall-back next to a superseded review attempt is review infrastructure.
        Assert.Equal(TransitionCauses.ReviewInfrastructure, causes[a.AddMinutes(90)].Cause);
        // A delivery from Ready is still a delivery; a review-lane drop without a cause row stays unclassified.
        Assert.Equal(TransitionCauses.Delivered, causes[a.AddMinutes(100)].Cause);
        Assert.Equal(TransitionCauses.Unclassified, causes[a.AddMinutes(101)].Cause);
        Assert.Equal(7, transitions.Count(t => t.Direction == TransitionDirections.Backward));
    }

    [Fact]
    public void Extract_PrefersTheExplicitCauseOfTheRow_AndInfersOnlyForLegacyRows()
    {
        var a = T0.AddHours(1);
        var ledger = new List<TimelineEvent>
        {
            Event(T0, TimelineEventKinds.PromptCreated, new() { ["targetState"] = TaskStates.Ready }),
            // Explicit lease recovery with a qualifier: the inference alone would
            // call this system Progress->Ready move a runner requeue.
            Lane(a, TaskStates.Progress, TaskStates.Ready, "run-liveness-detector", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.LeaseRecovery,
                [LaneChangeCauses.DetailQualifierKey] = "run-liveness-process-lost",
            }),
            // The same move without the field stays a legacy row and is inferred.
            Lane(a.AddMinutes(1), TaskStates.Ready, TaskStates.Progress, "system"),
            Lane(a.AddMinutes(2), TaskStates.Progress, TaskStates.Ready, "run-liveness-detector"),
            // Explicit escalation with the category as qualifier; the nearby
            // orchestrator_escalated summary is no longer needed for the detail.
            Lane(a.AddMinutes(3), TaskStates.Ready, TaskStates.Progress, "remote-runner:r", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.Claimed,
            }),
            Lane(a.AddMinutes(10), TaskStates.Progress, TaskStates.Escalated, "system", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.Escalated,
                [LaneChangeCauses.DetailQualifierKey] = HumanReviewEscalationCategories.AgentBlocked,
                ["reason"] = "agent-blocked: The agent reported a blocking dependency.",
            }),
            Event(a.AddMinutes(10).AddSeconds(1), TimelineEventKinds.OrchestratorEscalated, null, "prose summary"),
            // Explicit operator requeue without qualifier or reason: the inference
            // agrees, so its neighbouring-row detail (the integration outcome) is kept.
            Lane(a.AddMinutes(30), TaskStates.Escalated, TaskStates.HumanReview, "human:op", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.OperatorDecision,
            }),
            Event(a.AddMinutes(40).AddSeconds(-2), TimelineEventKinds.IntegrationFailed, new() { ["outcome"] = "GateFailed" }),
            Lane(a.AddMinutes(40), TaskStates.HumanReview, TaskStates.Ready, "human:op", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.OperatorRequeue,
            }),
            // Explicit cause that disagrees with the inference: the inferred
            // detail (actor) is not mixed into the explicit cause.
            Lane(a.AddMinutes(41), TaskStates.Ready, TaskStates.Progress, "system"),
            Lane(a.AddMinutes(45), TaskStates.Progress, TaskStates.Ready, "system", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.ClaimEnvironmentRetry,
            }),
            // An id outside the closed vocabulary is ignored, never a new bucket.
            Lane(a.AddMinutes(46), TaskStates.Ready, TaskStates.Progress, "system", new()
            {
                [LaneChangeCauses.DetailKey] = "made-up-cause",
            }),
            Lane(a.AddMinutes(50), TaskStates.Progress, TaskStates.AutoReview, "remote-runner-completion:r", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.Delivered,
                [LaneChangeCauses.DetailQualifierKey] = "done",
            }),
            Lane(a.AddMinutes(55), TaskStates.AutoReview, TaskStates.Ready, "system", new()
            {
                [LaneChangeCauses.DetailKey] = LaneChangeCauses.GateFailure,
                [LaneChangeCauses.DetailQualifierKey] = "build-test-gate-fail",
            }),
        };

        var transitions = LaneTransitionExtractor.Extract(ledger.OrderBy(e => e.Ts).ToList(), T0);
        var byTime = transitions.ToDictionary(t => t.At, t => t);

        Assert.Equal(TransitionCauses.LeaseRecovery, byTime[a].Cause);
        Assert.Equal("run-liveness-process-lost", byTime[a].CauseDetail);
        Assert.Equal(TransitionCauses.RunnerRequeue, byTime[a.AddMinutes(2)].Cause);
        Assert.Equal(TransitionCauses.Claimed, byTime[a.AddMinutes(3)].Cause);
        Assert.Null(byTime[a.AddMinutes(3)].CauseDetail);
        Assert.Equal(TransitionCauses.Escalated, byTime[a.AddMinutes(10)].Cause);
        Assert.Equal(HumanReviewEscalationCategories.AgentBlocked, byTime[a.AddMinutes(10)].CauseDetail);
        Assert.Equal(TransitionCauses.OperatorDecision, byTime[a.AddMinutes(30)].Cause);
        Assert.Equal(TransitionCauses.OperatorRequeue, byTime[a.AddMinutes(40)].Cause);
        Assert.Equal("after integration GateFailed", byTime[a.AddMinutes(40)].CauseDetail);
        Assert.Equal(TransitionCauses.ClaimEnvironmentRetry, byTime[a.AddMinutes(45)].Cause);
        Assert.Null(byTime[a.AddMinutes(45)].CauseDetail);
        Assert.Equal(TransitionCauses.Claimed, byTime[a.AddMinutes(46)].Cause);
        Assert.Equal(TransitionCauses.Delivered, byTime[a.AddMinutes(50)].Cause);
        Assert.Equal("done", byTime[a.AddMinutes(50)].CauseDetail);
        Assert.Equal(TransitionCauses.GateFailure, byTime[a.AddMinutes(55)].Cause);
        Assert.Equal("build-test-gate-fail", byTime[a.AddMinutes(55)].CauseDetail);
        Assert.Null(LaneTransitionExtractor.ExplicitCause(ledger.Single(e => e.Ts == a.AddMinutes(46))));
        Assert.Equal(LaneChangeCauses.Delivered, LaneTransitionExtractor.ExplicitCause(ledger.Single(e => e.Ts == a.AddMinutes(50))));
    }

    [Fact]
    public void TransitionCauses_AreTheLedgerVocabulary()
    {
        static Dictionary<string, string> Constants(Type type) => type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && !f.Name.StartsWith("Detail", StringComparison.Ordinal))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        var analysis = Constants(typeof(TransitionCauses));
        var ledger = Constants(typeof(LaneChangeCauses));

        Assert.Equal(ledger.OrderBy(p => p.Key), analysis.OrderBy(p => p.Key));
        Assert.Equal(ledger.Values.OrderBy(v => v, StringComparer.Ordinal), LaneChangeCauses.All.OrderBy(v => v, StringComparer.Ordinal));
        // Every id has a human label; the fallback returns the id itself.
        Assert.All(analysis.Values, id => Assert.NotEqual(id, TransitionCauses.Label(id)));
    }

    [Fact]
    public void Extract_ToleratesMissingDetails_AndKeepsDwellUnknownWhenTheStayStartIsUnknown()
    {
        var ledger = new List<TimelineEvent>
        {
            new() { Ts = T0, Kind = TimelineEventKinds.LaneChanged, Actor = "system", Summary = "moved", Details = null },
            Lane(T0.AddMinutes(5), TaskStates.Ready, TaskStates.Progress, "remote-runner:r"),
        };

        var transitions = LaneTransitionExtractor.Extract(ledger, null);

        Assert.Equal(2, transitions.Count);
        Assert.Equal(string.Empty, transitions[0].From);
        Assert.Equal(TransitionDirections.Lateral, transitions[0].Direction);
        Assert.Null(transitions[0].DwellSeconds);
        Assert.Equal(TransitionCauses.SystemMove, transitions[0].Cause);
        Assert.Equal(300, transitions[1].DwellSeconds);
        Assert.Empty(LaneTransitionExtractor.Extract([], T0));
    }

    [Fact]
    public void Analyzer_CarriesTransitionsAndBackwardCount()
    {
        var task = new TaskInfo
        {
            Id = "t", TaskKey = "DEMO::t", Key = "DEM-1", Title = "t", State = TaskStates.Completed,
            CreatedAt = T0, EnteredLaneAt = T0.AddHours(2),
        };
        var ledger = new List<TimelineEvent>
        {
            Event(T0, TimelineEventKinds.PromptCreated, new() { ["targetState"] = TaskStates.Ready }),
            Lane(T0.AddMinutes(1), TaskStates.Ready, TaskStates.Progress, "remote-runner:r"),
            Lane(T0.AddMinutes(2), TaskStates.Progress, TaskStates.Ready, "remote-runner-lease-recovery:r"),
            Lane(T0.AddMinutes(3), TaskStates.Ready, TaskStates.Progress, "remote-runner:r"),
            Lane(T0.AddMinutes(30), TaskStates.Progress, TaskStates.AutoReview, "remote-runner-completion:r"),
            Lane(T0.AddMinutes(60), TaskStates.AutoReview, TaskStates.HumanReview, "remote-review:rev"),
            Lane(T0.AddHours(2), TaskStates.HumanReview, TaskStates.Completed, "human:op"),
            Lane(T0.AddHours(3), TaskStates.Completed, TaskStates.Archive, "human:op"),
        };

        var row = TaskCycleTimeAnalyzer.Analyze(task, ledger, null).Row;

        Assert.NotNull(row);
        Assert.Equal(1, row!.BackwardTransitions);
        Assert.Equal(0, row.BounceRounds); // lease recovery is a runner retry, not a review bounce
        Assert.NotNull(row.Transitions);
        Assert.Equal(7, row.Transitions!.Count);
        Assert.Equal(TransitionCauses.Archived, row.Transitions[^1].Cause);
    }

    [Fact]
    public void Aggregation_BuildsMatrix_LaneDwell_BounceCauses_AndTopLoops()
    {
        var rows = new List<TaskCycleTime>
        {
            RowWith("A", 7200,
            [
                Transition(T0, TaskStates.Ready, TaskStates.Progress, TransitionDirections.Forward, 600, TransitionCauses.Claimed, null, null),
                Transition(T0.AddMinutes(10), TaskStates.Progress, TaskStates.AutoReview, TransitionDirections.Forward, 600, TransitionCauses.Delivered, null, null),
                Transition(T0.AddMinutes(15), TaskStates.AutoReview, TaskStates.Ready, TransitionDirections.Backward, 300, TransitionCauses.GateFailure, "build-test-gate-fail", 900),
                Transition(T0.AddMinutes(16), TaskStates.Ready, TaskStates.Progress, TransitionDirections.Forward, 60, TransitionCauses.Claimed, null, null),
                Transition(T0.AddMinutes(30), TaskStates.Progress, TaskStates.AutoReview, TransitionDirections.Forward, 840, TransitionCauses.Delivered, null, null),
                Transition(T0.AddMinutes(40), TaskStates.AutoReview, TaskStates.Ready, TransitionDirections.Backward, 600, TransitionCauses.GateFailure, "build-test-gate-fail", 1500),
                Transition(T0.AddMinutes(41), TaskStates.Ready, TaskStates.Progress, TransitionDirections.Forward, 60, TransitionCauses.Claimed, null, null),
                Transition(T0.AddMinutes(65), TaskStates.Progress, TaskStates.AutoReview, TransitionDirections.Forward, 1440, TransitionCauses.Delivered, null, null),
                Transition(T0.AddMinutes(70), TaskStates.AutoReview, TaskStates.HumanReview, TransitionDirections.Forward, 300, TransitionCauses.ReviewVerdict, "Merged", null),
                Transition(T0.AddMinutes(120), TaskStates.HumanReview, TaskStates.Completed, TransitionDirections.Forward, 3000, TransitionCauses.Accepted, null, null),
            ]),
            RowWith("B", 3600,
            [
                Transition(T0, TaskStates.Ready, TaskStates.Progress, TransitionDirections.Forward, 120, TransitionCauses.Claimed, null, null),
                Transition(T0.AddMinutes(20), TaskStates.Progress, TaskStates.AutoReview, TransitionDirections.Forward, 1200, TransitionCauses.Delivered, null, null),
                Transition(T0.AddMinutes(25), TaskStates.AutoReview, TaskStates.HumanReview, TransitionDirections.Forward, 300, TransitionCauses.ReviewVerdict, "Merged", null),
                Transition(T0.AddMinutes(50), TaskStates.HumanReview, TaskStates.Ready, TransitionDirections.Backward, 1500, TransitionCauses.OperatorRequeue, "Fresh assessment.", null),
                Transition(T0.AddMinutes(60), TaskStates.Ready, TaskStates.Completed, TransitionDirections.Forward, 600, TransitionCauses.Accepted, null, null),
            ]),
            RowWith("C", 1800, []),
        };

        var summary = CycleTimeTransitionAggregation.Build(rows);

        Assert.Equal(15, summary.TotalTransitions);
        Assert.Equal(3, summary.BackwardTransitions);
        Assert.Equal(2, summary.TasksWithBackwardTransitions);
        Assert.Equal(
            new[] { TaskStates.Ready, TaskStates.Progress, TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Completed },
            summary.Lanes);

        var readyToProgress = summary.Cells.Single(c => c.From == TaskStates.Ready && c.To == TaskStates.Progress);
        Assert.Equal(4, readyToProgress.Count);
        Assert.Equal(TransitionDirections.Forward, readyToProgress.Direction);
        var reviewToReady = summary.Cells.Single(c => c.From == TaskStates.AutoReview && c.To == TaskStates.Ready);
        Assert.Equal(2, reviewToReady.Count);
        Assert.Equal(TransitionDirections.Backward, reviewToReady.Direction);
        Assert.Equal(summary.Cells.OrderBy(c => LaneOrder.CanonicalIndex(c.From)).ThenBy(c => LaneOrder.CanonicalIndex(c.To)).Select(c => (c.From, c.To)),
            summary.Cells.Select(c => (c.From, c.To)));

        var readyDwell = summary.LaneDwell.Single(d => d.Lane == TaskStates.Ready);
        Assert.Equal(5, readyDwell.Stays);
        Assert.Equal(120, readyDwell.P50Seconds);
        Assert.Equal(600, readyDwell.MaxSeconds);
        Assert.Equal(1440, readyDwell.TotalSeconds);

        var gate = summary.BounceCauses.Single(c => c.Cause == TransitionCauses.GateFailure);
        Assert.Equal(2, gate.Count);
        Assert.Equal(1, gate.Tasks);
        Assert.Equal(2, gate.ReworkKnown);
        Assert.Equal(1200, gate.ReworkP50Seconds);
        Assert.Equal(2400, gate.ReworkTotalSeconds);
        Assert.Equal("Build/test gate failed", gate.Label);
        Assert.Equal(("build-test-gate-fail", 2), (gate.Details[0].Outcome, gate.Details[0].Count));
        var requeue = summary.BounceCauses.Single(c => c.Cause == TransitionCauses.OperatorRequeue);
        Assert.Equal(0, requeue.ReworkKnown);
        Assert.Null(requeue.ReworkP50Seconds);
        Assert.Equal(TransitionCauses.GateFailure, summary.BounceCauses[0].Cause);

        Assert.Equal(new[] { "A", "B" }, summary.TopLoops.Select(l => l.TaskId));
        Assert.Equal(2, summary.TopLoops[0].BackwardTransitions);
        Assert.Equal((TransitionCauses.GateFailure, 2), (summary.TopLoops[0].Causes[0].Outcome, summary.TopLoops[0].Causes[0].Count));
    }

    [Fact]
    public void BuildResponse_StripsPerTaskTransitionsUnlessRequested_AndAlwaysCarriesTheSummary()
    {
        var row = RowWith("A", 3600,
        [
            Transition(T0, TaskStates.Ready, TaskStates.Progress, TransitionDirections.Forward, 60, TransitionCauses.Claimed, null, null),
            Transition(T0.AddMinutes(30), TaskStates.Progress, TaskStates.Ready, TransitionDirections.Backward, 1800, TransitionCauses.LeaseRecovery, null, 30),
        ]);
        var analyses = new List<TaskCycleAnalysis> { new(row, null) };
        var now = T0.AddDays(1);

        var plain = ProjectCycleTimeService.BuildResponse("Demo", null, null, "7d", now, now.AddDays(-7), analyses);
        Assert.Null(plain.Tasks[0].Transitions);
        Assert.Equal(1, plain.Tasks[0].BackwardTransitions);
        Assert.Equal(2, plain.Transitions.TotalTransitions);
        Assert.Single(plain.Transitions.BounceCauses);
        Assert.Equal(TransitionCauses.LeaseRecovery, plain.Transitions.BounceCauses[0].Cause);

        var detailed = ProjectCycleTimeService.BuildResponse("Demo", null, null, "7d", now, now.AddDays(-7), analyses, includeTransitions: true);
        Assert.NotNull(detailed.Tasks[0].Transitions);
        Assert.Equal(2, detailed.Tasks[0].Transitions!.Count);
    }

    [Fact]
    public void Aggregation_GroupsBackwardMovesIntoReviewGateLeaseRecoveryAndOther()
    {
        var rows = new[]
        {
            RowWith("A", 3600,
            [
                Transition(T0, TaskStates.AutoReview, TaskStates.Ready, TransitionDirections.Backward, 60, TransitionCauses.QualityLoop, "review-concerns", null),
                Transition(T0.AddMinutes(5), TaskStates.AutoReview, TaskStates.Ready, TransitionDirections.Backward, 60, TransitionCauses.GateFailure, "build-test-gate-fail", null),
            ]),
            RowWith("B", 3600,
            [
                Transition(T0, TaskStates.Progress, TaskStates.Ready, TransitionDirections.Backward, 60, TransitionCauses.LeaseRecovery, null, null),
                Transition(T0.AddMinutes(5), TaskStates.AutoReview, TaskStates.Ready, TransitionDirections.Backward, 60, TransitionCauses.IntegrationRecovery, "Conflict", null),
                Transition(T0.AddMinutes(9), TaskStates.HumanReview, TaskStates.Ready, TransitionDirections.Backward, 60, TransitionCauses.OperatorRequeue, null, null),
            ]),
            RowWith("C", 3600,
            [
                Transition(T0, TaskStates.Progress, TaskStates.Ready, TransitionDirections.Backward, 60, TransitionCauses.ReviewInfrastructure, null, null),
                Transition(T0.AddMinutes(1), TaskStates.Ready, TaskStates.Progress, TransitionDirections.Forward, 60, TransitionCauses.Claimed, null, null),
            ]),
        };

        var summary = CycleTimeTransitionAggregation.Build(rows);

        Assert.Equal(
            new[] { BackwardCauseGroups.Review, BackwardCauseGroups.Gate, BackwardCauseGroups.LeaseRecovery, BackwardCauseGroups.Other },
            summary.BackwardCauseGroups.Select(g => g.Group));
        Assert.Equal(new[] { 1, 2, 1, 2 }, summary.BackwardCauseGroups.Select(g => g.Count));
        var gate = summary.BackwardCauseGroups.Single(g => g.Group == BackwardCauseGroups.Gate);
        Assert.Equal(2, gate.Tasks);
        Assert.Equal(new[] { TransitionCauses.GateFailure, TransitionCauses.IntegrationRecovery }, gate.Causes.Select(c => c.Outcome));
        Assert.Equal(summary.BackwardTransitions, summary.BackwardCauseGroups.Sum(g => g.Count));
        // Only A moved back for a review cause; B and C fell back for other reasons only.
        Assert.Equal(2, summary.TasksWithoutReviewCause);
    }

    // ---- helpers ------------------------------------------------------------

    private static TimelineEvent Event(DateTime at, string kind, Dictionary<string, string>? details, string summary = "") => new()
    {
        Ts = at, Kind = kind, Actor = TimelineActors.System, Summary = summary, Details = details,
    };

    private static TimelineEvent Lane(DateTime at, string from, string to, string actor, Dictionary<string, string>? extra = null)
    {
        var details = new Dictionary<string, string> { ["from"] = from, ["to"] = to };
        if (extra is not null) foreach (var (k, v) in extra) details[k] = v;
        return new TimelineEvent { Ts = at, Kind = TimelineEventKinds.LaneChanged, Actor = actor, Summary = $"{from} -> {to}", Details = details };
    }

    private static TaskLaneTransition Transition(
        DateTime at, string from, string to, string direction, double dwell, string cause, string? detail, double? rework) =>
        new(at, from, to, direction, dwell, "system", "system", cause, detail, null, rework);

    private static TaskCycleTime RowWith(string id, double lead, IReadOnlyList<TaskLaneTransition> transitions) =>
        new(id, "DEM-" + id, id + " title", TaskStates.Archive, @"C:\demo", T0, T0.AddMinutes(1), T0.AddSeconds(lead), "ledger",
            new CycleTimeStageSeconds { Coding = lead }, 0, lead, lead - 60, 1, 1, 0, 0, null, null, [],
            transitions.Count(t => t.Direction == TransitionDirections.Backward), transitions);
}
