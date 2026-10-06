using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3011: the pure decision, trigger, budget, and health rules of the
/// operator sweeps, tested as a matrix without filesystem or DI.
/// </summary>
public sealed class OperatorSweepPolicyTests
{
    private static readonly FakeTimeProvider Clock = new(DateTimeOffset.Parse("2026-10-04T12:00:00Z"));
    private static readonly CardRoundBudgetState Fresh = new(0, 4);
    private static readonly OperatorSweepTrigger Trigger = new("review:r1", AlreadyHandled: false);

    private static OperatorSweepGuardFacts Facts(
        bool paused = false,
        bool continuations = true,
        bool activeReview = false,
        bool activeRun = false,
        CardRoundBudgetState? budget = null)
        => new(paused, continuations, activeReview, activeRun, budget ?? Fresh);

    [Fact]
    public void NoTrigger_IsIgnoredSilently()
    {
        var decision = OperatorSweepPolicy.Decide(OperatorSweepKinds.FixRounds, null, Facts(activeReview: true));
        Assert.Equal(OperatorSweepAction.Ignore, decision.Action);
    }

    [Theory]
    [InlineData(OperatorSweepKinds.FixRounds, OperatorSweepReasons.FreshProductFailure)]
    [InlineData(OperatorSweepKinds.Salvage, OperatorSweepReasons.SalvageAvailable)]
    public void FreshTrigger_WithBudget_Acts(string sweep, string reason)
    {
        var decision = OperatorSweepPolicy.Decide(sweep, Trigger, Facts());
        Assert.Equal(OperatorSweepAction.Act, decision.Action);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void ActiveReviewAttempt_HoldsBeforeEveryOtherGuard()
    {
        // Even a paused sweep with an exhausted budget names the review lease,
        // because that is the blocker an operator has to know about.
        var decision = OperatorSweepPolicy.Decide(
            OperatorSweepKinds.FixRounds,
            Trigger,
            Facts(paused: true, activeReview: true, budget: new CardRoundBudgetState(9, 4)));
        Assert.Equal(OperatorSweepAction.Hold, decision.Action);
        Assert.Equal(OperatorSweepReasons.ActiveReviewAttempt, decision.Reason);
    }

    [Theory]
    [InlineData(false, true, false, OperatorSweepReasons.ActiveRunAttempt)]
    [InlineData(true, false, false, OperatorSweepReasons.Paused)]
    [InlineData(false, false, true, OperatorSweepReasons.ProjectContinuationsDisabled)]
    public void Guards_Hold_WithTheirReason(bool paused, bool activeRun, bool continuationsDisabled, string reason)
    {
        var decision = OperatorSweepPolicy.Decide(
            OperatorSweepKinds.Salvage,
            Trigger,
            Facts(paused: paused, activeRun: activeRun, continuations: !continuationsDisabled));
        Assert.Equal(OperatorSweepAction.Hold, decision.Action);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void AlreadyHandledSubject_Holds()
    {
        var decision = OperatorSweepPolicy.Decide(
            OperatorSweepKinds.FixRounds, Trigger with { AlreadyHandled = true }, Facts());
        Assert.Equal(OperatorSweepAction.Hold, decision.Action);
        Assert.Equal(OperatorSweepReasons.AlreadyHandled, decision.Reason);
    }

    [Fact]
    public void ExhaustedBudget_WaitsForAPerson()
    {
        var decision = OperatorSweepPolicy.Decide(
            OperatorSweepKinds.FixRounds, Trigger, Facts(budget: new CardRoundBudgetState(4, 4)));
        Assert.Equal(OperatorSweepAction.WaitForPerson, decision.Action);
        Assert.Equal(OperatorSweepReasons.BudgetExhausted, decision.Reason);
    }

    [Theory]
    [InlineData(FailureDomains.Product, OperatorSweepAction.Act, OperatorSweepReasons.ProductGateFailure)]
    [InlineData(FailureDomains.Infrastructure, OperatorSweepAction.Hold, OperatorSweepReasons.EnvironmentFailure)]
    [InlineData(null, OperatorSweepAction.WaitForPerson, OperatorSweepReasons.UnclassifiedFailure)]
    public void GateTriage_RoutesByDomain(string? domain, OperatorSweepAction action, string reason)
    {
        var decision = OperatorSweepPolicy.Decide(
            OperatorSweepKinds.GateTriage,
            new OperatorSweepTrigger("gate:sha:pre-develop-build-gate", false, domain),
            Facts());
        Assert.Equal(action, decision.Action);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void FixRoundTrigger_RequiresHumanReviewAndAProductFailureForTheCurrentDelivery()
    {
        var review = new OperatorSweepReviewFacts("r1", ReviewTerminalOutcome.ProductFailure, Sha('a'));
        Assert.NotNull(OperatorSweepTriggers.FixRound(TaskStates.HumanReview, review, Sha('a'), []));
        Assert.Null(OperatorSweepTriggers.FixRound(TaskStates.Escalated, review, Sha('a'), []));
        Assert.Null(OperatorSweepTriggers.FixRound(TaskStates.HumanReview, review, Sha('b'), []));
        Assert.Null(OperatorSweepTriggers.FixRound(
            TaskStates.HumanReview, review with { Outcome = ReviewTerminalOutcome.Pass }, Sha('a'), []));
    }

    [Fact]
    public void Receipt_MarksOnlyItsOwnSweepAndSubjectHandled()
    {
        var receipt = new TimelineEvent
        {
            Kind = TimelineEventKinds.OperatorSweepRoundStarted,
            Details = new Dictionary<string, string>
            {
                [OperatorSweepTriggers.ReceiptSweepKey] = OperatorSweepKinds.FixRounds,
                [OperatorSweepTriggers.ReceiptSubjectKey] = "review:r1",
            },
        };
        var review = new OperatorSweepReviewFacts("r1", ReviewTerminalOutcome.ProductFailure, null);
        Assert.True(OperatorSweepTriggers.FixRound(TaskStates.HumanReview, review, null, [receipt])!.AlreadyHandled);
        Assert.False(OperatorSweepTriggers.FixRound(
            TaskStates.HumanReview, review with { AttemptId = "r2" }, null, [receipt])!.AlreadyHandled);
    }

    [Theory]
    [InlineData(true, Contract.RunFailureClass.Product, FailureDomains.Infrastructure)]
    [InlineData(false, Contract.RunFailureClass.Product, FailureDomains.Product)]
    [InlineData(false, Contract.RunFailureClass.Quota, FailureDomains.Infrastructure)]
    public void GateClassification_PrefersTheGateRunnerThenTheTaxonomy(
        bool environmentLadder, Contract.RunFailureClass failureClass, string expected)
    {
        var failure = Gate(environmentLadder, failureClass, AcceptedIntegrationFailureCodes.BuildGateFailed, "3 tests failed");
        Assert.Equal(expected, OperatorSweepTriggers.ClassifyGateFailure(failure));
    }

    [Fact]
    public void GateClassification_FallsBackToTheFailureInterventionPolicy()
    {
        var product = Gate(false, Contract.RunFailureClass.Unknown, "build-gate-failed", "Assert.Equal failed");
        var infra = Gate(false, Contract.RunFailureClass.Unknown, "build-gate-failed", "dotnet: command timeout");
        Assert.Equal(FailureDomains.Product, OperatorSweepTriggers.ClassifyGateFailure(product));
        Assert.Equal(FailureDomains.Infrastructure, OperatorSweepTriggers.ClassifyGateFailure(infra));
    }

    [Fact]
    public void GateTrigger_IgnoresNonGateFailures()
    {
        var conflict = Gate(false, Contract.RunFailureClass.Product, AcceptedIntegrationFailureCodes.MergeConflict, "conflict")
            with { StepId = "merge-into-develop" };
        Assert.Null(OperatorSweepTriggers.GateTriage(TaskStates.HumanReview, conflict, []));
    }

    [Fact]
    public void SalvageFacts_ComeFromTheLatestNonTerminalRunWithASalvagePair()
    {
        TimelineEvent Finished(string status, string? sha) => new()
        {
            Kind = TimelineEventKinds.AgentRunFinished,
            RunId = "run-1",
            Details = sha is null
                ? new Dictionary<string, string> { ["status"] = status }
                : new Dictionary<string, string>
                {
                    ["status"] = status,
                    ["salvageBranch"] = "salvage/AGT-1",
                    ["salvageCommitSha"] = sha,
                    ["reason"] = "Timeout",
                },
        };

        var salvage = OperatorSweepTriggers.ReadSalvage([Finished("unknown", Sha('c'))]);
        Assert.Equal(Sha('c'), salvage!.Salvage.CommitSha);
        Assert.Equal("Timeout", salvage.ReportedReason);
        Assert.Null(OperatorSweepTriggers.ReadSalvage([Finished("done", Sha('c'))]));
        Assert.Null(OperatorSweepTriggers.ReadSalvage([Finished("unknown", null)]));
        // Only the latest run counts: a later finished run supersedes the salvage.
        Assert.Null(OperatorSweepTriggers.ReadSalvage([Finished("unknown", Sha('c')), Finished("done", null)]));
    }

    [Fact]
    public void Budget_CountsEveryRoundOnceAndIgnoresEpochs()
    {
        var journal = new[]
        {
            Record("J1", ReviewDecisionKind.Reissue, epoch: 0),
            Record("J1", ReviewDecisionKind.Reissue, epoch: 0),
            Record("J1", ReviewDecisionKind.OperatorRequeue, epoch: 1),
            Record("J1", ReviewDecisionKind.Reissue, epoch: 1),
            Record("J1", ReviewDecisionKind.Escalate, epoch: 1),
            Record("OTHER", ReviewDecisionKind.Reissue, epoch: 0),
        };
        var timeline = new[]
        {
            Reopen("multi-aspect-block", "rev-1"),
            Reopen("multi-aspect-concern", "rev-2"),
            // Orchestrator reopen events carry no reviewAttemptId: their journal record already counted.
            Reopen("noop-recovery", null),
            new TimelineEvent
            {
                Kind = TimelineEventKinds.ContinuationRoundStarted,
                Details = new Dictionary<string, string> { ["automatic"] = "true" },
            },
        };

        Assert.Equal(6, CardRoundBudget.CountRoundsUsed(journal, timeline, "J1"));
        // The orchestrator's own epoch-scoped count sees only one of them.
        Assert.Equal(1, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(journal, "J1"));
        var state = CardRoundBudget.Evaluate(journal, timeline, "J1", allowed: 4);
        Assert.True(state.Exhausted);
        Assert.Equal(0, state.Remaining);
    }

    [Fact]
    public void Health_IsAlarmWhenASweepFailedOrIsOverdue()
    {
        var options = new OperatorSweepOptions(true, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), 4);
        var start = Clock.GetUtcNow().UtcDateTime;
        Assert.False(OperatorSweepHealthPolicy.IsOverdue(null, start, options, start.AddMinutes(20)));
        Assert.True(OperatorSweepHealthPolicy.IsOverdue(null, start, options, start.AddMinutes(22)));
        Assert.True(OperatorSweepHealthPolicy.IsOverdue(start, start, options, start.AddMinutes(21)));

        OperatorSweepStatus Status(bool paused, string? error = null, bool overdue = false)
            => new("fix-rounds", paused, null, null, null, null, null, error, overdue, 0, 0, 0, []);
        Assert.Equal(OperatorSweepHealthPolicy.Healthy, OperatorSweepHealthPolicy.Status(true, [Status(false)]));
        Assert.Equal(OperatorSweepHealthPolicy.Paused, OperatorSweepHealthPolicy.Status(true, [Status(true)]));
        Assert.Equal(OperatorSweepHealthPolicy.Alarm, OperatorSweepHealthPolicy.Status(true, [Status(false, "boom")]));
        Assert.Equal(OperatorSweepHealthPolicy.Alarm, OperatorSweepHealthPolicy.Status(true, [Status(true, overdue: true)]));
        Assert.Equal(OperatorSweepHealthPolicy.Disabled, OperatorSweepHealthPolicy.Status(false, [Status(false, "boom")]));
    }

    [Fact]
    public void FixRoundPrompt_CarriesFindingsCommandsAndTheBudgetLine()
    {
        var report = new Contract.ReviewReportRequest(
            "exec", "inst", "lease", 1, "idem", "ProductFailure", "tests", "Two tests fail.",
            null!, null!,
            [
                new Contract.ReviewCommandEvidenceDto(
                    "verify-1", "tests", "dotnet", ["test", "backend.Tests"], Sha('a'), Sha('a'), Sha('a'),
                    Clock.GetUtcNow().UtcDateTime, Clock.GetUtcNow().UtcDateTime, 1, null, "x", "y"),
                new Contract.ReviewCommandEvidenceDto(
                    "verify-2", "lint", "npm", ["run", "lint"], Sha('a'), Sha('a'), Sha('a'),
                    Clock.GetUtcNow().UtcDateTime, Clock.GetUtcNow().UtcDateTime, 0, null, "x", "y"),
            ],
            [],
            [
                new Contract.ReviewVerdictDto("tests", "block", "product", "OrderTotal_RoundsDown fails.", "OrderTests.cs"),
                new Contract.ReviewVerdictDto("lint", "pass", "none", "Clean."),
            ]);

        var prompt = OperatorSweepActions.BuildFixRoundFollowUp("rev-9", report, null, round: 2, allowed: 4);

        Assert.Contains("rev-9", prompt);
        Assert.Contains("OrderTotal_RoundsDown fails.", prompt);
        Assert.DoesNotContain("Clean.", prompt);
        Assert.Contains("`dotnet test backend.Tests`", prompt);
        Assert.DoesNotContain("npm run lint", prompt);
        Assert.Contains("automatic round 2 of 4", prompt);
    }

    private static OperatorSweepGateFailure Gate(
        bool environmentLadder, Contract.RunFailureClass failureClass, string code, string reason)
        => new(Sha('d'), IntegrationGateJournal.PreDevelopBuildGateStep, code, reason, null, environmentLadder, failureClass);

    private static ReviewDecisionRecord Record(string jobId, ReviewDecisionKind kind, int epoch)
        => new(Clock.GetUtcNow().UtcDateTime, jobId, "Fixture", kind, "r", "p", "r", "f") { AttemptEpoch = epoch };

    private static TimelineEvent Reopen(string cause, string? reviewAttemptId)
    {
        var details = new Dictionary<string, string> { ["cause"] = cause };
        if (reviewAttemptId is not null) details["reviewAttemptId"] = reviewAttemptId;
        return new TimelineEvent { Kind = TimelineEventKinds.QualityLoopReopened, Details = details };
    }

    private static string Sha(char c) => new(c, 40);
}
