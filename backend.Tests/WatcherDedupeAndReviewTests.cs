using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Deduplication, restart survival, the comment-instead-of-a-second-card rule,
/// and the review mode that keeps a proposal out of Ready until an operator
/// answers.
/// </summary>
public sealed class WatcherDedupeAndReviewTests
{
    // -------------------------------------------------------------- dedupe

    [Fact]
    public async Task RepeatedSweeps_KeepOneCasePerFingerprint()
    {
        using var harness = new WatcherHarness();

        for (var sweep = 0; sweep < 5; sweep++)
        {
            await harness.SweepAsync(
                WatcherFixtureMatrix.MergedSweep(WatcherFixtureMatrix.NowUtc.AddMinutes(5 * sweep)));
        }

        Assert.Equal(8, harness.Store.Cases().Count);
        Assert.Equal(8, harness.Store.Proposals().Count);
        Assert.Equal(
            8,
            harness.Store.Cases().Select(row => row.CaseId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RepeatedSweeps_CountSweepsWithoutRaisingTheCaseTwice()
    {
        using var harness = new WatcherHarness();

        await harness.SweepAsync(WatcherFixtureMatrix.MergedSweep());
        var second = await harness.SweepAsync(
            WatcherFixtureMatrix.MergedSweep(WatcherFixtureMatrix.NowUtc.AddMinutes(5)));

        Assert.Equal(0, second.CasesOpened);
        Assert.Equal(8, second.CasesUpdated);
        Assert.All(harness.Store.Cases(), row => Assert.Equal(2, row.SweepsSeen));
    }

    [Fact]
    public async Task ASecondCliWithTheSameFaultGetsItsOwnCase()
    {
        using var harness = new WatcherHarness();
        var claude = WatcherFixtureMatrix.ById("probe-error-repeat").Signals.Probes;
        // The Codex hook modal of dossier row 1: the same rule, a different CLI.
        var codex = claude.Select(probe => probe with { CliType = "codex" }).ToList();
        var input = new WatcherSweepInput
        {
            NowUtc = WatcherFixtureMatrix.NowUtc,
            Probes = [.. claude, .. codex],
        };

        await harness.SweepAsync(input);

        var cases = harness.Store.Cases();
        Assert.Equal(2, cases.Count);
        Assert.Equal(2, cases.Select(row => row.Fingerprint).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, row => Assert.Equal(WatcherDetectorRules.ProbeErrorRepeat, row.DetectorRule));
    }

    [Fact]
    public async Task ACaseWhoseSignalStopsReachesAnExplicitTerminal()
    {
        using var harness = new WatcherHarness();

        await harness.SweepAsync(WatcherFixtureMatrix.MergedSweep());
        var quiet = await harness.SweepAsync(
            WatcherSweepInput.Empty(WatcherFixtureMatrix.NowUtc.AddMinutes(5)));

        Assert.Equal(8, quiet.CasesClosed);
        Assert.All(harness.Store.Cases(), row =>
        {
            Assert.Equal(WatcherCaseStates.GaveUp, row.State);
            Assert.True(row.IsTerminal);
        });
    }

    // ------------------------------------------------------------- restart

    [Fact]
    public async Task Restart_ResumesTheCaseSetWithoutDuplicatingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "watcher-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string[] idsBefore;
            using (var before = new WatcherHarness(root))
            {
                await before.SweepAsync(WatcherFixtureMatrix.MergedSweep());
                idsBefore = [.. before.Store.Cases().Select(row => row.CaseId).Order(StringComparer.Ordinal)];
            }

            using var after = new WatcherHarness(root);
            Assert.Equal(idsBefore, after.Store.Cases().Select(row => row.CaseId).Order(StringComparer.Ordinal));

            // The sweep that follows the restart is the second one, so it is
            // also the one that may propose.
            var result = await after.SweepAsync(
                WatcherFixtureMatrix.MergedSweep(WatcherFixtureMatrix.NowUtc.AddMinutes(5)));

            Assert.Equal(0, result.CasesOpened);
            Assert.Equal(8, after.Store.Cases().Count);
            Assert.Equal(8, result.ProposalsCreated);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_NeverReusesACaseId()
    {
        var root = Path.Combine(Path.GetTempPath(), "watcher-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var before = new WatcherHarness(root))
                await before.SweepAsync(WatcherFixtureMatrix.ById("probe-error-repeat").Sweep());

            using var after = new WatcherHarness(root);
            await after.SweepAsync(WatcherFixtureMatrix.MergedSweep());

            var ids = after.Store.Cases().Select(row => row.CaseId).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // -------------------------------------------- comment instead of a card

    [Fact]
    public async Task AFingerprintThatAlreadyOwnsAnOpenCardGetsACommentNotASecondCard()
    {
        using var harness = new WatcherHarness();
        var fixture = WatcherFixtureMatrix.ById("integration-failure-repeat");

        await harness.SweepAsync(fixture.Sweep());
        await harness.SweepAsync(fixture.Sweep(WatcherFixtureMatrix.NowUtc.AddMinutes(5)));
        Assert.Single(harness.Gateway.Created);

        // The case is answered, the fault recurs, and the card it drafted is
        // still open.
        var proposal = harness.Store.Proposals().Single();
        await harness.Review.DecideAsync(
            proposal.ProposalId,
            new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Approved },
            "operator@test");

        await harness.SweepAsync(fixture.Sweep(WatcherFixtureMatrix.NowUtc.AddMinutes(10)));
        await harness.SweepAsync(fixture.Sweep(WatcherFixtureMatrix.NowUtc.AddMinutes(15)));

        // Exactly one more card was never created, and the recurrence landed as
        // a note on the card this fingerprint already owns.
        Assert.Single(harness.Gateway.Created);
        var comment = Assert.Single(harness.Gateway.Comments);
        Assert.Equal(proposal.CreatedTaskKey, comment.TaskKey);
        Assert.Equal(WatcherProposalKinds.Comment, harness.Store.Proposals()[^1].Kind);
    }

    // --------------------------------------------------------- review mode

    [Fact]
    public async Task Approve_MovesTheDraftedCardToReadyWithTheRecommendedModel()
    {
        using var harness = new WatcherHarness();
        await harness.ReplayUntilProposalsAsync();
        var proposal = harness.Store.Proposals()[0];

        var outcome = await harness.Review.DecideAsync(
            proposal.ProposalId,
            new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Approved },
            "operator@test");

        Assert.True(outcome.Applied);
        var promoted = Assert.Single(harness.Gateway.Promoted);
        Assert.Equal(proposal.CreatedTaskId, promoted.TaskId);
        Assert.Equal("operator@test", promoted.DecidedBy);
        Assert.Equal(WatcherCaseStates.Resolved, harness.Store.FindCase(proposal.CaseId)!.State);
    }

    [Fact]
    public async Task Reject_SuppressesTheFingerprintVisiblyAndWithAnExpiry()
    {
        using var harness = new WatcherHarness();
        await harness.ReplayUntilProposalsAsync();
        var proposal = harness.Store.Proposals()[0];

        var outcome = await harness.Review.DecideAsync(
            proposal.ProposalId,
            new WatcherDecisionRequest
            {
                Decision = WatcherProposalDecisions.Rejected,
                Reason = "Planned maintenance window; the probe is expected to fail.",
            },
            "operator@test",
            WatcherFixtureMatrix.NowUtc);

        Assert.True(outcome.Applied);
        var suppression = Assert.Single(harness.Store.Suppressions());
        Assert.Equal(proposal.Fingerprint, suppression.Fingerprint);
        Assert.Equal("operator@test", suppression.SuppressedBy);
        Assert.True(suppression.IsActive(WatcherFixtureMatrix.NowUtc));
        Assert.False(suppression.IsActive(WatcherFixtureMatrix.NowUtc.AddDays(365)));
        Assert.Empty(harness.Gateway.Promoted);
    }

    [Fact]
    public async Task ASuppressedFingerprintKeepsCountingButProposesNothing()
    {
        using var harness = new WatcherHarness();
        var fixture = WatcherFixtureMatrix.ById("probe-error-repeat");

        await harness.SweepAsync(fixture.Sweep());
        await harness.SweepAsync(fixture.Sweep(WatcherFixtureMatrix.NowUtc.AddMinutes(5)));
        var proposal = harness.Store.Proposals().Single();
        await harness.Review.DecideAsync(
            proposal.ProposalId,
            new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Rejected, Reason = "known noise" },
            "operator@test",
            WatcherFixtureMatrix.NowUtc);

        var later = await harness.SweepAsync(fixture.Sweep(WatcherFixtureMatrix.NowUtc.AddMinutes(10)));

        Assert.Equal(1, later.SuppressedFindings);
        Assert.Single(harness.Store.Proposals());
        var watcherCase = harness.Store.Cases().Single();
        Assert.Equal(WatcherBacklogReasons.Suppressed, watcherCase.BacklogReason);
        Assert.Equal(3, watcherCase.SweepsSeen);
    }

    [Fact]
    public async Task AnExpiredSuppressionLetsTheFingerprintProposeAgain()
    {
        using var harness = new WatcherHarness();
        var fixture = WatcherFixtureMatrix.ById("probe-error-repeat");

        await harness.SweepAsync(fixture.Sweep());
        await harness.SweepAsync(fixture.Sweep(WatcherFixtureMatrix.NowUtc.AddMinutes(5)));
        var proposal = harness.Store.Proposals().Single();
        await harness.Review.DecideAsync(
            proposal.ProposalId,
            new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Rejected, Reason = "known noise" },
            "operator@test",
            WatcherFixtureMatrix.NowUtc);

        var afterExpiry = WatcherFixtureMatrix.NowUtc.AddDays(WatcherDefaults.SuppressionDays + 1);
        await harness.SweepAsync(fixture.Sweep(afterExpiry));

        Assert.Equal(2, harness.Store.Proposals().Count);
    }

    [Fact]
    public async Task Merge_FoldsTheDraftIntoAnExistingCardWithoutMovingAnything()
    {
        using var harness = new WatcherHarness();
        await harness.ReplayUntilProposalsAsync();
        var proposal = harness.Store.Proposals()[0];

        var outcome = await harness.Review.DecideAsync(
            proposal.ProposalId,
            new WatcherDecisionRequest
            {
                Decision = WatcherProposalDecisions.Merged,
                MergeIntoTaskKey = "AGT-2705",
            },
            "operator@test");

        Assert.True(outcome.Applied);
        Assert.Empty(harness.Gateway.Promoted);
        Assert.Contains(harness.Gateway.Comments, row => row.TaskKey == "AGT-2705");
        Assert.Equal(WatcherCaseStates.Resolved, harness.Store.FindCase(proposal.CaseId)!.State);
    }

    [Fact]
    public async Task ARejectionWithoutAReasonIsRefused()
    {
        using var harness = new WatcherHarness();
        await harness.ReplayUntilProposalsAsync();
        var proposal = harness.Store.Proposals()[0];

        var outcome = await harness.Review.DecideAsync(
            proposal.ProposalId,
            new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Rejected },
            "operator@test");

        Assert.False(outcome.Applied);
        Assert.Equal(WatcherProposalDecisions.Pending, harness.Store.FindProposal(proposal.ProposalId)!.Decision);
        Assert.Empty(harness.Store.Suppressions());
    }

    [Fact]
    public async Task AProposalCanOnlyBeAnsweredOnce()
    {
        using var harness = new WatcherHarness();
        await harness.ReplayUntilProposalsAsync();
        var proposal = harness.Store.Proposals()[0];
        var request = new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Approved };

        Assert.True((await harness.Review.DecideAsync(proposal.ProposalId, request, "first@test")).Applied);
        var again = await harness.Review.DecideAsync(proposal.ProposalId, request, "second@test");

        Assert.False(again.Applied);
        Assert.Single(harness.Gateway.Promoted);
    }

    [Fact]
    public async Task ClassEvidence_SeparatesUneditedAcceptancesFromEditedOnes()
    {
        using var harness = new WatcherHarness();
        await harness.ReplayUntilProposalsAsync();
        var repetition = harness.Store.Proposals()
            .Where(row => row.DetectorClass == WatcherDetectorClasses.Repetition)
            .ToList();

        await harness.Review.DecideAsync(
            repetition[0].ProposalId,
            new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Approved },
            "operator@test");
        await harness.Review.DecideAsync(
            repetition[1].ProposalId,
            new WatcherDecisionRequest { Decision = WatcherProposalDecisions.Edited, Edited = true },
            "operator@test");

        var evidence = harness.Review.ClassEvidence()
            .Single(row => row.DetectorClass == WatcherDetectorClasses.Repetition);

        Assert.Equal(1, evidence.Accepted);
        Assert.Equal(1, evidence.AcceptedWithEdit);
        Assert.True(evidence.Promotable);
    }

    [Fact]
    public void ContradictionAndDriftAreNeverPromotableToAutoApproval()
    {
        Assert.False(WatcherDetectorClasses.IsPromotable(WatcherDetectorClasses.Contradiction));
        Assert.False(WatcherDetectorClasses.IsPromotable(WatcherDetectorClasses.Drift));
        Assert.False(WatcherDetectorClasses.IsPromotable(WatcherDetectorClasses.Silence));
        Assert.True(WatcherDetectorClasses.IsPromotable(WatcherDetectorClasses.Repetition));
        Assert.True(WatcherDetectorClasses.IsPromotable(WatcherDetectorClasses.Hygiene));
    }

    [Fact]
    public async Task DisabledWatcher_DoesNothingAtAll()
    {
        using var harness = new WatcherHarness();

        var result = await harness.SweepAsync(
            WatcherFixtureMatrix.MergedSweep(),
            harness.Options with { Enabled = false });

        Assert.False(result.Enabled);
        Assert.Empty(harness.Store.Cases());
        Assert.Empty(harness.Gateway.Created);
    }
}
