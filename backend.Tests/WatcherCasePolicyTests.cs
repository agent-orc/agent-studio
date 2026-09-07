using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix tests for folding findings into durable cases: deduplication,
/// the two-sweep persistence check, suppression, and the explicit terminals.
/// </summary>
public class WatcherCasePolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 20, 0, 0, DateTimeKind.Utc);

    private static WatcherFinding Finding(
        string fingerprint = "repetition|a|b",
        string detectorClass = WatcherDetectorClasses.Repetition,
        int occurrences = 3,
        string[]? cards = null,
        bool uncertain = true) => new()
    {
        Fingerprint = fingerprint,
        DetectorClass = detectorClass,
        DetectorRule = "rule",
        Title = "Something keeps failing",
        FirstSeenAtUtc = Now.AddHours(-2),
        LastSeenAtUtc = Now,
        Occurrences = occurrences,
        AffectedCards = cards ?? [],
        Evidence = [new WatcherEvidenceItem("Fact", "value", "source")],
        UncertainCause = uncertain,
    };

    [Fact]
    public void ANewFinding_OpensOneCaseWithASweepCountOfOne()
    {
        var fold = WatcherCasePolicy.Fold([], [Finding()], [], Now);

        var item = Assert.Single(fold.Cases);
        Assert.Equal(WatcherCaseStates.Open, item.State);
        Assert.Equal(1, item.SweepCount);
        Assert.Single(fold.Opened);
        Assert.Empty(fold.Touched);
        Assert.Equal(WatcherIdentity.CaseId(item.Fingerprint), item.Id);
    }

    [Fact]
    public void TwoFindingsWithTheSameFingerprintInOneSweep_CollapseOntoOneCase()
    {
        var fold = WatcherCasePolicy.Fold([], [Finding(), Finding()], [], Now);

        var item = Assert.Single(fold.Cases);
        Assert.Equal(1, item.SweepCount);
    }

    [Fact]
    public void ARepeatSweep_AdvancesTheCountWithoutOpeningASecondCase()
    {
        var first = WatcherCasePolicy.Fold([], [Finding()], [], Now);

        var second = WatcherCasePolicy.Fold(first.Cases, [Finding()], [], Now.AddMinutes(5));

        var item = Assert.Single(second.Cases);
        Assert.Equal(2, item.SweepCount);
        Assert.Empty(second.Opened);
        Assert.Single(second.Touched);
    }

    [Theory]
    // One sweep is not enough: a transient blip must never become a ticket.
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void OnlyACaseThatSurvivedTwoSweeps_IsEligibleForAProposal(int sweeps, bool eligible)
    {
        var fold = WatcherCasePolicy.Fold([], [Finding()], [], Now);
        for (var sweep = 1; sweep < sweeps; sweep++)
            fold = WatcherCasePolicy.Fold(fold.Cases, [Finding()], [], Now.AddMinutes(5 * sweep));

        Assert.Equal(eligible, WatcherCasePolicy.EligibleForProposal(fold.Cases).Count == 1);
    }

    [Fact]
    public void ACaseThatAlreadyProduced_AProposalIsNotEligibleAgain()
    {
        var fold = WatcherCasePolicy.Fold([], [Finding()], [], Now);
        fold = WatcherCasePolicy.Fold(fold.Cases, [Finding()], [], Now.AddMinutes(5));
        var proposed = fold.Cases[0] with { ProposalId = "WPR-1" };

        Assert.Empty(WatcherCasePolicy.EligibleForProposal([proposed]));
    }

    [Fact]
    public void MergedAffectedCards_AccumulateAcrossSweeps()
    {
        var first = WatcherCasePolicy.Fold([], [Finding(cards: ["A-1"])], [], Now);

        var second = WatcherCasePolicy.Fold(
            first.Cases, [Finding(cards: ["A-2"])], [], Now.AddMinutes(5));

        Assert.Equal(["A-1", "A-2"], Assert.Single(second.Cases).AffectedCards);
    }

    [Fact]
    public void ACaseTheSweepNoLongerSees_ReachesAnExplicitTerminal()
    {
        var first = WatcherCasePolicy.Fold([], [Finding()], [], Now);

        var second = WatcherCasePolicy.Fold(
            first.Cases, [Finding("repetition|other")], [], Now.AddMinutes(5));

        var resolved = Assert.Single(
            second.Cases, item => item.Fingerprint == "repetition|a|b");
        Assert.Equal(WatcherCaseStates.Resolved, resolved.State);
        Assert.Equal(WatcherCasePolicy.TerminalReasons.SignalStopped, resolved.TerminalReason);
        Assert.Contains(second.Closed, item => item.Id == resolved.Id);
    }

    [Fact]
    public void ABlindSweep_ClosesNothing()
    {
        var first = WatcherCasePolicy.Fold([], [Finding()], [], Now);

        var second = WatcherCasePolicy.Fold(
            first.Cases, [], [], Now.AddMinutes(5), sweepHadSignals: false);

        Assert.Equal(WatcherCaseStates.Open, Assert.Single(second.Cases).State);
        Assert.Empty(second.Closed);
    }

    [Fact]
    public void AResolvedCaseWhoseSignalReturns_ReopensWithAFreshPersistenceCount()
    {
        var first = WatcherCasePolicy.Fold([], [Finding()], [], Now);
        var second = WatcherCasePolicy.Fold(first.Cases, [Finding()], [], Now.AddMinutes(5));
        var closed = WatcherCasePolicy.Fold(second.Cases, [], [], Now.AddMinutes(10));
        Assert.Equal(WatcherCaseStates.Resolved, Assert.Single(closed.Cases).State);

        var reopened = WatcherCasePolicy.Fold(closed.Cases, [Finding()], [], Now.AddMinutes(15));

        var item = Assert.Single(reopened.Cases);
        Assert.Equal(WatcherCaseStates.Open, item.State);
        // A flapping fingerprint must not skip the two-sweep check by having
        // been resolved once.
        Assert.Equal(1, item.SweepCount);
        Assert.Null(item.ProposalId);
        Assert.Single(reopened.Opened);
    }

    [Fact]
    public void AnActiveSuppression_DrivesTheCaseStraightToItsTerminal()
    {
        var finding = Finding();
        var suppression = new WatcherSuppression
        {
            Fingerprint = finding.Fingerprint,
            DetectorClass = finding.DetectorClass,
            Reason = "known noise",
            CreatedAtUtc = Now.AddDays(-1),
            ExpiresAtUtc = Now.AddDays(13),
        };

        var fold = WatcherCasePolicy.Fold([], [finding], [suppression], Now);

        var item = Assert.Single(fold.Cases);
        Assert.Equal(WatcherCaseStates.Suppressed, item.State);
        Assert.Equal(WatcherCasePolicy.TerminalReasons.Suppressed, item.TerminalReason);
        Assert.Empty(WatcherCasePolicy.EligibleForProposal(fold.Cases));
    }

    [Fact]
    public void AnExpiredSuppression_DoesNotSilenceTheFinding()
    {
        var finding = Finding();
        var expired = new WatcherSuppression
        {
            Fingerprint = finding.Fingerprint,
            DetectorClass = finding.DetectorClass,
            Reason = "was noise",
            CreatedAtUtc = Now.AddDays(-30),
            ExpiresAtUtc = Now.AddDays(-1),
        };

        var fold = WatcherCasePolicy.Fold([], [finding], [expired], Now);

        Assert.Equal(WatcherCaseStates.Open, Assert.Single(fold.Cases).State);
    }

    [Fact]
    public void ACaseId_IsDerivedFromItsFingerprintSoARestartRebuildsIt()
    {
        var left = WatcherIdentity.CaseId("repetition|source|thing");
        var right = WatcherIdentity.CaseId("repetition|source|thing");

        Assert.Equal(left, right);
        Assert.StartsWith("WCH-", left, StringComparison.Ordinal);
        Assert.NotEqual(left, WatcherIdentity.CaseId("repetition|source|other"));
    }

    [Fact]
    public void TheEvidenceDigest_IsStableForTheSameFactsAndMovesWhenTheyChange()
    {
        WatcherEvidenceItem[] pack = [new("A", "1", "s"), new("B", "2", "s")];

        Assert.Equal(WatcherIdentity.EvidenceDigest(pack), WatcherIdentity.EvidenceDigest(pack));
        Assert.NotEqual(
            WatcherIdentity.EvidenceDigest(pack),
            WatcherIdentity.EvidenceDigest([new("A", "1", "s"), new("B", "3", "s")]));
    }

    [Fact]
    public void TheEvidenceDigest_DistinguishesAMissingFactFromAPresentOne()
    {
        Assert.NotEqual(
            WatcherIdentity.EvidenceDigest([new WatcherEvidenceItem("A", "(not available)", "s")]),
            WatcherIdentity.EvidenceDigest([WatcherEvidenceItem.Missing("A", "s")]));
    }

    [Fact]
    public void TheEvidencePack_IsBoundedAndNamesWhatItDropped()
    {
        var evidence = Enumerable.Range(0, 200)
            .Select(index => new WatcherEvidenceItem($"Fact {index}", new string('x', 200), "source"))
            .ToList();
        var item = WatcherCasePolicy.Fold([], [Finding() with { Evidence = evidence }], [], Now).Cases[0];

        var pack = WatcherEvidencePackBuilder.Build(item, characterBudget: 2_000);

        Assert.True(pack.Truncated);
        Assert.True(pack.DroppedItems > 0);
        Assert.Contains("Pack bounded", pack.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEvidencePack_RendersAMissingFactAsMissingAndNotAsEmpty()
    {
        var item = WatcherCasePolicy.Fold(
            [],
            [Finding() with { Evidence = [WatcherEvidenceItem.Missing("Last seen", "runner")] }],
            [],
            Now).Cases[0];

        Assert.Contains("_not available_", WatcherEvidencePackBuilder.Build(item).ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDraft_CarriesTheHouseSectionsAndTheProposalTags()
    {
        var item = WatcherCasePolicy.Fold([], [Finding()], [], Now).Cases[0];
        var pack = WatcherEvidencePackBuilder.Build(item);

        var draft = WatcherProposalDraftBuilder.Build(item, pack, analysis: null);

        Assert.Contains("## Context", draft.PromptMarkdown, StringComparison.Ordinal);
        Assert.Contains("## Changes", draft.PromptMarkdown, StringComparison.Ordinal);
        Assert.Contains("## Acceptance", draft.PromptMarkdown, StringComparison.Ordinal);
        Assert.Contains(WatcherTags.Proposal, draft.Tags);
        Assert.Contains(WatcherTags.DetectorClass(item.DetectorClass), draft.Tags);
        Assert.Contains(WatcherTags.Fingerprint(item.FingerprintDigest), draft.Tags);
    }

    [Fact]
    public void TheDraft_QuotesAModelSuggestionAsASuggestionAndNotAsAFinding()
    {
        var item = WatcherCasePolicy.Fold([], [Finding()], [], Now).Cases[0];
        var analysis = new WatcherAnalysis
        {
            RootCause = "the cache entry is corrupt",
            Confidence = "medium",
            SuggestedChanges = ["rebuild the cache"],
            Unknowns = ["whether the cache is shared"],
        };

        var draft = WatcherProposalDraftBuilder.Build(item, WatcherEvidencePackBuilder.Build(item), analysis);

        Assert.Contains("Suggested by the bounded analysis, to confirm before acting", draft.PromptMarkdown, StringComparison.Ordinal);
        Assert.Contains("carries no authority", draft.PromptMarkdown, StringComparison.Ordinal);
        Assert.Contains("Unknowns the analysis could not close", draft.PromptMarkdown, StringComparison.Ordinal);
    }

    [Theory]
    // A validator's defect list is routine work.
    [InlineData(WatcherDetectorClasses.Hygiene, "chore")]
    // Everything else is a promise the platform is not keeping.
    [InlineData(WatcherDetectorClasses.Repetition, "bug")]
    [InlineData(WatcherDetectorClasses.Contradiction, "bug")]
    [InlineData(WatcherDetectorClasses.Silence, "bug")]
    [InlineData(WatcherDetectorClasses.Drift, "bug")]
    public void TheTaskType_FollowsTheDetectorClass(string detectorClass, string expected)
    {
        Assert.Equal(expected, WatcherModelRouting.TaskTypeFor(detectorClass));
    }

    [Fact]
    public void TheRecommendation_ComesFromTheRoutingPolicyWithoutALiveCliCatalogue()
    {
        var registry = new ModelRoutingPolicyRegistry();

        var recommendation = WatcherModelRouting.Recommend(
            registry, economyMode: false, WatcherDetectorClasses.Hygiene, "title", "prompt");

        Assert.Equal(registry.Policy.Version, recommendation.PolicyVersion);
        Assert.Contains(
            registry.Policy.Tiers,
            tier => string.Equals(tier.Id, recommendation.Tier, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(recommendation.Model));
    }

    [Fact]
    public void TheRecommendation_RespectsTheCorrectnessFloorOfTheRoutingPolicy()
    {
        var registry = new ModelRoutingPolicyRegistry();

        var recommendation = WatcherModelRouting.Recommend(
            registry,
            economyMode: true,
            WatcherDetectorClasses.Contradiction,
            "Fencing authority is ambiguous",
            "A lease ownership conflict can cause data-loss.");

        // Quota or economy mode may not lower a correctness floor.
        Assert.Equal("sol-xhigh", recommendation.Tier);
        Assert.Equal("sol-xhigh", recommendation.CorrectnessFloorTier);
    }
}
