using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3856: a per-card green suite is not started for a card that a batch
/// gates anyway. On 3 and 4 November 2026 36 of 40 green per-card suites were
/// followed by a batch gate for the same card.
/// </summary>
public sealed partial class BatchGatePilotService
{
    /// <summary>Card timeline kind of a refused per-card suite.</summary>
    internal const string SuiteRefusedEventKind = "batch-per-task-suite-refused";

    /// <summary>Lane and state reason of a refused per-card suite.</summary>
    internal const string SuiteRefusedReason = "per-task-suite-refused";

    /// <summary>
    /// Asked right before a per-task fallback gate starts, after its slot and
    /// batch waits, because partners can arrive while it waits. Returns the
    /// refusal reason, or null when the suite starts.
    /// </summary>
    private string? PerTaskSuiteRefusal(BatchGatePendingRecord pending, string? baseSha)
    {
        var project = pending.Subject.Project;
        var options = _settings.Get(project).BatchGate;
        if (!BatchEnabled(project)) return null;
        var cause = _store.ListFallbackCauses(project)
            .LastOrDefault(item => item.ReviewAttemptId == pending.ReviewAttemptId);
        if (cause is null) return null;
        var subject = Refresh(pending).Subject;
        var scope = Scope(subject);
        var manifests = _store.ListRecoverableManifests();
        var member = manifests.FirstOrDefault(manifest => manifest.BatchId != cause.BatchId
            && manifest.Members.Any(item => item.TaskKey == subject.TaskKey && item.RunAttempt == subject.RunAttempt));
        var partnerRoute = cause.Rule == PerTaskFallbackRules.PlannerPerTask
            && MonolithTransitionAdapter.BatchPartnerMissingRoute(cause.Event);
        if (member is null && !partnerRoute) return null;
        var eligible = 0;
        var queued = 0;
        if (partnerRoute && baseSha is not null)
        {
            var activeKeys = manifests.SelectMany(manifest => manifest.Members)
                .Select(item => item.TaskKey).ToHashSet(StringComparer.Ordinal);
            string[] waitingFallbacks;
            lock (_fallbackFlights)
                waitingFallbacks = _fallbackFlights.Values
                    .Where(flight => flight.Pending.ReviewAttemptId != pending.ReviewAttemptId
                        && !flight.Task.IsCompleted
                        && flight.Stage is PerTaskFallbackWaitKinds.PerTaskSlot
                            or PerTaskFallbackWaitKinds.BatchSlot or PerTaskFallbackWaitKinds.Preparing)
                    .Select(flight => flight.Pending.ReviewAttemptId).ToArray();
            foreach (var item in _store.ListPending().Select(Refresh))
            {
                if (item.ReviewAttemptId == pending.ReviewAttemptId
                    || item.Subject.TaskKey == subject.TaskKey
                    || !item.Subject.CurrentGeneration) continue;
                if (waitingFallbacks.Contains(item.ReviewAttemptId, StringComparer.Ordinal))
                {
                    // Another lone card that waits for its own per-card suite in
                    // the same scope is a partner too, unless it was refused once.
                    if (Scope(item.Subject) == scope && !_store.HasSuiteRefusal(project, item.ReviewAttemptId)
                        && _store.ListFallbackCauses(project).LastOrDefault(other =>
                            other.ReviewAttemptId == item.ReviewAttemptId) is { } other
                        && other.Rule == PerTaskFallbackRules.PlannerPerTask
                        && MonolithTransitionAdapter.BatchPartnerMissingRoute(other.Event))
                        eligible++;
                    continue;
                }
                if (FallbackInFlight(item.ReviewAttemptId)) continue;
                if (EligibleForWaitingBatch(item, scope, baseSha, options, activeKeys)) eligible++;
                else if (options.PreCheckFor(project) && Scope(item.Subject) == scope
                         && item.Subject.OwningBatchId is null && !activeKeys.Contains(item.Subject.TaskKey)
                         && BatchGatePolicy.Exclusion(item.Subject, scope, options) is null
                         && _store.ReadPreCheck(project, item.Subject.ResultSha, baseSha,
                             item.Subject.GateProfileDigest) is null)
                    queued++;
            }
        }
        return MonolithTransitionAdapter.PerTaskSuiteRefusal(new PerTaskSuiteFacts(
            member?.BatchId, partnerRoute, _store.HasSuiteRefusal(project, pending.ReviewAttemptId),
            eligible, queued, options.MinimumSize, _time.GetUtcNow(),
            subject.ReviewCompletedAtUtc.AddMinutes(options.DeadlineMinutes)));
    }

    /// <summary>
    /// Returns a refused card to the batch queue with its passed review. The
    /// refusal is durable, so the card timeline, the batch gate state and the
    /// next decision for the same review generation all read it.
    /// </summary>
    private void RefusePerTaskSuite(TaskInfo task, BatchGatePendingRecord pending, string reason)
    {
        var project = pending.Subject.Project;
        var rule = _store.ListFallbackCauses(project)
            .LastOrDefault(item => item.ReviewAttemptId == pending.ReviewAttemptId)?.Rule
            ?? PerTaskFallbackRules.PlannerPerTask;
        _store.RecordSuiteRefusal(new BatchGateSuiteRefusal(project, pending.Subject.TaskKey,
            pending.ReviewAttemptId, pending.Subject.RunAttempt, rule, reason, _time.GetUtcNow()));
        BatchGateOwnershipStore.Write(task.FolderPath,
            new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject));
        _store.ClearFallbackCause(project, pending.ReviewAttemptId);
        Lane(pending, "requeued", reason: $"{SuiteRefusedReason}: {reason}");
        _timeline?.Append(task.FolderPath, new TimelineEvent
        {
            Ts = _time.GetUtcNow().UtcDateTime,
            Kind = SuiteRefusedEventKind,
            Actor = TimelineActors.System,
            Summary = "Per-card suite not started: " + reason,
            Details = new Dictionary<string, string>
            {
                ["reviewAttemptId"] = pending.ReviewAttemptId,
                ["rule"] = rule,
                ["reason"] = reason,
            },
        });
        _logger.LogInformation(
            "batch-fallback-suite-refused task={TaskKey} attempt={AttemptId} rule={Rule} reason={Reason}",
            pending.Subject.TaskKey, pending.ReviewAttemptId, rule, reason);
    }
}
