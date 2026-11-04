namespace AgentStudio.Pipeline;

public sealed partial class BatchGatePilotService
{
    private string? WaitingFormationReason(BatchGateManifest proposed,
        BatchGateFormationOptions options)
    {
        var waiting = _store.ListRecoverableManifests()
            .Where(manifest => manifest.Scope == proposed.Scope
                && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
            .OrderByDescending(manifest => manifest.Members.Count)
            .ThenBy(manifest => manifest.ClosedAtUtc).FirstOrDefault();
        if (waiting is null) return null;
        if (waiting.Members.Count >= options.MaximumSize)
            return $"new batch: waiting batch {waiting.BatchId} reached maximumSize {options.MaximumSize}";
        if (options.Overlap != "separate") return null;
        var pending = _store.ListPending().Select(Refresh)
            .ToDictionary(item => item.Subject.TaskKey, StringComparer.Ordinal);
        var conflicts = proposed.Members.SelectMany(member => waiting.Members.SelectMany(other =>
        {
            if (!pending.TryGetValue(member.TaskKey, out var left)
                || !pending.TryGetValue(other.TaskKey, out var right))
                return Array.Empty<string>();
            return BatchGateWaitingPolicy.ConflictingFiles(
                new(member.TaskKey, _snapshots.Paths(left, _time.GetUtcNow())),
                new(other.TaskKey, _snapshots.Paths(right, _time.GetUtcNow())));
        })).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return conflicts.Length == 0 ? null
            : $"new batch: overlap separate from waiting batch {waiting.BatchId}, files "
                + string.Join(", ", conflicts);
    }

    private BatchGateFormation SeparateThresholdOverlap(BatchGateFormation formation,
        IReadOnlyList<BatchGatePendingRecord> current, BatchGateScope scope,
        string baseSha, BatchGateFormationOptions options)
    {
        if (options.Overlap != "separate" || formation.Manifest is null) return formation;
        var cards = formation.Manifest.Members.Select(member =>
        {
            var item = current.Single(candidate => candidate.Subject.TaskKey == member.TaskKey);
            return new BatchGateWaitingPolicy.Card(member.TaskKey,
                _snapshots.Paths(item, _time.GetUtcNow()));
        }).ToArray();
        var selected = BatchGateWaitingPolicy.LargestCompatible(cards,
            options.MaximumSize, options.Overlap);
        if (selected.Count == cards.Length) return formation;
        var selectedKeys = selected.Select(card => card.TaskKey).ToHashSet(StringComparer.Ordinal);
        var exclusions = cards.Where(card => !selectedKeys.Contains(card.TaskKey))
            .Select(card => new BatchGateExclusion(card.TaskKey,
                BatchExclusionReason.OverlapSeparate,
                "overlap separate: " + string.Join(", ", selected.SelectMany(member =>
                    BatchGateWaitingPolicy.ConflictingFiles(card, member))
                    .Distinct(StringComparer.OrdinalIgnoreCase))))
            .ToArray();
        if (selected.Count < options.MinimumSize)
            return new(null, exclusions, selected.Select(card => card.TaskKey).ToArray(),
                UsePerTaskGate: true);
        var closed = BatchGatePolicy.Close(current.Select(item => item.Subject), scope,
            baseSha, options, selected.Select(card => card.TaskKey).ToArray(),
            _time.GetUtcNow());
        return closed with
        {
            Manifest = closed.Manifest is null ? null : closed.Manifest with
            {
                Exclusions = closed.Manifest.Exclusions
                    .Where(exclusion => !exclusions.Any(overlap => overlap.TaskKey == exclusion.TaskKey))
                    .Concat(exclusions).ToArray(),
            },
        };
    }

    /// <summary>
    /// A closed snapshot is immutable. Before starting the next flight, retire
    /// only the formed snapshots whose members move into a larger snapshot.
    /// A flight already past Formed is never considered here.
    /// </summary>
    private void RepackFormedBatches()
    {
        var formed = _store.ListRecoverableManifests()
            .Where(manifest => manifest.ParentBatchId is null
                && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
            .ToArray();
        if (formed.Length == 0) return;
        var pending = _store.ListPending().Select(Refresh).ToArray();
        var activeKeys = _store.ListRecoverableManifests()
            .SelectMany(manifest => manifest.Members)
            .Select(member => member.TaskKey).ToHashSet(StringComparer.Ordinal);
        foreach (var group in formed.GroupBy(manifest => (manifest.Scope, manifest.BaseSha)))
        {
            var scope = group.Key.Scope;
            var baseSha = group.Key.BaseSha;
            var options = _settings.Get(scope.Project).BatchGate;
            if (options.Planner != "threshold") continue;
            var waitingManifests = group.OrderByDescending(manifest => manifest.Members.Count)
                .ThenBy(manifest => manifest.ClosedAtUtc)
                .ThenBy(manifest => manifest.BatchId, StringComparer.Ordinal).ToArray();
            var ownedKeys = waitingManifests.SelectMany(manifest => manifest.Members)
                .Select(member => member.TaskKey).ToHashSet(StringComparer.Ordinal);
            var eligible = pending.Where(item => item.Subject.Project == scope.Project
                    && item.Subject.Repository == scope.Repository
                    && item.Subject.IntegrationBranch == scope.IntegrationBranch
                    && item.Subject.GateProfileDigest == scope.GateProfileDigest
                    && item.Subject.PlatformVersion == scope.PlatformVersion
                    && item.Subject.OwningBatchId is null
                    && !activeKeys.Contains(item.Subject.TaskKey)
                    && _snapshots.PendingExclusion(item) is null
                    && !HeldOnRedBase(item)
                    && BatchGatePolicy.Exclusion(item.Subject, scope, options) is null
                    && (!options.PreCheckFor(scope.Project)
                        || BatchGatePreCheckPolicy.Decide(true,
                            _store.ReadPreCheck(scope.Project, item.Subject.ResultSha,
                                baseSha, item.Subject.GateProfileDigest)?.Outcome,
                            item.EnqueuedAtUtc, _time.GetUtcNow(), options.DeadlineMinutes,
                            false) == BatchGatePreCheckAction.Admit))
                .OrderBy(item => item.Subject.EnqueueSequence).ToArray();
            if (waitingManifests.Length == 1 && eligible.Length == 0) continue;
            var all = waitingManifests.SelectMany(manifest => manifest.Members)
                .Select(member => pending.FirstOrDefault(item => item.Subject.TaskKey == member.TaskKey
                    && item.Subject.RunAttempt == member.RunAttempt))
                .Where(item => item is not null).Cast<BatchGatePendingRecord>()
                .Concat(eligible.Where(item => !ownedKeys.Contains(item.Subject.TaskKey)))
                .DistinctBy(item => item.Subject.TaskKey).ToArray();
            if (all.Length == 0) continue;
            var cards = all.Select(item => new BatchGateWaitingPolicy.Card(item.Subject.TaskKey,
                _snapshots.Paths(item, _time.GetUtcNow()))).ToArray();
            var selected = BatchGateWaitingPolicy.LargestCompatible(cards,
                options.MaximumSize, options.Overlap);
            var selectedKeys = selected.Select(card => card.TaskKey).ToHashSet(StringComparer.Ordinal);
            var moving = waitingManifests.Where(manifest => manifest.Members.Any(member =>
                selectedKeys.Contains(member.TaskKey))).ToArray();
            var alreadyFormed = moving.Length == 1 && eligible.All(item =>
                    !selectedKeys.Contains(item.Subject.TaskKey))
                && moving[0].Members.Select(member => member.TaskKey)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(selectedKeys);
            if (alreadyFormed)
            {
                RecordWaitingExclusions(waitingManifests.Where(manifest =>
                    manifest.BatchId != moving[0].BatchId).ToArray(), cards, selected, options);
                continue;
            }
            if (selected.Count < options.MinimumSize || moving.Length == 0) continue;

            var clean = all.Select(item => item.Subject with { OwningBatchId = null }).ToArray();
            var close = BatchGatePolicy.Close(clean, scope, baseSha, options,
                selected.Select(card => card.TaskKey).ToArray(), _time.GetUtcNow());
            if (close.Manifest is null) continue;
            var exclusions = close.Manifest.Exclusions.Select(exclusion =>
            {
                var card = cards.First(item => item.TaskKey == exclusion.TaskKey);
                var conflicts = selected.SelectMany(member =>
                    BatchGateWaitingPolicy.ConflictingFiles(card, member)).Distinct(
                    StringComparer.OrdinalIgnoreCase).ToArray();
                return conflicts.Length == 0 || options.Overlap != "separate"
                    ? exclusion with { Reason = BatchExclusionReason.MaximumSize,
                        Detail = $"maximumSize {options.MaximumSize}" }
                    : exclusion with { Reason = BatchExclusionReason.OverlapSeparate,
                        Detail = "overlap separate: " + string.Join(", ", conflicts) };
            }).ToArray();
            var replacement = close.Manifest with { Exclusions = exclusions };
            lock (_runningBatches)
            {
                if (moving.Any(old => _assemblingBatchIds.Contains(old.BatchId)
                    || _store.LatestState(old.BatchId).Phase != BatchPhase.Formed))
                    continue;
                foreach (var old in moving)
                {
                    ReturnToPending(old);
                    State(old, BatchPhase.Abandoned, null, 0,
                        $"formed batch replaced by {replacement.BatchId} before gate start");
                    // A snapshot with a flight waiting for the reservation
                    // triggers a successor tick when that flight exits.
                    if (_startedBatchIds.Contains(old.BatchId))
                        _replanAfterBatch.TryAdd(old.BatchId, 0);
                }
            }
            _store.CloseManifest(replacement);
            foreach (var member in replacement.Members)
            {
                var item = all.Single(candidate => candidate.Subject.TaskKey == member.TaskKey);
                var task = _scanner.FindJob(member.TaskKey, item.WatchPath)
                    ?? throw new InvalidDataException("Batch member disappeared during repack.");
                BatchGateOwnershipStore.Write(task.FolderPath,
                    new BatchGateOwnership(item.ReviewAttemptId, member, replacement.BatchId));
            }
            RecordWaitingExclusions(waitingManifests.Except(moving).ToArray(),
                cards, selected, options);
        }
    }

    private void RecordWaitingExclusions(IReadOnlyList<BatchGateManifest> manifests,
        IReadOnlyList<BatchGateWaitingPolicy.Card> cards,
        IReadOnlyList<BatchGateWaitingPolicy.Card> selected,
        BatchGateFormationOptions options)
    {
        foreach (var manifest in manifests)
        {
            if (_store.LatestState(manifest.BatchId).Phase != BatchPhase.Formed) continue;
            var conflicts = manifest.Members.SelectMany(member =>
                selected.SelectMany(chosen => BatchGateWaitingPolicy.ConflictingFiles(
                    cards.First(card => card.TaskKey == member.TaskKey), chosen)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var reason = options.Overlap == "separate" && conflicts.Length > 0
                ? "new batch: overlap separate, files " + string.Join(", ", conflicts)
                : $"new batch: maximumSize {options.MaximumSize}";
            if (_store.LatestState(manifest.BatchId).Reason != reason)
                State(manifest, BatchPhase.Formed, null, 0, reason);
        }
    }
}
