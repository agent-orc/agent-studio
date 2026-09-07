namespace AgentStudio.Watcher;

/// <summary>Result of folding one sweep's findings into the durable case set.</summary>
public sealed record WatcherLedgerResult(
    IReadOnlyList<WatcherCase> Cases,
    IReadOnlyList<WatcherCase> Opened,
    IReadOnlyList<WatcherCase> Updated,
    IReadOnlyList<WatcherCase> Resolved,
    IReadOnlyList<WatcherFinding> Suppressed);

/// <summary>
/// Pure case bookkeeping. Given the cases that survived the last sweep and the
/// findings of this one, it produces the next case set.
/// </summary>
/// <remarks>
/// This is the component that makes the Watcher idempotent. Duplicate delivery
/// of the same finding inside one sweep folds into one case, a restart that
/// replays a sweep id does not double-count, and a case that stops being
/// detected reaches an explicit terminal instead of lingering. Nothing here
/// performs I/O, so restart behaviour is testable without a host.
/// </remarks>
public static class WatcherCaseLedger
{
    /// <summary>
    /// Folds <paramref name="findings"/> into <paramref name="existing"/> for
    /// sweep <paramref name="sweepId"/>. Sweep ids must increase; replaying the
    /// same id is treated as the same sweep and does not advance persistence or
    /// quiet counters.
    /// </summary>
    public static WatcherLedgerResult Fold(
        IReadOnlyList<WatcherCase> existing,
        IReadOnlyList<WatcherFinding> findings,
        long sweepId,
        DateTime sweptAt,
        WatcherSuppressionList? suppression = null)
    {
        var byId = existing.ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var opened = new List<WatcherCase>();
        var updated = new List<WatcherCase>();
        var suppressed = new List<WatcherFinding>();
        var seenThisSweep = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            var caseId = WatcherFingerprint.CaseId(finding.DetectorClass, finding.Project, finding.Fingerprint);

            if (suppression?.IsSuppressed(finding.Fingerprint, sweptAt) == true)
            {
                suppressed.Add(finding);
                continue;
            }

            if (!byId.TryGetValue(caseId, out var current))
            {
                var fresh = NewCase(caseId, finding, sweepId);
                byId[caseId] = fresh;
                seenThisSweep.Add(caseId);
                opened.Add(fresh);
                continue;
            }

            // Duplicate delivery inside one sweep appends evidence but must not
            // advance the persistence counter: two sweeps means two sweeps.
            var alreadySeen = !seenThisSweep.Add(caseId) || current.LastSweepId >= sweepId;
            var next = Merge(current, finding, sweepId, alreadySeen);
            byId[caseId] = next;
            updated.Add(next);
        }

        var resolved = new List<WatcherCase>();
        foreach (var (caseId, current) in byId.ToList())
        {
            if (current.IsTerminal) continue;
            if (seenThisSweep.Contains(caseId)) continue;
            if (sweepId - current.LastSweepId < WatcherCasePolicy.QuietSweepsToResolve) continue;

            var closed = current with
            {
                State = WatcherCaseState.Resolved,
                TerminalReason = $"not-detected-for-{WatcherCasePolicy.QuietSweepsToResolve}-sweeps",
            };
            byId[caseId] = closed;
            resolved.Add(closed);
        }

        var ordered = byId.Values
            .OrderBy(c => c.FirstSeenAt)
            .ThenBy(c => c.CaseId, StringComparer.Ordinal)
            .ToList();

        return new WatcherLedgerResult(ordered, opened, updated, resolved, suppressed);
    }

    /// <summary>
    /// Cases that have survived the persistence check and still need a
    /// proposal. This is the exact admission gate for W2: no analysis and no
    /// draft before two sweeps, and never twice for one case.
    /// </summary>
    public static IReadOnlyList<WatcherCase> AwaitingProposal(IReadOnlyList<WatcherCase> cases)
        =>
        [
            .. cases
                .Where(c => !c.IsTerminal)
                .Where(c => c.IsPersistent)
                .Where(c => string.IsNullOrWhiteSpace(c.ProposalId))
                .OrderBy(c => c.FirstSeenAt)
                .ThenBy(c => c.CaseId, StringComparer.Ordinal),
        ];

    private static WatcherCase NewCase(string caseId, WatcherFinding finding, long sweepId)
        => new()
        {
            CaseId = caseId,
            DetectorClass = finding.DetectorClass,
            Fingerprint = finding.Fingerprint,
            Project = finding.Project,
            DetectorRule = finding.DetectorRule,
            Summary = finding.Summary,
            FirstSeenAt = finding.ObservedAt,
            LastSeenAt = finding.ObservedAt,
            Occurrences = 1,
            SweepCount = 1,
            LastSweepId = sweepId,
            AffectedCards = finding.AffectedCards,
            Evidence = finding.Evidence,
            EvidencePackDigest = WatcherFingerprint.EvidenceDigest(finding.Evidence),
            State = WatcherCaseState.Open,
        };

    private static WatcherCase Merge(WatcherCase current, WatcherFinding finding, long sweepId, bool alreadySeenThisSweep)
    {
        var cards = current.AffectedCards
            .Concat(finding.AffectedCards)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(card => card, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return current with
        {
            // A terminal case that is detected again reopens rather than
            // spawning a second case for the same fingerprint.
            State = current.IsTerminal ? WatcherCaseState.Open : current.State,
            TerminalReason = current.IsTerminal ? null : current.TerminalReason,
            Summary = finding.Summary,
            LastSeenAt = finding.ObservedAt > current.LastSeenAt ? finding.ObservedAt : current.LastSeenAt,
            Occurrences = current.Occurrences + 1,
            SweepCount = alreadySeenThisSweep ? current.SweepCount : current.SweepCount + 1,
            LastSweepId = Math.Max(current.LastSweepId, sweepId),
            AffectedCards = cards,
            Evidence = finding.Evidence,
            EvidencePackDigest = WatcherFingerprint.EvidenceDigest(finding.Evidence),
        };
    }
}
