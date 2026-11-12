using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

public sealed record BatchGateReviewFixWait(string TaskKey, string ReviewAttemptId,
    int Round, int Maximum, IReadOnlyList<string> Aspects);

/// <summary>AGT-3562: a flight cancelled at its lease deadline that has not exited yet.</summary>
public sealed record BatchGateDetachedFlight(string BatchId, string Project,
    DateTimeOffset StartedAtUtc, DateTimeOffset DetachedAtUtc, int DeadlineMinutes, string Reason);

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
        IReadOnlyList<BatchGateDetachedFlight>? detachedFlights = null,
        string? leaseDeadlineDeferredBatchId = null,
        IReadOnlyList<BatchGateFallbackWait>? fallbackWaits = null,
        IReadOnlySet<string>? activePerTaskFlights = null,
        BatchGateInvariantReport? invariants = null,
        string? diskPauseReason = null)
    {
        // AGT-3694: what each per-task fallback waits for, so an overdue member
        // never shows only its rule.
        var waits = (fallbackWaits ?? []).GroupBy(wait => wait.ReviewAttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var at = now ?? DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(at.UtcDateTime);
        var pending = store.ListPending().Where(item =>
                string.Equals(item.Subject.Project, project, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Subject.EnqueueSequence).ToArray();
        var preChecks = store.ListPreChecks(project).OrderByDescending(item => item.CompletedAtUtc).ToArray();
        var fallbackCauses = store.ListFallbackCauses(project);
        var allPending = store.ListAllPending();
        var pendingByAttempt = allPending.GroupBy(item => item.ReviewAttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var latestCauseByAttempt = fallbackCauses.GroupBy(cause => cause.ReviewAttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var projectManifests = store.ListManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var relevantManifests = projectManifests.OrderByDescending(item => item.ClosedAtUtc)
            .Take(Math.Clamp(history, 0, 500))
            .Concat(projectManifests.Where(item => store.ReadPublication(item.BatchId) is { } publication
                && DateOnly.FromDateTime(publication.VerifiedAtUtc.UtcDateTime) == today)).ToArray();
        var relevantKeys = relevantManifests.SelectMany(item => item.Members)
            .Concat(pending.Select(item => item.Subject)).ToArray();
        var relevantChecks = preChecks.Where(check => DateOnly.FromDateTime(check.CompletedAtUtc.UtcDateTime) == today
            || relevantKeys.Any(member => member.TaskKey == check.TaskKey && member.ResultSha == check.ResultSha));
        var gateRows = BatchMemberTimelineProjection.Rows(ledger, project,
            new[] { today, today.AddDays(-1) }
                .Concat(relevantChecks.SelectMany(check => new[] {
                    DateOnly.FromDateTime(check.StartedAtUtc.UtcDateTime),
                    DateOnly.FromDateTime(check.StartedAtUtc.UtcDateTime).AddDays(-1) }))
                .Concat(relevantManifests.SelectMany(manifest => BatchMemberTimelineProjection.Days(
                    manifest.Members.Min(member => member.ReviewCompletedAtUtc),
                    store.ReadPublication(manifest.BatchId)?.VerifiedAtUtc ?? at))));
        var dailySummary = ledger?.Summary(store, project, today, gateRows);
        var plan = !settings.Enabled
            ? new BatchPlanDecision("disabled", "batch gate disabled", null, [], 0, [])
            : store.LatestPlanDecision(project)
                ?? new BatchPlanDecision(pending.Length == 0 ? "idle" : "wait",
                    pending.Length == 0 ? "no eligible cards" : "awaiting planner tick",
                    null, [], 0, []);
        if (pushWait is not null)
            plan = plan with { Reason = pushWait.Reason };
        var recoverable = store.ListRecoverableManifests()
            .Where(item => string.Equals(item.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        // AGT-3710: formed batches wait for the project's one flight first in, first out.
        // AGT-3712: the positions come from the flight queue the pilot starts from.
        var formedBatches = recoverable.Where(manifest =>
                store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
            .DistinctBy(manifest => manifest.BatchId, StringComparer.Ordinal).ToArray();
        var queuePositions = BatchGateFlightQueue.Positions(store, formedBatches,
            _ => leaseDeadlineDeferredBatchId);
        var flightQueue = formedBatches.OrderBy(manifest => queuePositions[manifest.BatchId]).ToArray();
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
                var cause = latestCauseByAttempt.GetValueOrDefault(item.ReviewAttemptId);
                var reason = formed is not null
                    ? $"batch {formed.BatchId} is {StateOf(store, formed).Phase.ToString().ToLowerInvariant()}"
                        + (queuePositions.TryGetValue(formed.BatchId, out var queued)
                            ? $", flight queue position {queued} of {flightQueue.Length}" : "")
                    : item.Subject.ActivePerTaskGate ? "per-task gate is in progress"
                    : cause is not null ? $"per-task fallback: {cause.Rule}"
                    : "pending member awaits the next planner tick";
                if (formed is null && waits.TryGetValue(item.ReviewAttemptId, out var wait))
                    reason += $", waits for {wait.Detail}";
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
                        && !latestCauseByAttempt.ContainsKey(item.ReviewAttemptId))
                    .Select(item => item.Subject.TaskKey))
                    .Distinct(StringComparer.Ordinal).ToArray(),
                CloseAtUtc = plan.CloseAtUtc is { } deadline && deadline <= at
                    ? null : plan.CloseAtUtc,
            };
        if (diskPauseReason is not null)
            plan = plan with { Decision = "wait", Reason = diskPauseReason };
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
                phaseAtUtc = phase.RecordedAtUtc,
                phaseReason = phase.Reason,
                phaseHistory = store.StateHistory(manifest.BatchId).Select(item => new {
                    phase = item.Phase.ToString().ToLowerInvariant(), reason = item.Reason, atUtc = item.RecordedAtUtc,
                }).ToArray(),
                queuePosition = queuePositions.TryGetValue(manifest.BatchId, out var position)
                    ? position : (int?)null,
                causedByTaskKey = phase.CausedByTaskKey,
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
                        hang = verdict?.Hang,
                        evidenceAvailable = store.StoredEvidenceFile(run.EvidencePath) is not null,
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
            .SelectMany(name => name == project ? fallbackCauses : store.ListFallbackCauses(name)).ToArray();
        var fallbackItems = pending.Select(item => new
            {
                item.Subject.TaskKey,
                item.ReviewAttemptId,
                Cause = latestCauseByAttempt.GetValueOrDefault(item.ReviewAttemptId),
                Ownership = BatchGateOwnershipStore.Read(item.JobFolderPath),
            })
            .Where(item => item.Cause is not null).ToArray();
        var perTaskGates = fallbackItems
            .Select((item, index) => new
            {
                item.TaskKey,
                item.ReviewAttemptId,
                position = fallbackItems.Take(index + 1).Count(other =>
                    other.Cause!.BatchId == item.Cause!.BatchId),
                total = fallbackItems.Count(other => other.Cause!.BatchId == item.Cause!.BatchId),
                batchId = item.Cause!.BatchId,
                reason = item.Cause.Event,
                running = activePerTaskFlights?.Contains(item.ReviewAttemptId) == true
                    && item.Ownership?.ReviewAttemptId == item.ReviewAttemptId
                    && item.Ownership.FallbackGateActive && item.Ownership.FallbackGateStartedAtUtc is not null,
                host = item.Ownership?.FallbackGateHost,
                startedAtUtc = item.Ownership?.FallbackGateStartedAtUtc,
            }).ToArray();
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
                preCheck = check,
                timeline = pendingByAttempt.TryGetValue(check.ReviewAttemptId, out var checkedPending)
                    ? BatchMemberTimelineProjection.Build(store, checkedPending.Subject, null,
                        gateRows, preChecks, fallbackCauses, allPending) : null });
        foreach (var cause in fallbackCauses)
        {
            if (!pendingByAttempt.TryGetValue(cause.ReviewAttemptId, out var origin)) continue;
            members[origin.Subject.TaskKey] = new { status = "fallback", batchId = cause.BatchId,
                phase = (string?)null, position = (int?)null, size = (int?)null,
                reason = cause.Rule, reasonDetail = cause.Event,
                fallbackRule = cause.Rule, fallbackEvent = cause.Event,
                timeline = BatchMemberTimelineProjection.Build(store, origin.Subject, null,
                    gateRows, preChecks, fallbackCauses, allPending) };
        }
        foreach (var item in pending)
        {
            var cause = causes.LastOrDefault(entry => entry.ReviewAttemptId == item.ReviewAttemptId);
            var wait = waits.GetValueOrDefault(item.ReviewAttemptId);
            members[item.Subject.TaskKey] = new { status = cause is null && wait is null ? "pending" : "fallback",
                batchId = cause?.BatchId,
                phase = (string?)null, position = (int?)null, size = (int?)null,
                reason = cause?.Rule, reasonDetail = cause?.Event,
                fallbackRule = cause?.Rule, fallbackEvent = cause?.Event,
                waitKind = wait?.Kind, waitingFor = wait?.Detail, waitSinceUtc = wait?.SinceUtc,
                engineRunId = wait?.EngineRunId, engineStatus = wait?.EngineStatus,
                preCheck = preChecks.FirstOrDefault(check => check.ResultSha == item.Subject.ResultSha
                    && check.GateProfileDigest == item.Subject.GateProfileDigest),
                timeline = BatchMemberTimelineProjection.Build(store, item.Subject, null,
                    gateRows, preChecks, fallbackCauses, allPending) };
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
                var fallbackCause = causes.LastOrDefault(entry => entry.TaskKey == member.TaskKey
                    && entry.RunAttempt == member.RunAttempt && entry.RecordedAtUtc >= manifest.ClosedAtUtc);
                var left = phase == BatchPhase.Abandoned || ejected || fallbackCause is not null;
                var baseMoveReason = StateOf(store, manifest)
                    .BaseMoveMemberReasons?.GetValueOrDefault(member.TaskKey);
                var cause = left ? fallbackCause : null;
                // A member of a batch abandoned before its gate that is queued
                // again waits for the next batch; it is no per-task fallback.
                var requeued = left && cause is null && !ejected && pending.Any(item =>
                    item.Subject.TaskKey == member.TaskKey && item.Subject.RunAttempt == member.RunAttempt);
                var wait = left ? (fallbackWaits ?? []).LastOrDefault(item =>
                    item.TaskKey == member.TaskKey && cause?.ReviewAttemptId == item.ReviewAttemptId) : null;
                members[member.TaskKey] = new
                {
                    status = replay is { Outcome: "cascade-deferred" } || requeued ? "pending"
                        : released ? "published"
                        : left ? "fallback"
                        : phase == BatchPhase.Unknown ? "unknown" : "in-batch",
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
                    waitKind = wait?.Kind, waitingFor = wait?.Detail, waitSinceUtc = wait?.SinceUtc,
                    engineRunId = wait?.EngineRunId, engineStatus = wait?.EngineStatus,
                    preCheck = preChecks.FirstOrDefault(check => check.ResultSha == member.ResultSha
                        && check.BaseSha == manifest.BaseSha
                        && check.GateProfileDigest == member.GateProfileDigest),
                    timeline = BatchMemberTimelineProjection.Build(store, member, manifest,
                        gateRows, preChecks, fallbackCauses, allPending),
                };
            }
        }
        // AGT-3740: a passed review that ran its own build and tests never
        // enters the queue. The state still names the route it took instead.
        foreach (var route in causes.Where(entry => entry.Rule == PerTaskFallbackRules.ReviewRanBuildTests)
                     .GroupBy(entry => entry.TaskKey, StringComparer.Ordinal)
                     .Select(group => group.MaxBy(entry => entry.RecordedAtUtc)!))
            members.TryAdd(route.TaskKey, new { status = "fallback", batchId = (string?)null,
                phase = (string?)null, position = (int?)null, size = (int?)null,
                reason = route.Rule, reasonDetail = route.Event,
                fallbackRule = route.Rule, fallbackEvent = route.Event,
                preCheck = (BatchGatePreCheckVerdict?)null });
        return new
        {
            diskPauseReason,
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
            perTaskGates,
            flightQueue = flightQueue.Select(manifest => new
            {
                manifest.BatchId,
                position = queuePositions[manifest.BatchId],
                queuedSinceUtc = BatchGateFlightQueue.QueuedSince(store, manifest),
                size = manifest.Members.Count,
                taskKeys = manifest.Members.Select(member => member.TaskKey).ToArray(),
            }).ToArray(),
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
            ejectRequests = store.ListEjects(project),
            reviewFixWaits = reviewFixWaits ?? [],
            detachedFlights = detachedFlights ?? [],
            fallbackWaits = fallbackWaits ?? [],
            fallbackStalls = store.ListFallbackStalls(project, 20),
            isolationFindings = store.ListIsolationFindings(project)
                .Where(finding => finding.Status != "proven-red"
                    || DeterministicIsolationFinding(store, projectManifests, finding))
                .Select(finding => IsolationFinding(store, projectManifests, finding)).ToArray(),
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

    /// <summary>
    /// AGT-3577: a finding with the run that proved it. A proven-red finding
    /// also carries that run's failed tests with their error excerpts and
    /// whether its evidence file can be opened.
    /// </summary>
    private static object IsolationFinding(BatchGateStore store,
        IReadOnlyList<BatchGateManifest> manifests, BatchGateIsolationFinding finding)
    {
        var proving = finding.Status == "proven-red" ? ProvingRun(store, manifests, finding) : null;
        var batchId = finding.ProvingBatchId ?? proving?.BatchId;
        var runId = finding.ProvingRunId ?? proving?.RunId;
        var failures = finding.Status == "proven-red" && batchId is not null && runId is not null
            ? store.RunFailures(finding.Project, batchId, runId)
                .DistinctBy(failure => failure.TestName, StringComparer.Ordinal)
                .Select(failure => new { failure.TestName, failure.ErrorExcerpt }).ToArray()
            : [];
        return new
        {
            finding.Project, finding.ParentBatchId, finding.TaskKey, finding.RunAttempt,
            finding.ResultSha, finding.BaseSha, finding.Status, finding.EvidencePath,
            finding.RecordedAtUtc,
            provingBatchId = batchId,
            provingRunId = runId,
            failures,
            evidenceAvailable = finding.Status == "proven-red"
                && store.StoredEvidenceFile(finding.EvidencePath) is not null,
        };
    }

    private static bool DeterministicIsolationFinding(BatchGateStore store,
        IReadOnlyList<BatchGateManifest> manifests, BatchGateIsolationFinding finding)
    {
        var proving = ProvingRun(store, manifests, finding);
        var batchId = finding.ProvingBatchId ?? proving?.BatchId;
        var runId = finding.ProvingRunId ?? proving?.RunId;
        return batchId is not null && runId is not null
            && store.ReadVerdict(batchId, runId)?.Classification == "DeterministicSuiteRed";
    }

    // Findings written before AGT-3577 name only the evidence path: the proving
    // run is the last run of a red child holding the member with that evidence.
    private static (string BatchId, string RunId)? ProvingRun(BatchGateStore store,
        IReadOnlyList<BatchGateManifest> manifests, BatchGateIsolationFinding finding)
    {
        if (finding.ProvingBatchId is not null && finding.ProvingRunId is not null) return null;
        foreach (var child in manifests.Where(manifest => manifest.ParentBatchId == finding.ParentBatchId
                     && manifest.Members.Any(member => member.TaskKey == finding.TaskKey)))
        {
            var run = store.ListRuns(child.BatchId).LastOrDefault();
            if (run is not null && string.Equals(store.ReadVerdict(child.BatchId, run.BatchRunId)?.EvidencePath,
                    finding.EvidencePath, StringComparison.Ordinal))
                return (child.BatchId, run.BatchRunId);
        }
        return null;
    }

    private static string? EjectionDetail(BatchGateReplayRecord replay)
        => replay.Reason
           ?? (replay.ConflictedFiles.Count > 0
               ? "Conflicts with the candidate in " + string.Join(", ", replay.ConflictedFiles.Take(5))
               : null);
}
