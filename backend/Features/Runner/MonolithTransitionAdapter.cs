using AgentStudio.TaskServer.Contracts.BatchGate;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Host boundary for the shared decision library. No transition rules belong here.</summary>
public static class MonolithTransitionAdapter
{
    public static TransitionDecision Decide(TransitionFacts facts) => TransitionDecisionPolicy.Decide(facts);
    public static ReviewBudgetDecision ReviewBudget(ReviewBudgetFacts facts) => ReviewBudgetPolicy.Decide(facts);
    public static OperatorSweepDecision Sweep(string sweep, OperatorSweepTrigger? trigger, OperatorSweepGuardFacts facts)
        => OperatorSweepPolicy.Decide(sweep, trigger, facts);

    public static TransitionDecision StageCoding(string taskId, string attemptId, string? targetState)
        => StageDecisionPolicy.Coding(taskId, attemptId, targetState);

    public static StagePolicyDecision StageGateTransition(string taskId, string attemptId,
        int failed, int roundsUsed, int roundsAllowed,
        bool infrastructureFailure = false, bool interventionRequired = false)
        => StageDecisionPolicy.GateTransition(taskId, attemptId, failed, roundsUsed,
            roundsAllowed, infrastructureFailure, interventionRequired);

    public static ReviewFollowUpDecision ReviewFollowUp(
        IEnumerable<ReviewFollowUpFinding> findings, int concernRoundsUsed, int maxConcernRounds)
        => ReviewFollowUpPolicy.Decide(findings, concernRoundsUsed, maxConcernRounds);

    public static RemoteDeliveryIntegrationDecision RemoteDeliveryIntegration(
        bool hasSettledResultEnvelope, string? reviewOutcome, ReviewPlanDto? reviewPlan,
        IReadOnlyList<ReviewVerdictDto> verdicts)
        => RemoteDeliveryIntegrationPolicy.Decide(hasSettledResultEnvelope, reviewOutcome, reviewPlan, verdicts);

    public static ReviewRoundBudgetDecision ReviewRoundBudget(
        ReviewRoundBudgetLedger ledger, string attemptId, IEnumerable<string> blockedAspects,
        int maximumRounds, int consecutiveBlockRounds,
        int? priorAutomaticReissues = null, int? maximumAutomaticReissues = null)
        => ReviewRoundBudgetPolicy.Decide(ledger, attemptId, blockedAspects, maximumRounds,
            consecutiveBlockRounds, priorAutomaticReissues, maximumAutomaticReissues);

    public static CardRoundBudgetAction CardRoundBudget(CardRoundBudgetState budget,
        IEnumerable<string> blockingAspects)
        => CardRoundBudgetPolicy.Decide(budget, blockingAspects);

    public static CardRoundBudgetAction CardRoundBudgetForReview(CardRoundBudgetState budget,
        IReadOnlyCollection<string> blockingAspects, bool opensConcernRound)
        => CardRoundBudgetPolicy.DecideForReview(budget, blockingAspects, opensConcernRound);

    public static string GateCancellation(string? expectedSha, string? currentSha,
        string? expectedLane, string? currentLane, bool continuePending,
        bool shuttingDown, string? reportedReason = null)
        => GateCancellationPolicy.Decide(expectedSha, currentSha, expectedLane, currentLane,
            continuePending, shuttingDown, reportedReason);

    public static bool GateCancellationOwnsResult(string? expectedSha, string? currentSha,
        string? expectedAttempt, string? currentAttempt,
        string? expectedLane, string? currentLane, bool continuePending)
        => GateCancellationPolicy.OwnsResult(expectedSha, currentSha,
            expectedAttempt, currentAttempt, expectedLane, currentLane, continuePending);

    public static BatchPlanStep BatchPlan(BatchPlanFacts facts)
        => BatchPlanDecisionPolicy.Decide(facts);

    public static RemoteReviewContinuationStep ContinuationReplay(IEnumerable<RoundTimelineFact> ledger,
        string attemptId, string cause, string moveKey, string currentState)
        => ContinuationReplayPolicy.Decide(ledger, attemptId, cause, moveKey, currentState);

    /// <summary>The AI Operator decision while the <c>OperatorDecision</c> switch selects Monolith.</summary>
    public static AiOperatorDecision Operator(AiOperatorDecisionPayload payload) => AiOperatorDecisionPolicy.Decide(payload);

    /// <summary>A measured loop boundary, without selecting a terminal action.</summary>
    public static LoopExhaustionSignalV1? LoopSignal(string id, int count, int limit,
        string window, string attempt, IReadOnlyList<string> candidates)
        => LoopExhaustionPolicy.Signal(id, count, limit, window, attempt, candidates);

    public static OrchestratorDecisionVerdict? ReviewText(string output)
        => LegacyReviewDecisionPolicy.Parse(output) is { } choice
            ? new OrchestratorDecisionVerdict(choice.Action switch
            {
                "reissue" => OrchestratorDecisionAction.Reissue,
                "escalate" => OrchestratorDecisionAction.Escalate,
                _ => OrchestratorDecisionAction.AcceptAsDone,
            }, choice.Reason)
            : null;

    /// <summary>The transition an admitted active coding-completion choice maps to (AGT-3064).</summary>
    public static TransitionDecision? ActiveCodingTransition(DecisionChoiceV1 choice, string cardKey, string attemptId)
        => ActiveDecisionPolicy.CodingTransition(choice, cardKey, attemptId);
    // Every backend call into a pure decision policy crosses this boundary.
    public static bool BatchPlanDecisionPolicyAdmissible(BatchPlanFacts facts, BatchPlanDecision decision, out string error)
        => BatchPlanDecisionPolicy.Admissible(facts, decision, out error);

    public static BatchPlanDecision BatchPlanDecisionPolicyAdvise(BatchPlanFacts facts, BatchPlanDecision deterministic, BatchAdvisorReply reply)
        => BatchPlanDecisionPolicy.Advise(facts, deterministic, reply);

    public static string BatchPlanDecisionPolicyAdvisorInput(BatchPlanInput snapshot, BatchPlanDecision deterministic)
        => BatchPlanDecisionPolicy.AdvisorInput(snapshot, deterministic);

    public static string BatchPlanDecisionPolicySignature(BatchPlanDecision decision)
        => BatchPlanDecisionPolicy.Signature(decision);

    public static CardRunLedgerResult CardRunLedgerPolicyBuild(IReadOnlyList<RunLedgerRowV1> rows, IReadOnlyList<RunLedgerRoundInputV1> rounds, IReadOnlyList<string> verifiedTrees, Func<RunLedgerRowV1, DecisionTokenUsageV1, DecisionPriceV1> price)
        => CardRunLedgerPolicy.Build(rows, rounds, verifiedTrees, price);

    public static DecisionUsageTotals CardRunLedgerPolicyDeduplicate(IReadOnlyList<DecisionUsageFrameV1> frames)
        => CardRunLedgerPolicy.Deduplicate(frames);

    public static CouncilReviewReaction CouncilDecisionPolicyDerive(string reviewFileName, string gradeToken, IReadOnlyList<string> findings, int priorReissues, int maxReissues, string jobId, int? targetRunAttempt = null, string? executionError = null, DateTime now = default)
        => CouncilDecisionPolicy.Derive(reviewFileName, gradeToken, findings, priorReissues, maxReissues, jobId, targetRunAttempt, executionError, now);

    public static CouncilReviewReaction CouncilDecisionPolicyEscalateBecause(CouncilReviewReaction reaction, string reason)
        => CouncilDecisionPolicy.EscalateBecause(reaction, reason);

    public static string? DecisionCostCapPolicyCallEnvelopeReason(int input, int output)
        => DecisionCostCapPolicy.CallEnvelopeReason(input, output);

    public static string? DecisionCostCapPolicyCardReason(DecisionSpendSnapshot card, decimal estimatedUsd = 0)
        => DecisionCostCapPolicy.CardReason(card, estimatedUsd);

    public static bool DecisionCostCapPolicyChargesCard(GroundsScopeV1 scope)
        => DecisionCostCapPolicy.ChargesCard(scope);

    public static (int Input, int Output) DecisionCostCapPolicyEnvelope(int requestedInput, int requestedOutput)
        => DecisionCostCapPolicy.Envelope(requestedInput, requestedOutput);

    public static string DecisionCostCapPolicyHostDayKey(GroundsScopeV1 scope, DateTime at)
        => DecisionCostCapPolicy.HostDayKey(scope, at);

    public static string? DecisionCostCapPolicyHostDayReason(DecisionSpendSnapshot scheduled, DecisionSpendSnapshot events, bool scheduledTrigger, int input, int output, decimal estimatedUsd = 0)
        => DecisionCostCapPolicy.HostDayReason(scheduled, events, scheduledTrigger, input, output, estimatedUsd);

    public static bool DecisionCostCapPolicyIsScheduled(GroundsTriggerV1 trigger)
        => DecisionCostCapPolicy.IsScheduled(trigger);

    public static string? DecisionCostCapPolicyMeasuredOverrunReason(DecisionTokenUsageV1 tokens, int input, int output)
        => DecisionCostCapPolicy.MeasuredOverrunReason(tokens, input, output);

    public static IReadOnlyList<GroundsAlternativeV1> DecisionGroundsPolicyCodingCompletionAlternatives(TransitionDecision deterministic, bool hasSettledEnvelope, string? lastVerifiedTreeSha = null, string? resultSha = null, string? resultRef = null)
        => DecisionGroundsPolicy.CodingCompletionAlternatives(deterministic, hasSettledEnvelope, lastVerifiedTreeSha, resultSha, resultRef);

    public static DecisionChoiceV1 DecisionGroundsPolicyCodingCompletionChoice(TransitionDecision deterministic)
        => DecisionGroundsPolicy.CodingCompletionChoice(deterministic);

    public static DecisionGroundsV1 DecisionGroundsPolicyFreeze(DecisionGroundsInputV1 input)
        => DecisionGroundsPolicy.Freeze(input);

    public static bool DecisionGroundsPolicyIsIntact(DecisionGroundsV1 grounds)
        => DecisionGroundsPolicy.IsIntact(grounds);

    public static string DecisionReceiptPolicyEvidenceRef(string receiptId)
        => DecisionReceiptPolicy.EvidenceRef(receiptId);

    public static DecisionModelResponse DecisionReceiptPolicyParse(string? text, DecisionGroundsV1 grounds)
        => DecisionReceiptPolicy.Parse(text, grounds);

    public static string DecisionReceiptPolicyReceiptId(string occurrenceId, string groundsDigest)
        => DecisionReceiptPolicy.ReceiptId(occurrenceId, groundsDigest);

    public static IReadOnlyList<GroundsAlternativeV1> DecisionRulePolicyApply(IReadOnlyList<DecisionRuleV1> rules, string trigger, string profile, IReadOnlyList<GroundsFactV1> facts, IReadOnlyList<GroundsAlternativeV1> alternatives, DateTime observedAt)
        => DecisionRulePolicy.Apply(rules, trigger, profile, facts, alternatives, observedAt);

    public static DecisionRuleReplaySignals DecisionRulePolicyReplay(IReadOnlyList<DecisionRuleV1> rules, OrchestratorDecisionReceiptV1 receipt)
        => DecisionRulePolicy.Replay(rules, receipt);

    public static IReadOnlyList<GroundsFactV1> DecisionRulePolicySignals(IReadOnlyList<DecisionRuleV1> rules, string trigger, IReadOnlyList<GroundsFactV1> facts, string policyVersion, DateTime observedAt)
        => DecisionRulePolicy.Signals(rules, trigger, facts, policyVersion, observedAt);

    public static DecisionRuleValidation DecisionRulePolicyValidate(IReadOnlyList<DecisionRuleV1> rules, bool projectOverlay, DecisionRuleModelCatalogue models)
        => DecisionRulePolicy.Validate(rules, projectOverlay, models);

    public static string? DecisionRulePolicyCardAlternativeRefusal(IReadOnlyList<DecisionRuleV1> rules)
        => DecisionRulePolicy.CardAlternativeRefusal(rules);

    public static string? DecisionRulePolicyModelRefusal(string model, DecisionRuleModelCatalogue models)
        => DecisionRulePolicy.ModelRefusal(model, models);

    public static string? GateEnvironmentRetryPolicyOperatorReplayRefusal(GateEnvironmentRetryFacts facts)
        => GateEnvironmentRetryPolicy.OperatorReplayRefusal(facts);

    public static string GateEnvironmentRetryPolicyParkedReason(int attempts, string integrationBranch, string? gateReason)
        => GateEnvironmentRetryPolicy.ParkedReason(attempts, integrationBranch, gateReason);

    public static bool LocalLoopDecisionPolicyRepeatedNoProgress(int priorReissues, bool sawRecovery, int toolCalls, int fileChanges, int substantiveCharacters)
        => LocalLoopDecisionPolicy.RepeatedNoProgress(priorReissues, sawRecovery, toolCalls, fileChanges, substantiveCharacters);

    public static ReissueLoopBreaker.Decision ReissueLoopBreakerEvaluate(int priorReissues, int maxReissues, bool emptyFollowupDiff, bool stateAcceptable)
        => ReissueLoopBreaker.Evaluate(priorReissues, maxReissues, emptyFollowupDiff, stateAcceptable);

    public static RepeatedAspectBlockDiagnosis? RepeatedAspectDecisionPolicyDiagnose(IReadOnlyList<AgentStudio.TaskServer.Contracts.ReviewFollowUpFinding> verdicts, IEnumerable<RepeatedAspectRoundFact> records, string jobId, int attemptEpoch, int maximumRounds)
        => RepeatedAspectDecisionPolicy.Diagnose(verdicts, records, jobId, attemptEpoch, maximumRounds);

    public static string RepeatedAspectDecisionPolicyFingerprint(string value)
        => RepeatedAspectDecisionPolicy.Fingerprint(value);

    public static string? ReviewFollowUpPolicyExtractFindingSection(string? reportBody)
        => ReviewFollowUpPolicy.ExtractFindingSection(reportBody);

    public static ReviewInfrastructureRepeatDiagnosis? ReviewInfrastructureRepeatPolicyDiagnose(IReadOnlyList<ReviewInfrastructureAttemptFact> chainOldestFirst, string? plannedIntegrationRef)
        => ReviewInfrastructureRepeatPolicy.Diagnose(chainOldestFirst, plannedIntegrationRef);

    public static AgentStudio.TaskServer.Contracts.ReviewFollowUpDecision ReviewRoundBudgetPolicyApplyFollowUp(AgentStudio.TaskServer.Contracts.ReviewFollowUpDecision followUp, ReviewRoundBudgetDecision decision)
        => ReviewRoundBudgetPolicy.ApplyFollowUp(followUp, decision);

    public static AgentStudio.TaskServer.Contracts.ReviewReportRequest ReviewRoundBudgetPolicyApplyRemoteReport(AgentStudio.TaskServer.Contracts.ReviewReportRequest request, ReviewRoundBudgetDecision decision)
        => ReviewRoundBudgetPolicy.ApplyRemoteReport(request, decision);

    public static IReadOnlyList<string> ReviewRoundBudgetPolicyCountedBlockingAspects(IEnumerable<AgentStudio.TaskServer.Contracts.ReviewFollowUpFinding> findings)
        => ReviewRoundBudgetPolicy.CountedBlockingAspects(findings);

    public static IReadOnlyList<string> ReviewRoundBudgetPolicyCountedBlockingAspects(IEnumerable<AgentStudio.TaskServer.Contracts.ReviewVerdictDto> verdicts)
        => ReviewRoundBudgetPolicy.CountedBlockingAspects(verdicts);

    public static bool ReviewRoundBudgetPolicyIsDeliveredOutcome(string? outcome)
        => ReviewRoundBudgetPolicy.IsDeliveredOutcome(outcome);

    public static int RoundCounterPolicyCount(IEnumerable<RoundDecisionFact> journal, IEnumerable<RoundTimelineFact> timeline, string jobId)
        => RoundCounterPolicy.Count(journal, timeline, jobId);

    public static string RunTimeoutSalvageContinuationPolicyComposeEscalationReason(string? reportedReason, RunSalvageReference? salvage, int automaticContinuationRoundsUsed)
        => RunTimeoutSalvageContinuationPolicy.ComposeEscalationReason(reportedReason, salvage, automaticContinuationRoundsUsed);

    public static bool RunTimeoutSalvageContinuationPolicyIsNonTerminalOutcome(string? outcome)
        => RunTimeoutSalvageContinuationPolicy.IsNonTerminalOutcome(outcome);

    public static TransitionFacts StageDecisionPolicyFacts(string taskId, int reissues, StagePayload payload)
        => StageDecisionPolicy.Facts(taskId, reissues, payload);

    public static OrchestrationAction StageDecisionPolicyToStageAction(TransitionAction action)
        => StageDecisionPolicy.ToStageAction(action);

    public static string TransitionDecisionPolicyCompletionLane(string outcome, bool providerRejected)
        => TransitionDecisionPolicy.CompletionLane(outcome, providerRejected);

    public static bool TransitionDecisionPolicyCreateReview(string outcome, bool verifiedDelivery, bool reportOnly)
        => TransitionDecisionPolicy.CreateReview(outcome, verifiedDelivery, reportOnly);

    public static bool TransitionDecisionPolicyIsDeferredAcceptance(bool deferred, string outcome)
        => TransitionDecisionPolicy.IsDeferredAcceptance(deferred, outcome);

    public static bool TransitionDecisionPolicyOpensFindingRound(string outcome, ReviewFollowUpAction action)
        => TransitionDecisionPolicy.OpensFindingRound(outcome, action);

    public static bool TransitionDecisionPolicyRetryInfrastructure(bool budgetAvailable, bool aspectRetryExhausted, bool interventionActive, bool probeFailed, bool diagnosisSupported)
        => TransitionDecisionPolicy.RetryInfrastructure(budgetAvailable, aspectRetryExhausted, interventionActive, probeFailed, diagnosisSupported);

    public static string? TransitionDecisionPolicyTargetLane(TransitionAction? action, string? sourcePayloadTarget, OrchestrationAction stageAction)
        => TransitionDecisionPolicy.TargetLane(action, sourcePayloadTarget, stageAction);

}
