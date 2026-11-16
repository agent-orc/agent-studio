using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using OrchestrationStage = AgentStudio.TaskServer.Contracts.OrchestrationStage;

namespace AgentStudio.Runner;

/// <param name="PublishedSha">The published tree that carries the delivery, when <see cref="State"/> is published.</param>
/// <param name="Reason">What the engine is doing, or why it stopped.</param>
/// <param name="Retryable">A failure the same passed review may retry without a new review.</param>
/// <param name="RunId">The engine decision occurrence the state was read from, when one exists.</param>
public sealed record EngineDeliveryIntegrationStatus(
    EngineDeliveryIntegrationState State,
    string? PublishedSha = null,
    string? Reason = null,
    bool Retryable = false,
    string? RunId = null)
{
    /// <summary>Operator text: "running (...)", "published at abc1234", "failed: ...", "not started".</summary>
    public string Describe() => State switch
    {
        EngineDeliveryIntegrationState.Running => "running" + (Reason is { Length: > 0 } running ? $" ({running})" : ""),
        EngineDeliveryIntegrationState.Published => PublishedSha is { Length: > 0 } sha
            ? $"published at {(sha.Length > 7 ? sha[..7] : sha)}" : "published",
        EngineDeliveryIntegrationState.Failed => "failed: " + (Reason is { Length: > 0 } failed
            ? failed.TrimEnd('.') : "no reason was recorded"),
        _ => "not started" + (Reason is { Length: > 0 } idle ? $" ({idle})" : ""),
    };
}

/// <param name="Started">A fresh engine decision now owns the card.</param>
/// <param name="Detail">Operator text: the occurrence that was created, or why none was.</param>
public sealed record EngineDeliveryIntegrationRequest(bool Started, string Detail);

/// <summary>
/// AGT-3883: what the stranding watchdog asks the engine about a passed
/// review's per-task integration when the engine owns delivery. Tests
/// substitute a fake engine.
/// </summary>
public interface IEngineDeliveryIntegration
{
    EngineDeliveryIntegrationStatus Read(TaskInfo card, ReviewAttemptDto review);

    /// <summary>
    /// Asks the engine for a fresh decision on the same passed review, at most
    /// once per lane visit. Never creates a review attempt.
    /// </summary>
    EngineDeliveryIntegrationRequest Request(TaskInfo card, ReviewAttemptDto review,
        EngineDeliveryIntegrationStatus status);
}

/// <summary>
/// Reads the engine-owned per-task integration of one card from the engine's
/// decision occurrences, the gate journal, the delivery settlement and Git, and
/// asks the engine again through a new <c>ReviewReport</c> occurrence.
/// <para>
/// AGT-3876 (16.11.2026) published its delivery to develop while the engine
/// decision that integrated it never moved the card, and after the operator
/// moved it back nothing owned it. AGT-3868's gate environment failure parked
/// the card with "will be retried without a new review", and after the move
/// back nothing retried. In both cases the watchdog saw only "engine-owned".
/// </para>
/// </summary>
public sealed class EngineDeliveryIntegrationService(
    IServiceProvider services,
    TaskIntegrationStatusService integrationStatus,
    ILogger<EngineDeliveryIntegrationService> logger) : IEngineDeliveryIntegration
{
    private const string ReviewReportProfile = "ReviewReport";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Resolved per call: the store's applier depends on services that reach
    // the watchdog, so constructor injection would form a cycle.
    private MonolithOrchestrationStore? Store => services.GetService<MonolithOrchestrationStore>();

    public EngineDeliveryIntegrationStatus Read(TaskInfo card, ReviewAttemptDto review)
    {
        var occurrences = Store?.ForAuthorityAttempt(card.ProjectName, review.AttemptId) ?? [];
        // An unactivated preparation owns nothing until its report settles.
        if (occurrences.LastOrDefault(item => item.Run.Status is "leased" or "applying"
                || item.Run.Status == "pending" && item.Claimable) is { } open)
            return new(EngineDeliveryIntegrationState.Running,
                Reason: $"engine decision {open.Run.RunId} is {open.Run.Status}", RunId: open.Run.RunId);
        if (IntegrationGateJournal.Read(card.FolderPath) is not null)
            return new(EngineDeliveryIntegrationState.Running, Reason: "the integration gate is running");
        if (services.GetService<BatchGatePilotService>()?.IsPerTaskGateRunning(review.AttemptId) == true)
            return new(EngineDeliveryIntegrationState.Running, Reason: "the per-task gate is running");

        var status = ReadStatus(card);
        if (status?.Status == IntegrationStatuses.Integrated)
            return new(EngineDeliveryIntegrationState.Published,
                PublishedSha: ReviewSubjectStore.IsValidResultSha(status.Verification?.Sha)
                    ? status.Verification!.Sha : status.Sha,
                Reason: status.Detail);
        if (status?.Status == IntegrationStatuses.MergedLocally)
            return new(EngineDeliveryIntegrationState.Running,
                Reason: $"merged locally; the push to {status.IntegrationBranch} is pending");

        var latest = occurrences.LastOrDefault(item => item.Profile == ReviewReportProfile
            && item.Run.Status != "cancelled");
        if (latest?.Run.Status == "failed")
        {
            var failure = latest.ApplicationFailure ?? "the engine decision could not be applied";
            // A refused lane move refuses the same decision again; any other
            // application failure may succeed on a fresh decision.
            var refused = failure.StartsWith("Engine lane move failed: IntegrationFailed", StringComparison.Ordinal);
            return new(EngineDeliveryIntegrationState.Failed,
                Reason: $"engine decision {latest.Run.RunId}: {failure}", Retryable: !refused,
                RunId: latest.Run.RunId);
        }

        var settlement = RemoteDeliverySettlementStore.Read(card.FolderPath);
        if (RemoteDeliverySettlementStore.MatchesAttempt(settlement, review.AttemptId)
            && settlement!.Stage >= RemoteDeliverySettlementStage.IntegrationSettled
            && settlement.IntegrationOutcome is { } outcome)
        {
            if (!MonolithTransitionAdapter.EngineDeliveryIntegrationPublished(outcome))
                return new(EngineDeliveryIntegrationState.Failed,
                    Reason: RemoteDeliveryParkReason.NormalizeStoredDetail(settlement.IntegrationDetail) ?? outcome,
                    Retryable: MonolithTransitionAdapter.EngineDeliveryIntegrationRetryable(outcome),
                    RunId: latest?.Run.RunId);
            return new(EngineDeliveryIntegrationState.Failed,
                Reason: $"the integration reported {outcome}, but {status?.IntegrationBranch ?? "the integration branch"} "
                    + $"does not carry the delivery ({status?.Status ?? "unknown"})",
                Retryable: true, RunId: latest?.Run.RunId);
        }
        return new(EngineDeliveryIntegrationState.NotStarted,
            Reason: latest is null ? "the engine has no decision for this review"
                : $"engine decision {latest.Run.RunId} {latest.Run.Status} without integrating",
            RunId: latest?.Run.RunId);
    }

    public EngineDeliveryIntegrationRequest Request(TaskInfo card, ReviewAttemptDto review,
        EngineDeliveryIntegrationStatus status)
    {
        if (Store is not { } store)
            return new(false, "The engine decision store is unavailable.");
        if (review.Outcome != ReviewTerminalOutcome.Pass)
            return new(false, $"Review {review.AttemptId} did not pass, so there is no integration to ask for.");
        // One request per lane visit: an operator move back opens the next one.
        var key = $"engine-delivery-resume:{review.AttemptId}:{card.EnteredLaneAt.Ticks}";
        if (store.GetByIdempotencyKey(key) is { Status: not "cancelled" } prior)
            return new(false, $"The engine was already asked in this lane visit; decision {prior.RunId} is {prior.Status}.");
        var payload = DecisionPayload(store, card, review);
        if (status is { State: EngineDeliveryIntegrationState.Failed, Retryable: true })
            RemoteDeliverySettlementStore.ReopenForRetry(card.FolderPath, review.AttemptId);
        var run = store.Create(card.ProjectName, card.Id, OrchestrationStage.ReviewDecision, payload, key,
            TaskStates.AutoReview, review.AttemptId, review.LastFence, review.AuthorityEpoch,
            claimable: true, profile: ReviewReportProfile);
        EngineDecisionWaitMarker.Write(card.FolderPath, run.RunId);
        logger.LogInformation(
            "engine-delivery-integration-requested project={Project} job={JobId} attempt={AttemptId} run={RunId} state={State}",
            card.ProjectName, card.Id, review.AttemptId, run.RunId, status.State);
        return new(true, status.State switch
        {
            EngineDeliveryIntegrationState.Published => $"asked the engine to finish the lane move ({run.RunId})",
            EngineDeliveryIntegrationState.Failed => $"asked the engine to retry the integration of the same reviewed delivery ({run.RunId})",
            _ => $"asked the engine to start the integration ({run.RunId})",
        });
    }

    /// <summary>
    /// The payload of the review report's own engine decision, so the engine
    /// decides on the same facts. Without one, the passed review and its
    /// journaled report carry the facts the review decision reads.
    /// </summary>
    private static string DecisionPayload(MonolithOrchestrationStore store, TaskInfo card, ReviewAttemptDto review)
    {
        var reported = store.ForAuthorityAttempt(card.ProjectName, review.AttemptId)
            .LastOrDefault(item => item.Profile == ReviewReportProfile
                && item.Run.CurrentStage == OrchestrationStage.ReviewDecision
                && item.Run.PayloadJson.Contains("\"reviewOutcome\"", StringComparison.Ordinal));
        if (reported is not null) return reported.Run.PayloadJson;
        var report = RemoteReviewSettlementJournal.Read(card.FolderPath, review.AttemptId).Entry?.Report;
        return JsonSerializer.Serialize(new StagePayload(
            ReviewOutcome: report?.Outcome ?? review.Outcome?.ToString(),
            ReviewSummary: report?.Summary ?? review.TerminalReason,
            RunAttemptId: review.SourceRunAttemptId,
            ReviewSubjectId: review.Subject.SubjectId,
            ReviewAttemptId: review.AttemptId,
            ResultSha: review.Subject.ExpectedResultSha,
            ResultRef: review.Subject.ResultRef,
            Verdicts: report?.Verdicts,
            ReviewPlan: review.Subject.Plan,
            FailureClassification: report?.FailureClassification ?? review.FailureClassification), Json);
    }

    private TaskIntegrationStatus? ReadStatus(TaskInfo card)
    {
        try
        {
            return integrationStatus.BuildLookup([card]).Values.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "EngineDeliveryIntegrationService: the integration verdict is best-effort");
            return null;
        }
    }
}
