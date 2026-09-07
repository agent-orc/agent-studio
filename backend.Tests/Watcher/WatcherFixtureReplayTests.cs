using Xunit;

namespace AgentStudio.Tests.Watcher;

/// <summary>
/// Acceptance for the 2026-09-06 fixture matrix: replaying that evening must
/// produce one case and one ticket proposal per hand-written finding, each with
/// evidence and a routing recommendation, and none of them in Ready.
/// </summary>
public sealed class WatcherFixtureReplayTests
{
    private static readonly CliModelCatalog Catalogue = new()
    {
        Source = "watcher-fixture-catalogue",
        FetchedAt = WatcherFixtureMatrix.Evening,
        Models =
        [
            Model("gpt-5.6-sol", "medium", "high", "xhigh"),
            Model("gpt-5.6-terra", "medium"),
            Model("gpt-5.6-luna", "medium"),
        ],
    };

    [Fact]
    public void ReplayingTheEveningProducesOneFindingPerHandWrittenFinding()
    {
        var findings = WatcherDetectors.RunAll(
            WatcherFixtureMatrix.Observation(),
            WatcherDetectorThresholds.Defaults());

        Assert.Equal(WatcherFixtureMatrix.ExpectedFindings, findings.Count);
    }

    [Fact]
    public void EveryDetectorClassIsExercisedByTheMatrix()
    {
        var findings = WatcherDetectors.RunAll(
            WatcherFixtureMatrix.Observation(),
            WatcherDetectorThresholds.Defaults());

        var byClass = findings.GroupBy(f => f.DetectorClass).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(3, byClass[WatcherDetectorClass.Repetition]);
        Assert.Equal(2, byClass[WatcherDetectorClass.Contradiction]);
        Assert.Equal(1, byClass[WatcherDetectorClass.Silence]);
        Assert.Equal(1, byClass[WatcherDetectorClass.Drift]);
        Assert.Equal(1, byClass[WatcherDetectorClass.Hygiene]);
    }

    [Fact]
    public void TwoSweepsProduceEightCasesAndEightProposalsNoneInReady()
    {
        var cases = ReplayTwoSweeps();

        Assert.Equal(WatcherFixtureMatrix.ExpectedFindings, cases.Count);
        Assert.All(cases, c => Assert.True(c.IsPersistent, $"{c.CaseId} did not survive two sweeps"));

        var proposals = cases
            .Select(c => Propose(c))
            .ToList();

        Assert.Equal(WatcherFixtureMatrix.ExpectedFindings, proposals.Count);
        Assert.All(proposals, proposal =>
        {
            Assert.Equal(WatcherProposalConventions.ProposalLane, proposal.CardDraft.TargetState);
            Assert.NotEqual(WatcherProposalConventions.ApprovedLane, proposal.CardDraft.TargetState);
            Assert.Equal(WatcherProposalDecision.Pending, proposal.Decision);
            Assert.Null(proposal.SpawnedTaskKey);
            Assert.Contains(WatcherProposalConventions.ProposalTag, proposal.CardDraft.Tags);
        });
    }

    [Fact]
    public void EveryProposalCarriesEvidenceAndARoutingRecommendation()
    {
        foreach (var watcherCase in ReplayTwoSweeps())
        {
            var proposal = Propose(watcherCase);

            Assert.NotEmpty(watcherCase.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(watcherCase.EvidencePackDigest));
            Assert.Equal(watcherCase.EvidencePackDigest, proposal.EvidencePackDigest);
            Assert.False(string.IsNullOrWhiteSpace(proposal.Recommendation.Model));
            Assert.False(string.IsNullOrWhiteSpace(proposal.Recommendation.ThinkingLevel));
            Assert.Equal("2026-07-24", proposal.Recommendation.PolicyVersion);

            // The house prompt style: context with timestamps, changes, acceptance.
            Assert.Contains("## Context", proposal.CardDraft.PromptMarkdown, StringComparison.Ordinal);
            Assert.Contains("## Changes", proposal.CardDraft.PromptMarkdown, StringComparison.Ordinal);
            Assert.Contains("## Acceptance", proposal.CardDraft.PromptMarkdown, StringComparison.Ordinal);
            Assert.Contains(watcherCase.Fingerprint, proposal.CardDraft.PromptMarkdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProposalsReferenceTheOverlappingCardsOfTheirCase()
    {
        var cases = ReplayTwoSweeps();

        var dirtyCheckout = Assert.Single(
            cases,
            c => c.DetectorClass == WatcherDetectorClass.Repetition && c.Project == "QS");
        var proposal = Propose(dirtyCheckout);

        Assert.Equal(9, proposal.CardDraft.RelatedCards.Count);
        Assert.Contains("QS-101", proposal.CardDraft.RelatedCards);
    }

    [Fact]
    public void ContingentSetToZeroKeepsCasesButAdmitsNoProposalAndNoModelCall()
    {
        var cases = ReplayTwoSweeps();
        var contingent = WatcherContingent.Zero();
        IReadOnlyList<WatcherSpendEntry> spend = [];

        var admittedProposals = cases
            .Count(_ => WatcherContingentLedger
                .Admit(spend, contingent, WatcherSpendKind.Proposal, WatcherFixtureMatrix.Evening)
                .Allowed);

        var admittedModelCalls = WatcherContingentLedger
            .Admit(spend, contingent, WatcherSpendKind.ModelCall, WatcherFixtureMatrix.Evening, tokens: 10_000)
            .Allowed;

        Assert.Equal(WatcherFixtureMatrix.ExpectedFindings, cases.Count);
        Assert.Equal(0, admittedProposals);
        Assert.False(admittedModelCalls);

        // The unanalysed backlog stays visible: the cases are still there and
        // still awaiting a proposal.
        Assert.Equal(
            WatcherFixtureMatrix.ExpectedFindings,
            WatcherCaseLedger.AwaitingProposal(cases).Count);
    }

    [Fact]
    public void CasesProjectOntoTheExistingActivityEntryGrid()
    {
        foreach (var watcherCase in ReplayTwoSweeps())
        {
            var problem = WatcherActivityProjection.FindingRaised(watcherCase);
            Assert.Equal(OrchestratorLogKinds.Alert, problem.Kind);
            Assert.Equal(WatcherBusTopics.FindingRaised, problem.Topic);

            var decision = WatcherActivityProjection.DecisionRequired(watcherCase, Propose(watcherCase));
            Assert.Equal(OrchestratorLogKinds.Decision, decision.Kind);
            Assert.Equal(WatcherBusTopics.DecisionRequired, decision.Topic);
        }
    }

    /// <summary>
    /// Replays the evening twice. The dossier requires a case to survive two
    /// sweeps before it may become a proposal, so this is the smallest replay
    /// that reaches the W2 admission gate.
    /// </summary>
    private static IReadOnlyList<WatcherCase> ReplayTwoSweeps()
    {
        var thresholds = WatcherDetectorThresholds.Defaults();
        var first = WatcherFixtureMatrix.Observation(WatcherFixtureMatrix.Evening);
        var second = WatcherFixtureMatrix.Observation(WatcherFixtureMatrix.Evening.AddMinutes(5));

        var afterFirst = WatcherCaseLedger.Fold(
            [], WatcherDetectors.RunAll(first, thresholds), sweepId: 1, first.CapturedAt);
        var afterSecond = WatcherCaseLedger.Fold(
            afterFirst.Cases, WatcherDetectors.RunAll(second, thresholds), sweepId: 2, second.CapturedAt);

        return afterSecond.Cases;
    }

    private static WatcherProposal Propose(WatcherCase watcherCase)
    {
        var recommendation = WatcherModelRouting.Recommend(
            new ModelRoutingPolicyRegistry(), Catalogue, economyMode: false, watcherCase);

        return WatcherProposalDrafting.Draft(watcherCase, recommendation, WatcherFixtureMatrix.Evening);
    }

    private static CliModelInfo Model(string id, params string[] thinkingLevels) => new()
    {
        Id = id,
        Label = id,
        Vendor = "test",
        Available = true,
        ThinkingLevels = [.. thinkingLevels],
        DefaultThinkingLevel = thinkingLevels.FirstOrDefault(),
    };
}
