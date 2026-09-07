using Xunit;

namespace AgentStudio.Tests.Watcher;

/// <summary>
/// Deduplication, persistence across sweeps, restart behaviour, terminals, and
/// suppression. These are the properties that let the Watcher consume
/// at-least-once input without producing duplicate cases or duplicate spend.
/// </summary>
public sealed class WatcherCaseLedgerTests
{
    private static readonly DateTime T0 = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameFingerprintTwiceInOneSweepIsOneCaseAndOneSweepOfPersistence()
    {
        var finding = Finding("fp-1");

        var result = WatcherCaseLedger.Fold([], [finding, finding], sweepId: 1, T0);

        var single = Assert.Single(result.Cases);
        Assert.Equal(2, single.Occurrences);
        Assert.Equal(1, single.SweepCount);
        Assert.False(single.IsPersistent);
    }

    [Fact]
    public void TwoSweepsMakeACasePersistentAndEligibleForAProposal()
    {
        var first = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 1, T0);
        Assert.Empty(WatcherCaseLedger.AwaitingProposal(first.Cases));

        var second = WatcherCaseLedger.Fold(first.Cases, [Finding("fp-1", T0.AddMinutes(5))], sweepId: 2, T0.AddMinutes(5));

        var single = Assert.Single(second.Cases);
        Assert.Equal(2, single.SweepCount);
        Assert.True(single.IsPersistent);
        Assert.Single(WatcherCaseLedger.AwaitingProposal(second.Cases));
    }

    [Fact]
    public void ReplayingTheSameSweepIdAfterRestartDoesNotAdvancePersistence()
    {
        var first = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 1, T0);

        // A restart re-reads the durable case set and replays the sweep it was
        // in the middle of. That must not count as a second sweep.
        var replay = WatcherCaseLedger.Fold(first.Cases, [Finding("fp-1")], sweepId: 1, T0);

        var single = Assert.Single(replay.Cases);
        Assert.Equal(1, single.SweepCount);
        Assert.False(single.IsPersistent);
    }

    [Fact]
    public void CaseIdentitySurvivesARestartBecauseItIsDerivedNotGenerated()
    {
        var before = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 1, T0).Cases;

        // Simulate a process restart: the ledger is rebuilt from nothing and
        // the same finding arrives again.
        var afterRestart = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 2, T0.AddMinutes(5)).Cases;

        Assert.Equal(before[0].CaseId, afterRestart[0].CaseId);
    }

    [Fact]
    public void DistinctFingerprintsInOneProjectProduceDistinctCases()
    {
        // Regression: the case id used to be built by normalizing the
        // fingerprint, which collapsed every hex digest to one placeholder and
        // gave all cases of one project the same id.
        var result = WatcherCaseLedger.Fold(
            [],
            [Finding("aaaaaaaa1111bbbb"), Finding("cccccccc2222dddd")],
            sweepId: 1,
            T0);

        Assert.Equal(2, result.Cases.Count);
        Assert.Equal(2, result.Cases.Select(c => c.CaseId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ACaseThatStopsBeingDetectedReachesAnExplicitTerminal()
    {
        var sweep1 = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 1, T0);
        var sweep2 = WatcherCaseLedger.Fold(sweep1.Cases, [], sweepId: 2, T0.AddMinutes(5));
        Assert.Empty(sweep2.Resolved);

        var sweep3 = WatcherCaseLedger.Fold(sweep2.Cases, [], sweepId: 3, T0.AddMinutes(10));

        var closed = Assert.Single(sweep3.Resolved);
        Assert.Equal(WatcherCaseState.Resolved, closed.State);
        Assert.False(string.IsNullOrWhiteSpace(closed.TerminalReason));
        Assert.True(closed.IsTerminal);
    }

    [Fact]
    public void ATerminalCaseDetectedAgainReopensInsteadOfForkingASecondCase()
    {
        var sweep1 = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 1, T0);
        var sweep2 = WatcherCaseLedger.Fold(sweep1.Cases, [], sweepId: 2, T0.AddMinutes(5));
        var sweep3 = WatcherCaseLedger.Fold(sweep2.Cases, [], sweepId: 3, T0.AddMinutes(10));
        Assert.Single(sweep3.Cases);

        var sweep4 = WatcherCaseLedger.Fold(
            sweep3.Cases, [Finding("fp-1", T0.AddMinutes(15))], sweepId: 4, T0.AddMinutes(15));

        var single = Assert.Single(sweep4.Cases);
        Assert.Equal(WatcherCaseState.Open, single.State);
        Assert.Null(single.TerminalReason);
    }

    [Fact]
    public void AProposedCaseIsNotOfferedForAProposalASecondTime()
    {
        var sweep1 = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 1, T0);
        var sweep2 = WatcherCaseLedger.Fold(sweep1.Cases, [Finding("fp-1")], sweepId: 2, T0.AddMinutes(5));

        var proposed = sweep2.Cases.Select(c => c with { ProposalId = "wp-1" }).ToList();

        Assert.Empty(WatcherCaseLedger.AwaitingProposal(proposed));
    }

    [Fact]
    public void ASuppressedFingerprintOpensNoCaseAndIsReportedAsSuppressed()
    {
        var suppression = WatcherSuppressionList.Empty
            .Suppress(Finding("fp-1").Fingerprint, "operator rejected: planned maintenance", T0);

        var result = WatcherCaseLedger.Fold([], [Finding("fp-1")], sweepId: 1, T0, suppression);

        Assert.Empty(result.Cases);
        Assert.Single(result.Suppressed);
    }

    [Fact]
    public void SuppressionExpiresAndTheFindingReturns()
    {
        var suppression = WatcherSuppressionList.Empty
            .Suppress(Finding("fp-1").Fingerprint, "rejected", T0, TimeSpan.FromDays(1));

        var afterExpiry = T0.AddDays(2);
        var result = WatcherCaseLedger.Fold(
            [], [Finding("fp-1", afterExpiry)], sweepId: 1, afterExpiry, suppression);

        Assert.Single(result.Cases);
        Assert.Empty(result.Suppressed);
        Assert.Empty(suppression.Active(afterExpiry));
    }

    [Fact]
    public void RejectingTheSameFingerprintTwiceRefreshesRatherThanStacks()
    {
        var suppression = WatcherSuppressionList.Empty
            .Suppress("fp-1", "first", T0)
            .Suppress("fp-1", "second", T0.AddDays(1));

        var entry = Assert.Single(suppression.Entries);
        Assert.Equal("second", entry.Reason);
        Assert.Equal(T0.AddDays(1), entry.SuppressedAt);
    }

    private static WatcherFinding Finding(string fingerprint, DateTime? at = null)
        => new(
            ObservedAt: at ?? T0,
            DetectorClass: WatcherDetectorClass.Repetition,
            Fingerprint: fingerprint,
            DetectorRule: WatcherDetectors.Rules.Repetition,
            Summary: $"summary for {fingerprint}",
            AffectedCards: ["AGT-1"],
            Evidence: [new WatcherEvidenceItem("fingerprint", fingerprint, "test")],
            Project: "AGT");
}
