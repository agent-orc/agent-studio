using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

public sealed record BatchGateReviewFixWait(string TaskKey, string ReviewAttemptId,
    int Round, int Maximum, IReadOnlyList<string> Aspects);

public static class BatchGateStateSnapshotReader
{
    public static object Read(BatchGateStore store, string project,
        BatchGateFormationOptions settings, int history = 10,
        IReadOnlyDictionary<string, BatchGateCachedRisk>? risks = null,
        PendingIntegrationPushWait? pushWait = null,
        GateSlotUsage? gateSlots = null,
        IReadOnlyList<PendingIntegrationPushWait>? pushWaits = null,
        DateTimeOffset? now = null,
        GateRunLedger? ledger = null,
        IReadOnlyList<BatchGateReviewFixWait>? reviewFixWaits = null,
        GateCapacity? gateCapacity = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var dailySummary = ledger?.Summary(store, project, DateOnly.FromDateTime(at.UtcDateTime));
        var pending = store.ListPending().Where(item =>
                string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Subject.EnqueueSequence).ToArray();
        var preChecks = store.ListPreChecks(project).OrderByDescending(item => item.CompletedAtUtc).ToArray();
        var plan = !settings.Enabled
            ? new BatchPlanDecision("disabled", "batch gate disabled", null, [], 0, [])
            : store.LatestPlanDecision(project)
                ?? new BatchPlanDecision(pending.Length == 0 ? "idle" : "wait",
                    pending.Length == 0 ? "no eligible cards" : "awaiting planner tick",
                    null, [], 0, []);
        if (pushWait is not null)
            plan = plan with { Reason = pushWait.Reason };
        var manifests = store.ListManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ClosedAtUtc)
            .Take(Math.Clamp(history, 0, 500)).ToArray();
        var moved = store.ListManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .Select(item => store.LatestState(item.BatchId))
            .Where(state => state.BaseMoveToSha is not null).ToArray();
        var baseMoves = moved.GroupBy(state => state.BaseMoveToSha!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { baseSha = group.Key,
                kept = group.Count(state => state.BaseMoveOutcome == "kept"),
                abandoned = group.Count(state => state.BaseMoveOutcome == "abandoned") })
            .OrderByDescending(group => group.baseSha).ToArray();
        var batches = manifests.Select(manifest =>
        {
            var phase = store.LatestState(manifest.BatchId);
            return new
            {
                manifest.BatchId,
                manifest.ParentBatchId,
                manifest.ReformedFromBatchId,
                phase = phase.Phase.ToString().ToLowerInvariant(),
                phaseReason = phase.Reason,
                baseMoveToSha = phase.BaseMoveToSha,
                baseMoveOutcome = phase.BaseMoveOutcome,
                successorBatchId = phase.SuccessorBatchId,
                baseMoveMemberReasons = phase.BaseMoveMemberReasons,
                manifest.FormationReason,
                exclusions = manifest.Exclusions,
                manifest.BaseSha,
                baseVerdict = store.ReadBaseVerdict(project, manifest.BaseSha,
                    manifest.Scope.GateProfileDigest),
                candidateSha = phase.CandidateSha,
                members = manifest.Members.Select(member => new
                {
                    member.TaskKey,
                    replay = store.TryReadReplay(manifest.BatchId, member.TaskKey),
                    released = store.ReadPublication(manifest.BatchId) is { } publication
                        && store.TryReadMember(manifest.BatchId, member.TaskKey,
                            publication.BatchRunId) is not null,
                }).ToArray(),
                runs = store.ListRuns(manifest.BatchId).Select(run =>
                {
                    var verdict = store.ReadVerdict(manifest.BatchId, run.BatchRunId);
                    return new
                    {
                        run.BatchRunId, run.Host,
                        outcome = verdict?.Outcome,
                        classification = verdict?.Classification,
                        startedAtUtc = (DateTimeOffset?)run.StartedAtUtc,
                        finishedAtUtc = verdict?.CompletedAtUtc,
                    };
                }).ToArray(),
                publication = store.ReadPublication(manifest.BatchId),
            };
        }).ToArray();
        // AGT-3466: why a member left the batch route, by review generation.
        var causes = pending.Select(item => item.Subject.Project)
            .Concat(manifests.Select(manifest => manifest.Scope.Project)).Append(project)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(store.ListFallbackCauses).ToArray();
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var wait in reviewFixWaits ?? [])
            members[wait.TaskKey] = new { status = "review-fix", batchId = (string?)null,
                phase = (string?)null, position = (int?)null, size = (int?)null,
                reason = $"actionable concern in {string.Join(", ", wait.Aspects)}", reasonDetail =
                    $"Review {wait.ReviewAttemptId} found a behavior gap in {string.Join(", ", wait.Aspects)}. "
                    + $"Fix round {wait.Round} of {wait.Maximum} must finish before batch admission." };
        foreach (var check in preChecks.Where(check => check.Outcome == "red"))
            members.TryAdd(check.TaskKey, new { status = "pre-check-red", batchId = (string?)null,
                phase = (string?)null, position = (int?)null, size = (int?)null,
                preCheck = check });
        foreach (var item in pending)
        {
            var cause = causes.LastOrDefault(entry => entry.ReviewAttemptId == item.ReviewAttemptId);
            members[item.Subject.TaskKey] = new { status = cause is null ? "pending" : "fallback",
                batchId = cause?.BatchId,
                phase = (string?)null, position = (int?)null, size = (int?)null,
                reason = cause?.Rule, reasonDetail = cause?.Event,
                fallbackRule = cause?.Rule, fallbackEvent = cause?.Event,
                preCheck = preChecks.FirstOrDefault(check => check.ResultSha == item.Subject.ResultSha
                    && check.GateProfileDigest == item.Subject.GateProfileDigest) };
        }
        foreach (var manifest in manifests.Reverse())
        {
            var phase = store.LatestState(manifest.BatchId).Phase;
            var publication = store.ReadPublication(manifest.BatchId);
            for (var index = 0; index < manifest.Members.Count; index++)
            {
                var member = manifest.Members[index];
                var released = publication is not null && store.TryReadMember(
                    manifest.BatchId, member.TaskKey, publication.BatchRunId) is not null;
                // An ejected member left its batch for the per-task gate; the
                // card names why (AGT-3104).
                var replay = store.TryReadReplay(manifest.BatchId, member.TaskKey);
                if (replay is { Outcome: "conflict" })
                {
                    members.Remove(member.TaskKey);
                    continue;
                }
                var ejected = replay is { Outcome: "unattributable" };
                var left = phase == BatchPhase.Abandoned || ejected;
                var baseMoveReason = store.LatestState(manifest.BatchId)
                    .BaseMoveMemberReasons?.GetValueOrDefault(member.TaskKey);
                var cause = left
                    ? causes.LastOrDefault(entry => entry.TaskKey == member.TaskKey
                        && entry.RunAttempt == member.RunAttempt && entry.RecordedAtUtc >= manifest.ClosedAtUtc)
                    : null;
                // A member of a batch abandoned before its gate that is queued
                // again waits for the next batch; it is no per-task fallback.
                var requeued = left && cause is null && !ejected && pending.Any(item =>
                    item.Subject.TaskKey == member.TaskKey && item.Subject.RunAttempt == member.RunAttempt);
                members[member.TaskKey] = new
                {
                    status = replay is { Outcome: "cascade-deferred" } || requeued ? "pending"
                        : released ? "published"
                        : left ? "fallback" : "in-batch",
                    batchId = manifest.BatchId,
                    phase = phase.ToString().ToLowerInvariant(),
                    position = (int?)(index + 1),
                    size = (int?)manifest.Members.Count,
                    reason = baseMoveReason ?? (ejected ? replay!.Outcome : requeued ? "requeued" : cause?.Rule),
                    reasonDetail = ejected ? EjectionDetail(replay!)
                        : baseMoveReason is not null ? store.LatestState(manifest.BatchId).Reason
                        : requeued ? store.LatestState(manifest.BatchId).Reason : cause?.Event,
                    fallbackRule = cause?.Rule,
                    fallbackEvent = cause?.Event,
                    preCheck = preChecks.FirstOrDefault(check => check.ResultSha == member.ResultSha
                        && check.BaseSha == manifest.BaseSha
                        && check.GateProfileDigest == member.GateProfileDigest),
                };
            }
        }
        return new
        {
            plan,
            pending = pending.Select(item =>
            {
                // A card the planner tick has not read yet keeps the lane estimate.
                var cached = risks?.GetValueOrDefault(item.Subject.TaskKey);
                return new
                {
                    item.Subject.TaskKey,
                    item.ReviewAttemptId,
                    item.Subject.ResultSha,
                    preCheck = preChecks.FirstOrDefault(check => check.ResultSha == item.Subject.ResultSha
                        && check.GateProfileDigest == item.Subject.GateProfileDigest),
                    waitMinutes = Math.Max(0, (at - item.EnqueuedAtUtc).TotalMinutes),
                    risk = cached?.Risk ?? (item.Subject.DocumentationOnly ? .08 : .2),
                    paths = cached?.Paths ?? [],
                    riskAgeMinutes = cached is null
                        ? (double?)null : Math.Max(0, (at - cached.ReadAtUtc).TotalMinutes),
                    exclusion = (string?)null,
                };
            }).ToArray(),
            batches,
            baseMoves,
            baseMoveTotals = new { kept = moved.Count(state => state.BaseMoveOutcome == "kept"),
                abandoned = moved.Count(state => state.BaseMoveOutcome == "abandoned") },
            members,
            reviewFixWaits = reviewFixWaits ?? [],
            isolationFindings = store.ListIsolationFindings(project),
            pushWait,
            pushWaits = pushWaits ?? (pushWait is null
                ? Array.Empty<PendingIntegrationPushWait>() : [pushWait]),
            gateSlots,
            gateCapacity,
            integrationGateLeaseCapMinutes = BatchIntegrationReservationService.IntegrationGateLeaseCap.TotalMinutes,
            preChecksRun = preChecks.Length,
            preChecksRed = preChecks.Count(check => check.Outcome == "red"),
            preCheckMinutesSaved = preChecks.Sum(check => check.MinutesSaved),
            dailySummary,
        };
    }

    private static string? EjectionDetail(BatchGateReplayRecord replay)
        => replay.Reason
           ?? (replay.ConflictedFiles.Count > 0
               ? "Conflicts with the candidate in " + string.Join(", ", replay.ConflictedFiles.Take(5))
               : null);
}
