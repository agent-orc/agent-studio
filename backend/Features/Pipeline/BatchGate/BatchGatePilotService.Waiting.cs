using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

public sealed partial class BatchGatePilotService
{
    private bool ProjectGateBusy(BatchGateScope scope)
    {
        lock (_runningBatches)
            if (_runningBatches.TryGetValue(scope.Project, out var flight)
                && !flight.IsCompleted) return true;
        string[] fallbackAttempts;
        lock (_fallbackFlights)
            fallbackAttempts = _fallbackFlights.Where(pair => !pair.Value.IsCompleted)
                .Select(pair => pair.Key).ToArray();
        if (_store.ListPending().Any(item => item.Subject.Project == scope.Project
            && fallbackAttempts.Contains(item.ReviewAttemptId, StringComparer.Ordinal)))
            return true;
        return _leases.IsBusy(scope);
    }

    private bool CanJoinWaitingBatch(BatchGatePendingRecord item, BatchGateScope scope,
        string baseSha, BatchGateFormationOptions options)
    {
        var waiting = _store.ListRecoverableManifests()
            .Where(manifest => manifest.Scope == scope && manifest.BaseSha == baseSha
                && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Formed)
            .ToArray();
        if (waiting.Length == 0) return false;
        var activeKeys = _store.ListRecoverableManifests()
            .SelectMany(manifest => manifest.Members)
            .Select(member => member.TaskKey).ToHashSet(StringComparer.Ordinal);
        if (!EligibleForWaitingBatch(item, scope, baseSha, options, activeKeys)) return false;
        var pending = _store.ListPending().Select(Refresh)
            .ToDictionary(candidate => candidate.Subject.TaskKey, StringComparer.Ordinal);
        var card = Card(item);
        return waiting.Any(manifest => manifest.Members.Count < options.MaximumSize
            && manifest.Members.All(member => pending.TryGetValue(member.TaskKey, out var other)
                && (options.Overlap != "separate" || MonolithTransitionAdapter.BatchConflictingFiles(
                    card, Card(other)).Count == 0)));
    }

    private bool EligibleForWaitingBatch(BatchGatePendingRecord item,
        BatchGateScope scope, string baseSha, BatchGateFormationOptions options,
        IReadOnlySet<string> activeKeys)
        => item.Subject.Project == scope.Project
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
                    false) == BatchGatePreCheckAction.Admit);

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
            return MonolithTransitionAdapter.BatchConflictingFiles(Card(left), Card(right));
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
            Card(current.Single(candidate => candidate.Subject.TaskKey == member.TaskKey))).ToArray();
        var selected = MonolithTransitionAdapter.BatchLargestCompatible(cards,
            options.MaximumSize, options.Overlap);
        if (selected.Count == cards.Length) return formation;
        var selectedKeys = selected.Select(card => card.TaskKey).ToHashSet(StringComparer.Ordinal);
        var exclusions = cards.Where(card => !selectedKeys.Contains(card.TaskKey))
            .Select(card => new BatchGateExclusion(card.TaskKey,
                BatchExclusionReason.OverlapSeparate,
                "overlap separate: " + string.Join(", ", selected.SelectMany(member =>
                    MonolithTransitionAdapter.BatchConflictingFiles(card, member))
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
}
