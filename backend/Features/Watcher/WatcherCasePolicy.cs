namespace AgentStudio.Watcher;

/// <summary>What one sweep changed about the durable case set.</summary>
/// <param name="Cases">Every case after the fold, terminals included.</param>
/// <param name="Opened">Cases this sweep saw for the first time or reopened.</param>
/// <param name="Closed">Cases this sweep drove to a terminal.</param>
/// <param name="Touched">Cases whose counters this sweep advanced.</param>
public sealed record WatcherCaseFold(
    IReadOnlyList<WatcherCase> Cases,
    IReadOnlyList<WatcherCase> Opened,
    IReadOnlyList<WatcherCase> Closed,
    IReadOnlyList<WatcherCase> Touched);

/// <summary>
/// Pure folding of one sweep's findings into the durable case set. This is
/// where deduplication, the two-sweep persistence check, suppression, and the
/// explicit terminals live, so all four are testable without a filesystem.
/// </summary>
public static class WatcherCasePolicy
{
    /// <summary>Sweeps a fingerprint must survive before it may be analysed.</summary>
    public const int PersistenceSweeps = 2;

    public static class TerminalReasons
    {
        public const string SignalStopped = "The signal stopped before the case reached a proposal.";
        public const string Suppressed = "The fingerprint is on the suppression list after an operator rejection.";
    }

    /// <summary>
    /// Fold findings into cases. Identical findings deduplicate onto one case
    /// by fingerprint; a case the sweep no longer sees reaches an explicit
    /// terminal rather than lingering as a silent open row.
    /// </summary>
    /// <param name="existing">The durable case set before this sweep.</param>
    /// <param name="findings">What the detectors concluded this sweep.</param>
    /// <param name="suppressions">Suppression list, expired entries included.</param>
    /// <param name="nowUtc">Sweep instant.</param>
    /// <param name="sweepHadSignals">
    /// False when collection produced nothing at all. A blind sweep is not
    /// evidence that a problem went away, so it closes nothing.
    /// </param>
    public static WatcherCaseFold Fold(
        IReadOnlyList<WatcherCase> existing,
        IReadOnlyList<WatcherFinding> findings,
        IReadOnlyList<WatcherSuppression> suppressions,
        DateTime nowUtc,
        bool sweepHadSignals = true)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(suppressions);

        var byId = existing.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var suppressedFingerprints = suppressions
            .Where(item => item.IsActiveAt(nowUtc))
            .Select(item => item.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        var opened = new List<WatcherCase>();
        var touched = new List<WatcherCase>();
        var closed = new List<WatcherCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Deduplicate within the sweep first: two detectors must never write
        // the same fingerprint twice in one cycle.
        foreach (var finding in findings)
        {
            var id = WatcherIdentity.CaseId(finding.Fingerprint);
            if (!seen.Add(id)) continue;

            var previous = byId.GetValueOrDefault(id);
            var folded = Fold(previous, finding, id, suppressedFingerprints.Contains(finding.Fingerprint), nowUtc);
            byId[id] = folded;

            if (previous is null || WatcherCaseStates.IsTerminal(previous.State)) opened.Add(folded);
            else touched.Add(folded);
            if (WatcherCaseStates.IsTerminal(folded.State) && !WatcherCaseStates.IsTerminal(previous?.State))
                closed.Add(folded);
        }

        if (sweepHadSignals)
        {
            foreach (var stale in existing.Where(item =>
                         !WatcherCaseStates.IsTerminal(item.State) && !seen.Contains(item.Id)))
            {
                var resolved = stale with
                {
                    State = WatcherCaseStates.Resolved,
                    TerminalReason = TerminalReasons.SignalStopped,
                    UpdatedAtUtc = nowUtc,
                };
                byId[resolved.Id] = resolved;
                closed.Add(resolved);
            }
        }

        return new WatcherCaseFold(
            byId.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList(),
            opened,
            closed,
            touched);
    }

    /// <summary>
    /// Cases that survived the persistence check and may spend contingent on
    /// analysis and a proposal. A case that already produced a proposal is not
    /// eligible again, which is how "one strong call per evidence fingerprint"
    /// is enforced across sweeps.
    /// </summary>
    public static IReadOnlyList<WatcherCase> EligibleForProposal(IEnumerable<WatcherCase> cases) => cases
        .Where(item => item.SweepCount >= PersistenceSweeps)
        .Where(item => item.ProposalId is null)
        .Where(item => item.State is WatcherCaseStates.Open
            or WatcherCaseStates.Analysed
            or WatcherCaseStates.Backlogged)
        .OrderBy(item => item.FirstSeenAtUtc)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToList();

    private static WatcherCase Fold(
        WatcherCase? previous,
        WatcherFinding finding,
        string id,
        bool suppressed,
        DateTime nowUtc)
    {
        var cards = Merge(previous?.AffectedCards, finding.AffectedCards);
        var evidenceDigest = WatcherIdentity.EvidenceDigest(finding.Evidence);

        // A terminal case that starts producing its signal again reopens with a
        // fresh persistence count, so a flapping fingerprint cannot skip the
        // two-sweep check by having been resolved once.
        var reopening = previous is null || WatcherCaseStates.IsTerminal(previous.State);

        var state = suppressed
            ? WatcherCaseStates.Suppressed
            : reopening
                ? WatcherCaseStates.Open
                : previous!.State;

        return new WatcherCase
        {
            Id = id,
            Fingerprint = finding.Fingerprint,
            FingerprintDigest = WatcherIdentity.Digest(finding.Fingerprint),
            DetectorClass = finding.DetectorClass,
            DetectorRule = finding.DetectorRule,
            Title = finding.Title,
            Project = finding.Project,
            FirstSeenAtUtc = reopening
                ? finding.FirstSeenAtUtc
                : Min(previous!.FirstSeenAtUtc, finding.FirstSeenAtUtc),
            LastSeenAtUtc = Max(previous?.LastSeenAtUtc, finding.LastSeenAtUtc),
            Occurrences = Math.Max(previous is null || reopening ? 0 : previous.Occurrences, finding.Occurrences),
            SweepCount = reopening ? 1 : previous!.SweepCount + 1,
            AffectedCards = cards,
            Evidence = finding.Evidence,
            EvidenceDigest = evidenceDigest,
            UncertainCause = finding.UncertainCause,
            State = state,
            ProposalId = reopening ? null : previous!.ProposalId,
            TerminalReason = suppressed ? TerminalReasons.Suppressed : null,
            UpdatedAtUtc = nowUtc,
        };
    }

    private static List<string> Merge(IReadOnlyList<string>? left, IReadOnlyList<string> right) =>
        (left ?? []).Concat(right)
        .Where(card => !string.IsNullOrWhiteSpace(card))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(card => card, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;

    private static DateTime Max(DateTime? left, DateTime right) =>
        left is null || right >= left.Value ? right : left.Value;
}
