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
        GateCapacity? gateCapacity = null,
        BatchGateInvariantReport? invariants = null)
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
        var projectManifests = store.ListManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var recoverable = store.ListRecoverableManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var fallbackCauses = store.ListFallbackCauses(project);
        var planDeadlinePassed = plan.CloseAtUtc is { } closeAt && closeAt <= at;
        var overduePending = pending.Where(item => (item.Subject.ReviewCompletedAtUtc
                .AddMinutes(settings.DeadlineMinutes) <= at || planDeadlinePassed)
            && !plan.ProposedMembers.Contains(item.Subject.TaskKey, StringComparer.Ordinal))
            .ToArray();
        var overdue = overduePending
            .Select(item =>
            {
                var formed = recoverable.FirstOrDefault(manifest => manifest.Members.Any(member =>
                    member.TaskKey == item.Subject.TaskKey
                    && member.RunAttempt == item.Subject.RunAttempt));
                var cause = fallbackCauses.LastOrDefault(entry =>
                    entry.ReviewAttemptId == item.ReviewAttemptId);
                var reason = formed is not null
                    ? $"batch {formed.BatchId} is {StateOf(store, formed).Phase.ToString().ToLowerInvariant()}"
                    : item.Subject.ActivePerTaskGate ? "per-task gate is in progress"
                    : cause is not null ? $"per-task fallback: {cause.Rule}"
                    : "pending member awaits the next planner tick";
                return $"{item.Subject.TaskKey}: {reason}";
            }).ToArray();
        if (overdue.Length > 0)
            plan = plan with
            {
                Decision = plan.Decision == "idle" ? "wait" : plan.Decision,
                Reason = plan.Reason + "; overdue pending: " + string.Join("; ", overdue),
                ProposedMembers = plan.ProposedMembers.Concat(overduePending
                    .Where(item => !item.Subject.ActivePerTaskGate
                        && !recoverable.Any(manifest => manifest.Members.Any(member =>
                            member.TaskKey == item.Subject.TaskKey
                            && member.RunAttempt == item.Subject.RunAttempt))
                        && !fallbackCauses.Any(cause => cause.ReviewAttemptId == item.ReviewAttemptId))
                    .Select(item => item.Subject.TaskKey))
                    .Distinct(StringComparer.Ordinal).ToArray(),
                CloseAtUtc = plan.CloseAtUtc is { } deadline && deadline <= at
                    ? null : plan.CloseAtUtc,
            };
        var manifests = projectManifests
            .OrderByDescending(item => item.ClosedAtUtc)
            .Take(Math.Clamp(history, 0, 500))
            .Concat(recoverable)
            .DistinctBy(item => item.BatchId)
            .OrderByDescending(item => item.ClosedAtUtc).ToArray();
        var moved = projectManifests
            .Select(item => StateOf(store, item))
            .Where(state => state.BaseMoveToSha is not null).ToArray();
        var stacked = store.ListManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase)
                && item.StackedOnBatchId is not null).ToArray();
        var executions = store.ListExecutions(project);
        var stackedCancelled = stacked.Where(item =>
            StateOf(store, item).Phase == BatchPhase.Abandoned
            && store.ReadPublication(item.BatchId) is null).ToArray();
        var wastedMinutes = stackedCancelled.Sum(item =>
        {
            var facts = executions.Where(fact => fact.BatchId == item.BatchId).ToArray();
            if (facts.Length > 0)
                return facts.Sum(fact => Math.Max(0, (fact.DurationMs is { } ms
                    ? ms : (long)(fact.CompletedAtUtc - fact.StartedAtUtc).TotalMilliseconds) / 60000d));
            var started = store.ListRuns(item.BatchId).FirstOrDefault()?.StartedAtUtc;
            return started is null ? 0 : Math.Max(0,
                (StateOf(store, item).RecordedAtUtc - started.Value).TotalMinutes);
        });
        var baseMoves = moved.GroupBy(state => state.BaseMoveToSha!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { baseSha = group.Key,
                kept = group.Count(state => state.BaseMoveOutcome == "kept"),
                abandoned = group.Count(state => state.BaseMoveOutcome == "abandoned") })
            .OrderByDescending(group => group.baseSha).ToArray();
        var batches = manifests.Select(manifest =>
        {
            var phase = StateOf(store, manifest);
            return new
            {
                manifest.BatchId,
                manifest.ParentBatchId,
                manifest.ReformedFromBatchId,
                manifest.StackedOnBatchId,
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
            var phase = StateOf(store, manifest).Phase;
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
                var baseMoveReason = StateOf(store, manifest)
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
                        : baseMoveReason is not null ? StateOf(store, manifest).Reason
                        : requeued ? StateOf(store, manifest).Reason : cause?.Event,
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
            stacking = new
            {
                started = stacked.Count(item => store.ListRuns(item.BatchId).Count > 0),
                published = stacked.Count(item => store.ReadPublication(item.BatchId) is not null),
                cancelled = stackedCancelled.Length,
                gateMinutesWasted = wastedMinutes,
            },
            baseMoves,
            baseMoveTotals = new { kept = moved.Count(state => state.BaseMoveOutcome == "kept"),
                abandoned = moved.Count(state => state.BaseMoveOutcome == "abandoned") },
            members,
            reviewFixWaits = reviewFixWaits ?? [],
            isolationFindings = store.ListIsolationFindings(project),
            // AGT-3563: the last invariant check of the pilot tick; null before the first tick.
            invariants = invariants is null ? null : new
            {
                invariants.CheckedAtUtc,
                invariants.Findings,
            },
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

    /// <summary>
    /// A manifest the snapshot reads between the manifest write and its first
    /// phase record is formed; the next tick writes that record (AGT-3563).
    /// </summary>
    private static BatchGateState StateOf(BatchGateStore store, BatchGateManifest manifest)
        => store.HasState(manifest.BatchId) ? store.LatestState(manifest.BatchId)
            : new BatchGateState(manifest.BatchId, manifest.MembershipDigest, BatchPhase.Formed,
                null, 0, manifest.ClosedAtUtc);

    private static string? EjectionDetail(BatchGateReplayRecord replay)
        => replay.Reason
           ?? (replay.ConflictedFiles.Count > 0
               ? "Conflicts with the candidate in " + string.Join(", ", replay.ConflictedFiles.Take(5))
               : null);
}
