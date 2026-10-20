namespace AgentStudio.Watcher;

/// <summary>Result of folding one sweep's observations into the durable case store.</summary>
public sealed record WatcherIngestResult(
    IReadOnlyList<WatcherCase> UpdatedCases,
    IReadOnlyList<WatcherCase> ReadyForProposal);

/// <summary>
/// Pure, restart-safe correlation engine: folds normalized signal
/// observations into durable <see cref="WatcherCase"/> records, deduplicated
/// by fingerprint (§2 case contract). Deterministic and model-free - no
/// detector, no analysis, no proposal drafting happens here. Testable the
/// same way as <c>AcceptanceRailHostedService.RunOnceAsync</c>.
/// </summary>
public sealed class WatcherCaseEngine
{
    private readonly WatcherCaseStore _store;
    private readonly WatcherSuppressionStore _suppressions;

    public WatcherCaseEngine(WatcherCaseStore store, WatcherSuppressionStore suppressions)
    {
        _store = store;
        _suppressions = suppressions;
    }

    public WatcherIngestResult Ingest(
        string workspaceRoot,
        IReadOnlyList<WatcherSignalObservation> observations,
        string sweepId,
        WatcherOptions options,
        DateTime nowUtc)
    {
        var updated = new List<WatcherCase>();
        var readyForProposal = new List<WatcherCase>();

        foreach (var obs in observations)
        {
            var fingerprint = WatcherFingerprint.Compute(obs);
            var existing = _store.Find(workspaceRoot, fingerprint);
            var isNewSweep = existing == null || !string.Equals(existing.LastSweepId, sweepId, StringComparison.Ordinal);
            var sweepCount = existing == null ? 1 : (isNewSweep ? existing.SweepCount + 1 : existing.SweepCount);

            var suppressed = _suppressions.IsSuppressed(workspaceRoot, fingerprint, nowUtc);
            var state = existing?.State ?? WatcherCaseStates.Open;
            if (suppressed) state = WatcherCaseStates.Suppressed;
            else if (state == WatcherCaseStates.Suppressed
                || state == WatcherCaseStates.Resolved && obs.Subject is not null)
                state = WatcherCaseStates.Open;

            var mergedCards = obs.Subject is not null
                ? obs.AffectedCards.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : (existing?.AffectedCards ?? [])
                    .Union(obs.AffectedCards, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var updatedCase = new WatcherCase
            {
                Id = existing?.Id ?? _store.AllocateCaseId(workspaceRoot),
                Fingerprint = fingerprint,
                DetectorClass = obs.DetectorClass,
                Project = obs.Project,
                AffectedCards = mergedCards,
                FirstSeenUtc = existing?.FirstSeenUtc ?? obs.ObservedAtUtc,
                LastSeenUtc = obs.ObservedAtUtc,
                OccurrenceCount = (existing?.OccurrenceCount ?? 0) + 1,
                SweepCount = sweepCount,
                LastSweepId = sweepId,
                State = state,
                LastSummary = obs.Summary,
                LastDetails = obs.Details,
                SourcePaths = obs.SourcePaths,
                EvidenceDigest = existing?.EvidenceDigest,
                ProposalId = existing?.ProposalId,
                ProposalJobId = existing?.ProposalJobId,
                IsCommentOnly = existing?.IsCommentOnly ?? false,
                GaveUpReason = existing?.GaveUpReason,
                Subject = obs.Subject,
                Severity = obs.Severity,
                SinceUtc = obs.SinceUtc,
                BlockedCount = obs.BlockedCount,
                SuggestedAction = obs.SuggestedAction,
            };
            _store.Save(workspaceRoot, updatedCase);
            updated.Add(updatedCase);

            var alreadyHasProposal = updatedCase.ProposalId != null;
            if (!suppressed && !alreadyHasProposal && updatedCase.SweepCount >= options.PersistenceSweepsBeforeProposal)
                readyForProposal.Add(updatedCase);
        }

        return new WatcherIngestResult(updated, readyForProposal);
    }

    public IReadOnlyList<WatcherCase> ResolveClearedOperationalCases(
        string workspaceRoot, IReadOnlyList<WatcherSignalObservation> current, DateTime nowUtc)
    {
        var active = current.Where(x => x.FingerprintKey.StartsWith("operational:", StringComparison.Ordinal))
            .Select(WatcherFingerprint.Compute).ToHashSet(StringComparer.Ordinal);
        var cleared = _store.All(workspaceRoot)
            .Where(x => x.Subject is not null
                && x.State is WatcherCaseStates.Open or WatcherCaseStates.DecisionRequired
                && !active.Contains(x.Fingerprint))
            .Select(x => x with { State = WatcherCaseStates.Resolved, LastSeenUtc = nowUtc })
            .ToList();
        foreach (var item in cleared) _store.Save(workspaceRoot, item);
        return cleared;
    }
}
