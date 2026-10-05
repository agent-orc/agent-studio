
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pure-policy coverage for <see cref="ReissueLoopBreaker"/> (ASS-794): the
/// deterministic loop-breaker that stops a finished task from penduluming
/// between <c>2-ready</c> and the run loop on the multi-aspect BLOCK path. Two
/// rules in precedence order: an empty follow-up diff on an already-reissued
/// clean card accepts; budget spent escalates; otherwise there is no loop-break.
/// </summary>
public class ReissueLoopBreakerTests
{
    [Fact]
    public void Evaluate_EmptyDiffOnReissuedCleanCard_Accepts()
    {
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 1, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.AcceptEmptyDiff, decision.Action);
        Assert.True(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_EmptyDiffButFirstPass_DoesNotAccept()
    {
        // priorReissues == 0: there is no "follow-up" run yet, so the empty-diff
        // rule must not fire. Budget is intact too, so no loop-break at all.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 0, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
        Assert.False(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_EmptyDiffButStateNotAcceptable_DoesNotAccept()
    {
        // An empty re-run on a card whose own close-out is NOT clean is not
        // evidence that nothing is open; the empty-diff accept must not fire.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 1, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: false);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
    }

    [Fact]
    public void Evaluate_NonEmptyDiffBudgetIntact_NoLoopBreak()
    {
        // Real follow-up work landed (HEAD changed) and budget remains: fall
        // through to the normal reissue path.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 1, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
        Assert.False(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_BudgetSpentNonEmptyDiff_Escalates()
    {
        // Budget exhausted and the re-run is not empty: do not loop back to
        // 2-ready again; escalate to human review.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 2, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.Escalate, decision.Action);
        Assert.True(decision.BreaksLoop);
    }

    [Fact]
    public void Evaluate_BudgetSpentButStateNotAcceptable_StillEscalates()
    {
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 3, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: false);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.Escalate, decision.Action);
    }

    [Fact]
    public void Evaluate_EmptyDiffAcceptTakesPrecedenceOverBudget()
    {
        // Both rules are eligible (budget spent AND empty clean re-run). The
        // empty-diff accept must win: an empty clean re-run is low-burden to
        // accept and should not be escalated as a "budget exhausted" failure.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 2, maxReissues: 2, emptyFollowupDiff: true, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.AcceptEmptyDiff, decision.Action);
    }

    [Fact]
    public void Evaluate_AspectRecurrenceIsHandledBeforeLoopBreaker()
    {
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 1,
            maxReissues: 5,
            emptyFollowupDiff: true,
            stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.AcceptEmptyDiff, decision.Action);
    }

    [Fact]
    public void Diagnose_SecondIdenticalAspectBlockInAttemptEpoch_ReachesLimit()
    {
        var report = AspectRunReport.From(
        [
            new AspectVerdict(
                "requirement-fit",
                AspectStatus.Block,
                "Required S1 slice is still missing.",
                "body",
                "requirement:concerns"),
        ]);
        var first = RepeatedAspectBlockPolicy.Diagnose(
            report, [], "AGT-1", attemptEpoch: 4, maximumRounds: 2)!;
        var records = new[]
        {
            new ReviewDecisionRecord(
                DateTime.UnixEpoch,
                "AGT-1",
                "Project",
                ReviewDecisionKind.Reissue,
                "blocked",
                "prompt",
                "response",
                "follow-up")
            {
                AttemptEpoch = 4,
                FailureKind = RepeatedAspectBlockPolicy.FailureKind,
                FailureFingerprint = first.Fingerprint,
            },
        };

        var second = RepeatedAspectBlockPolicy.Diagnose(
            report, records, "AGT-1", attemptEpoch: 4, maximumRounds: 2)!;

        Assert.True(second.MustEscalate);
        Assert.Equal(2, second.ConsecutiveRounds);
        Assert.Contains("requirement-fit", second.Finding);
    }

    [Fact]
    public void Diagnose_TracksOneRepeatedAspectWhileAnotherFindingChanges()
    {
        var firstReport = AspectRunReport.From(
        [
            new AspectVerdict(
                "code-quality", AspectStatus.Block, "First quality issue.", "body", "quality:concerns"),
            new AspectVerdict(
                "requirement-fit", AspectStatus.Block, "Same missing slice.", "body", "requirement:concerns"),
        ]);
        var first = RepeatedAspectBlockPolicy.Diagnose(
            firstReport, [], "AGT-2", attemptEpoch: 1, maximumRounds: 2)!;
        var records = new[]
        {
            new ReviewDecisionRecord(
                DateTime.UnixEpoch,
                "AGT-2",
                "Project",
                ReviewDecisionKind.Reissue,
                "blocked",
                "prompt",
                "response",
                "follow-up")
            {
                AttemptEpoch = 1,
                FailureKind = RepeatedAspectBlockPolicy.FailureKind,
                FailureFingerprint = first.Fingerprint,
                FailureFingerprints = first.CurrentFingerprints,
            },
        };
        var secondReport = AspectRunReport.From(
        [
            new AspectVerdict(
                "code-quality", AspectStatus.Block, "Different quality issue.", "body", "quality:concerns"),
            new AspectVerdict(
                "requirement-fit", AspectStatus.Block, "Same missing slice.", "body", "requirement:concerns"),
        ]);

        var second = RepeatedAspectBlockPolicy.Diagnose(
            secondReport, records, "AGT-2", attemptEpoch: 1, maximumRounds: 2)!;

        Assert.True(second.MustEscalate);
        Assert.Equal("code-quality: Different quality issue.", second.Finding);
    }

    [Fact]
    public void Diagnose_ChangedSummaryAndEpoch_StillMatchTheAspect()
    {
        var firstReport = AspectRunReport.From([
            new AspectVerdict("code-quality", AspectStatus.Block, "First file", "body", "quality:concerns"),
        ]);
        var first = RepeatedAspectBlockPolicy.Diagnose(
            firstReport, [], "AGT-3", attemptEpoch: 0, maximumRounds: 2)!;
        var record = new ReviewDecisionRecord(
            DateTime.UnixEpoch, "AGT-3", "Project", ReviewDecisionKind.Reissue,
            "blocked", "prompt", "response", "follow-up")
        {
            AttemptEpoch = 0,
            FailureKind = RepeatedAspectBlockPolicy.FailureKind,
            FailureFingerprint = first.Fingerprint,
        };
        var changedReport = AspectRunReport.From([
            new AspectVerdict("code-quality", AspectStatus.Block, "Another method", "body", "quality:concerns"),
        ]);

        var boundary = new ReviewDecisionRecord(
            DateTime.UnixEpoch, "AGT-3", "Project", ReviewDecisionKind.OperatorRequeue,
            "continue", "", "", "") { AttemptEpoch = 1 };
        var second = RepeatedAspectBlockPolicy.Diagnose(
            changedReport, [record, boundary], "AGT-3", attemptEpoch: 1, maximumRounds: 2)!;

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(2, second.ConsecutiveRounds);
        Assert.Equal("code-quality: Another method", second.Finding);
    }

    [Fact]
    public void Evaluate_DefaultHappyPath_NoLoopBreak()
    {
        // First pass, real diff, budget intact: the common case is no loop-break.
        var decision = ReissueLoopBreaker.Evaluate(
            priorReissues: 0, maxReissues: 2, emptyFollowupDiff: false, stateAcceptable: true);

        Assert.Equal(ReissueLoopBreaker.LoopBreakAction.None, decision.Action);
        Assert.False(decision.BreaksLoop);
    }
}
