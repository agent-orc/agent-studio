using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// The current gate failure on a delivered card, as the integration projection
/// and the pipeline log record it. Read by <see cref="IOperatorSweepGateFacts"/>.
/// </summary>
/// <param name="DeliverySha">The delivery SHA the gate ran against, or null when unknown.</param>
/// <param name="StepId">Failed step id (normally <c>pre-develop-build-gate</c>).</param>
/// <param name="FailureCode">Integration failure code (<c>build-gate-failed</c>, <c>gate-environment-failure</c>, ...).</param>
/// <param name="Reason">Recorded failure reason.</param>
/// <param name="Evidence">Verdict summary and evidence excerpt for the classifier.</param>
/// <param name="EnvironmentLadderOwns">The gate runner itself classified the failure as environmental.</param>
/// <param name="FailureClass">The shared run-failure taxonomy class on the integration failure.</param>
public sealed record OperatorSweepGateFailure(
    string? DeliverySha,
    string StepId,
    string FailureCode,
    string Reason,
    string? Evidence,
    bool EnvironmentLadderOwns,
    Contract.RunFailureClass FailureClass);

/// <summary>Reads the current gate failure of one card. A seam so the sweep is testable without Git.</summary>
public interface IOperatorSweepGateFacts
{
    OperatorSweepGateFailure? Read(TaskInfo job);
}

/// <summary>The latest settled review for the card's current delivery.</summary>
public sealed record OperatorSweepReviewFacts(
    string AttemptId,
    ReviewTerminalOutcome? Outcome,
    string? ExpectedResultSha);

/// <summary>A timed-out or lost-worker run that left a salvage commit.</summary>
public sealed record OperatorSweepSalvageFacts(
    RunSalvageReference Salvage,
    string RunAttemptId,
    string? ReportedReason);

/// <summary>
/// Pure trigger readers, one per sweep. They answer "is there something for this
/// sweep on this card, and which exact failure is it"; whether to act is
/// <see cref="OperatorSweepPolicy"/>'s question.
/// </summary>
public static class OperatorSweepTriggers
{
    public const string ReceiptSubjectKey = "subjectKey";
    public const string ReceiptSweepKey = "sweep";

    /// <summary>
    /// <c>fix-rounds</c>: a card in Human Review whose latest review for the
    /// current delivery settled as <c>ProductFailure</c>. A review for an older
    /// SHA is not fresh: the card has moved on since.
    /// </summary>
    public static OperatorSweepTrigger? FixRound(
        string lane,
        OperatorSweepReviewFacts? latestReview,
        string? currentDeliverySha,
        IEnumerable<TimelineEvent> timeline)
    {
        if (!string.Equals(lane, TaskStates.HumanReview, StringComparison.Ordinal)) return null;
        if (latestReview is not { Outcome: ReviewTerminalOutcome.ProductFailure }) return null;
        if (!string.IsNullOrWhiteSpace(currentDeliverySha)
            && !string.IsNullOrWhiteSpace(latestReview.ExpectedResultSha)
            && !string.Equals(currentDeliverySha, latestReview.ExpectedResultSha, StringComparison.OrdinalIgnoreCase))
            return null;
        var subject = "review:" + latestReview.AttemptId;
        return new OperatorSweepTrigger(subject, HasReceipt(timeline, OperatorSweepKinds.FixRounds, subject));
    }

    /// <summary>
    /// <c>gate-triage</c>: a delivered card whose current failure is the merge
    /// gate. The domain comes from the product's own classifiers, in order: the
    /// gate runner's environment verdict, the shared run-failure taxonomy on the
    /// integration failure, then <see cref="AgentStudio.Pipeline.FailureInterventionPolicy"/>.
    /// </summary>
    public static OperatorSweepTrigger? GateTriage(
        string lane,
        OperatorSweepGateFailure? failure,
        IEnumerable<TimelineEvent> timeline)
    {
        if (lane is not (TaskStates.HumanReview or TaskStates.Escalated)) return null;
        if (failure is null || !IsGateFailure(failure)) return null;
        var subject = $"gate:{failure.DeliverySha ?? "unknown"}:{failure.StepId}";
        return new OperatorSweepTrigger(
            subject,
            HasReceipt(timeline, OperatorSweepKinds.GateTriage, subject),
            ClassifyGateFailure(failure));
    }

    /// <summary>
    /// <c>salvage</c>: an escalated card whose latest run finished without a
    /// terminal outcome and named a salvage commit. The automatic AGT-2861
    /// continuation already had its one round; this sweep spends one round from
    /// the card budget instead of leaving the salvage for an operator.
    /// </summary>
    public static OperatorSweepTrigger? Salvage(
        string lane,
        OperatorSweepSalvageFacts? salvage,
        IEnumerable<TimelineEvent> timeline)
    {
        if (!string.Equals(lane, TaskStates.Escalated, StringComparison.Ordinal)) return null;
        if (salvage is null) return null;
        var subject = "salvage:" + salvage.Salvage.CommitSha;
        return new OperatorSweepTrigger(subject, HasReceipt(timeline, OperatorSweepKinds.Salvage, subject));
    }

    /// <summary>The salvage of the card's latest finished run, when that run ended non-terminally.</summary>
    public static OperatorSweepSalvageFacts? ReadSalvage(IEnumerable<TimelineEvent> timeline)
    {
        var finished = timeline.LastOrDefault(entry => entry.Kind == TimelineEventKinds.AgentRunFinished);
        var details = finished?.Details;
        if (details is null) return null;
        if (!RunTimeoutSalvageContinuationPolicy.IsNonTerminalOutcome(details.GetValueOrDefault("status")))
            return null;
        var salvage = RunSalvageReference.From(
            details.GetValueOrDefault("salvageBranch"),
            details.GetValueOrDefault("salvageCommitSha"),
            details.GetValueOrDefault("salvageRecoveryBranch"),
            details.GetValueOrDefault("salvageRecoveryCommitSha"));
        if (salvage is null) return null;
        var attempt = details.GetValueOrDefault("runAttemptId") ?? finished!.RunId ?? string.Empty;
        return new OperatorSweepSalvageFacts(salvage, attempt, details.GetValueOrDefault("reason"));
    }

    public static bool IsGateFailure(OperatorSweepGateFailure failure)
        => string.Equals(failure.StepId, IntegrationGateJournal.PreDevelopBuildGateStep, StringComparison.Ordinal)
           || failure.FailureCode is AcceptedIntegrationFailureCodes.BuildGateFailed
               or AcceptedIntegrationFailureCodes.DeliveryGateFailed
               or AcceptedIntegrationFailureCodes.GateEnvironmentFailure
               or AcceptedIntegrationFailureCodes.GateInterrupted;

    /// <returns><c>product</c>, <c>infrastructure</c>, or null when no classifier had an answer.</returns>
    public static string? ClassifyGateFailure(OperatorSweepGateFailure failure)
    {
        if (failure.EnvironmentLadderOwns) return FailureDomains.Infrastructure;
        switch (failure.FailureClass)
        {
            case Contract.RunFailureClass.Product:
                return FailureDomains.Product;
            case Contract.RunFailureClass.Infrastructure:
            case Contract.RunFailureClass.Quota:
                return FailureDomains.Infrastructure;
        }
        var classified = FailureInterventionPolicy.Classify(new FailureCommandEvidence(
            failure.FailureCode,
            StdoutTail: string.Join("\n", new[] { failure.Reason, failure.Evidence }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            StepId: failure.StepId));
        return classified?.Domain;
    }

    public static bool HasReceipt(IEnumerable<TimelineEvent> timeline, string sweep, string subject)
        => timeline.Any(entry =>
            entry.Kind == TimelineEventKinds.OperatorSweepRoundStarted
            && entry.Details?.GetValueOrDefault(ReceiptSweepKey) == sweep
            && entry.Details?.GetValueOrDefault(ReceiptSubjectKey) == subject);
}
