using AgentStudio.Git;

namespace AgentStudio.Pipeline;

public sealed partial class BatchGatePilotService
{
    /// <summary>
    /// Probe the exact proposed order before writing a closed manifest. Git replay
    /// is side effect free with respect to the integration ref; assembly repeats
    /// it under the coordinator lease because the result can still move later.
    /// </summary>
    private async Task<BatchGateFormation> AdmitFormationAsync(
        BatchGateFormation formation, IReadOnlyList<BatchGatePendingRecord> candidates,
        BatchGateFormationOptions options, CancellationToken ct,
        IReadOnlySet<string>? replaceableBatchIds = null,
        bool routeRejected = true)
    {
        var manifest = formation.Manifest;
        if (manifest is null) return formation;
        var byKey = candidates.ToDictionary(item => item.Subject.TaskKey, StringComparer.Ordinal);
        var pendingAttempts = _store.ListPending().Select(item => item.ReviewAttemptId)
            .ToHashSet(StringComparer.Ordinal);
        var admitted = new List<BatchGateSubject>();
        var rejected = new List<BatchGateExclusion>();
        var actions = new List<(BatchGatePendingRecord Pending, BatchReplayResult? Replay,
            BatchExclusionReason Reason)>();
        var tip = manifest.BaseSha;
        foreach (var member in manifest.Members)
        {
            if (!byKey.TryGetValue(member.TaskKey, out var candidate)
                || !pendingAttempts.Contains(candidate.ReviewAttemptId))
            {
                rejected.Add(new(member.TaskKey, BatchExclusionReason.NoLongerPending));
                continue;
            }
            var item = Refresh(candidate);
            var subject = item.Subject;
            if (subject.OwningBatchId is { } owner
                && replaceableBatchIds?.Contains(owner) == true)
                subject = subject with { OwningBatchId = null };
            var exclusion = _snapshots.PendingExclusion(item) is { } pendingReason
                ? new BatchGateExclusion(member.TaskKey,
                    BatchExclusionReason.SupersededGeneration, pendingReason)
                : BatchGatePolicy.Exclusion(subject, manifest.Scope, options) is { } reason
                    ? new BatchGateExclusion(member.TaskKey, reason)
                    : _store.HasEject(manifest.Scope.Project, member.TaskKey)
                        ? new BatchGateExclusion(member.TaskKey,
                            BatchExclusionReason.OtherBatchOwnsMember, "operator eject")
                        : null;
            if (exclusion is not null)
            {
                rejected.Add(exclusion);
                continue;
            }
            if (options.PreCheckFor(manifest.Scope.Project))
            {
                var verdict = _store.ReadPreCheck(manifest.Scope.Project, subject.ResultSha,
                    manifest.BaseSha, subject.GateProfileDigest);
                var preCheck = BatchGatePreCheckPolicy.Decide(true, verdict?.Outcome,
                    item.EnqueuedAtUtc, _time.GetUtcNow(), options.DeadlineMinutes,
                    item.Subject.OwningBatchId is { } owned
                    && replaceableBatchIds?.Contains(owned) == true);
                if (preCheck != BatchGatePreCheckAction.Admit)
                {
                    rejected.Add(new(member.TaskKey, BatchExclusionReason.PreCheckNotAdmitted,
                        verdict?.Outcome ?? "waiting"));
                    actions.Add((item, null, BatchExclusionReason.PreCheckNotAdmitted));
                    continue;
                }
            }
            if (_git.IsDeliveryPublished(candidate.RepositoryPath, subject.ResultSha,
                    manifest.BaseSha))
            {
                rejected.Add(new(member.TaskKey, BatchExclusionReason.AlreadyIntegrated));
                actions.Add((item, null, BatchExclusionReason.AlreadyIntegrated));
                continue;
            }
            var replay = _git.ReplayBatchMember(candidate.RepositoryPath,
                subject.ResultRef, subject.ResultSha, tip);
            if (replay.ConflictedFiles.Count > 0)
            {
                rejected.Add(new(member.TaskKey, BatchExclusionReason.ReplayConflict,
                    string.Join(", ", replay.ConflictedFiles)));
                actions.Add((item, replay, BatchExclusionReason.ReplayConflict));
                continue;
            }
            switch (BatchGatePolicy.ReplayDisposition(replay))
            {
                case BatchReplayDisposition.Pause:
                    _logger.LogWarning("batch-formation-replay-unavailable task={TaskKey} reason={Reason}",
                        member.TaskKey, replay.Error);
                    return formation with { Manifest = null, UsePerTaskGate = false,
                        Reason = "member replay unavailable before formation: " + member.TaskKey };
                case BatchReplayDisposition.EjectUnattributable:
                    rejected.Add(new(member.TaskKey, BatchExclusionReason.UnattributableReplay,
                        replay.Error));
                    actions.Add((item, replay, BatchExclusionReason.UnattributableReplay));
                    continue;
                default:
                    admitted.Add(subject);
                    tip = replay.TipSha!;
                    break;
            }
        }
        // A remote push may have changed the base while planning or probing.
        // Leave the queue untouched; the next tick plans on the fetched tip.
        var latestBase = _git.FetchBatchIntegrationTip(byKey[manifest.Members[0].TaskKey]
            .RepositoryPath, manifest.Scope.IntegrationBranch, ct);
        if (latestBase is null || !string.Equals(latestBase, manifest.BaseSha,
                StringComparison.OrdinalIgnoreCase))
            return formation with { Manifest = null, UsePerTaskGate = false,
                Reason = "integration base moved before formation" };

        if (!routeRejected && rejected.Count > 0)
            return formation with { Manifest = null, UsePerTaskGate = false,
                Reason = "replacement members changed before formation" };
        foreach (var (item, replay, reason) in actions)
        {
            if (reason == BatchExclusionReason.PreCheckNotAdmitted)
            {
                var verdict = _store.ReadPreCheck(manifest.Scope.Project, item.Subject.ResultSha,
                    manifest.BaseSha, item.Subject.GateProfileDigest);
                if (verdict?.Outcome == "red") SchedulePreCheckRed(item, verdict, ct);
                else SchedulePreCheck(item, manifest.BaseSha, ct);
            }
            else if (reason == BatchExclusionReason.AlreadyIntegrated)
                ScheduleFallback(item, ct, new PerTaskFallbackCause(
                    PerTaskFallbackRules.AlreadyIntegrated,
                    $"the integration branch at {Short(manifest.BaseSha)} already contains the result"),
                    containedInSha: manifest.BaseSha);
            else if (reason == BatchExclusionReason.ReplayConflict)
                await StartConflictRoundAsync(item, new BatchGateReplayRecord(
                    manifest.BatchId, manifest.MembershipDigest, item.Subject.TaskKey,
                    item.Subject.ResultSha, manifest.BaseSha, null, replay!.Replacements,
                    replay.ConflictedFiles, "conflict", _time.GetUtcNow())).ConfigureAwait(false);
            else
                ScheduleFallback(item, ct, new PerTaskFallbackCause(
                    PerTaskFallbackRules.UnattributableReplay,
                    "batch formation could not attribute the replay: "
                    + (replay?.Error ?? "no commit mapping")));
        }
        if (admitted.Count < options.MinimumSize)
            return formation with { Manifest = null, UsePerTaskGate = false,
                Exclusions = formation.Exclusions.Concat(rejected).ToArray(),
                EligibleKeys = admitted.Select(item => item.TaskKey).ToArray(),
                Reason = "admitted members below minimum batch size: "
                    + string.Join("; ", rejected.Select(item =>
                        item.TaskKey + " " + item.Reason + (item.Detail is null ? "" : " (" + item.Detail + ")"))) };
        if (rejected.Count == 0) return formation;
        var kept = admitted.ToArray();
        return formation with { Manifest = manifest with
        {
            Members = kept,
            EligibleKeys = manifest.EligibleKeys.Except(rejected.Select(item => item.TaskKey),
                StringComparer.Ordinal).ToArray(),
            MembershipDigest = BatchGatePolicy.MembershipDigest(manifest.BaseSha,
                manifest.Scope, kept),
            Exclusions = manifest.Exclusions.Concat(rejected).ToArray(),
        }, Exclusions = formation.Exclusions.Concat(rejected).ToArray(),
            EligibleKeys = formation.EligibleKeys.Except(rejected.Select(item => item.TaskKey),
                StringComparer.Ordinal).ToArray() };
    }
}
