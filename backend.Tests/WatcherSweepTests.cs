using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The acceptance rail of AGT-2721: replaying the 2026-09-06 fixtures must
/// produce eight cases and eight proposals in the proposal state, none of them
/// Ready, and a zero contingent must produce the cases without the proposals.
/// </summary>
public sealed class WatcherSweepTests
{
    [Fact]
    public async Task Replay_ProducesEightCasesAndEightProposals()
    {
        using var harness = new WatcherHarness();

        var result = await harness.ReplayUntilProposalsAsync();

        Assert.Equal(8, harness.Store.Cases().Count);
        Assert.Equal(8, harness.Store.Proposals().Count);
        Assert.Equal(8, result.ProposalsCreated);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Replay_LeavesEveryProposalPendingAndOutsideReady()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        Assert.All(harness.Store.Proposals(), proposal =>
            Assert.Equal(WatcherProposalDecisions.Pending, proposal.Decision));
        // Approval is the only path that moves a card, and nobody approved.
        Assert.Empty(harness.Gateway.Promoted);
        Assert.All(harness.Gateway.Created, created =>
            Assert.Contains(WatcherTags.Proposal, created.Draft.Tags));
    }

    [Fact]
    public async Task Replay_GivesEveryProposalEvidenceAndAModelRecommendation()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        Assert.All(harness.Store.Proposals(), proposal =>
        {
            Assert.NotEmpty(proposal.EvidencePackDigest);
            Assert.NotEmpty(proposal.Recommendation.Model);
            Assert.NotEmpty(proposal.Recommendation.PolicyVersion);
            Assert.NotEmpty(proposal.Recommendation.Tier);
            Assert.Contains("## Context", proposal.Draft.Prompt, StringComparison.Ordinal);
            Assert.Contains("## Changes", proposal.Draft.Prompt, StringComparison.Ordinal);
            Assert.Contains("## Acceptance", proposal.Draft.Prompt, StringComparison.Ordinal);
            Assert.Contains(proposal.EvidencePackDigest, proposal.Draft.Prompt, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Replay_CarriesTheDetectorClassAsATagOnEveryDraft()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        foreach (var proposal in harness.Store.Proposals())
        {
            Assert.Contains(WatcherTags.ForClass(proposal.DetectorClass), proposal.Draft.Tags);
            Assert.True(WatcherDetectorClasses.IsKnown(proposal.DetectorClass));
        }
    }

    [Fact]
    public async Task FirstSweep_OpensCasesButWritesNoProposal()
    {
        using var harness = new WatcherHarness();

        var first = await harness.SweepAsync(WatcherFixtureMatrix.MergedSweep());

        Assert.Equal(8, first.CasesOpened);
        Assert.Equal(0, first.ProposalsCreated);
        Assert.Empty(harness.Gateway.Created);
        Assert.All(harness.Store.Cases(), row =>
        {
            Assert.Equal(WatcherCaseStates.Open, row.State);
            Assert.Equal(WatcherBacklogReasons.AwaitingPersistence, row.BacklogReason);
        });
    }

    [Fact]
    public async Task Replay_MovesEveryCaseToDecisionRequired()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        Assert.All(harness.Store.Cases(), row =>
        {
            Assert.Equal(WatcherCaseStates.DecisionRequired, row.State);
            Assert.NotNull(row.ProposalId);
            Assert.Null(row.BacklogReason);
        });
    }

    [Fact]
    public async Task Replay_PublishesOneProblemAndOneDecisionPerCase()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        Assert.Equal(8, harness.Activity.Raised.Count);
        Assert.Equal(8, harness.Activity.Proposed.Count);
        // A second sweep of an unchanged fingerprint is a rollup on the case,
        // not a second Problem row in the feed.
        Assert.Equal(
            8,
            harness.Activity.Raised.Select(row => row.CaseId).Distinct(StringComparer.Ordinal).Count());
    }

    // ------------------------------------------------------------- contingent

    [Fact]
    public async Task ZeroContingent_ProducesCasesButNoProposalsAndNoModelCalls()
    {
        using var harness = new WatcherHarness(contingent: WatcherContingentOptions.Exhausted);

        var result = await harness.ReplayUntilProposalsAsync();

        Assert.Equal(8, harness.Store.Cases().Count);
        Assert.Empty(harness.Store.Proposals());
        Assert.Equal(0, result.ProposalsCreated);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, harness.Analyst.Calls);
        Assert.Empty(harness.Gateway.Created);
        Assert.Empty(harness.Gateway.Comments);
    }

    [Fact]
    public async Task ZeroContingent_KeepsTheUnanalysedBacklogVisible()
    {
        using var harness = new WatcherHarness(contingent: WatcherContingentOptions.Exhausted);

        var result = await harness.ReplayUntilProposalsAsync();

        Assert.Equal(8, result.Backlog);
        Assert.All(harness.Store.Cases(), row =>
        {
            Assert.Equal(WatcherCaseStates.Open, row.State);
            Assert.Equal(WatcherBacklogReasons.ContingentExhausted, row.BacklogReason);
        });
        Assert.Equal(8, harness.Activity.Exhausted.Count);
    }

    [Fact]
    public async Task ProposalBudget_StopsAtTheConfiguredCountAndLeavesTheRestAsBacklog()
    {
        using var harness = new WatcherHarness(contingent: WatcherContingentOptions.Default with
        {
            DailyProposals = 3,
            WeeklyProposals = 3,
        });

        var result = await harness.ReplayUntilProposalsAsync();

        Assert.Equal(3, result.ProposalsCreated);
        Assert.Equal(3, harness.Store.Proposals().Count);
        Assert.Equal(5, result.Backlog);
    }

    [Fact]
    public async Task ContingentSnapshot_ReportsUsageAgainstTheBudget()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        var snapshot = WatcherContingentPolicy.Describe(
            harness.Options.Contingent,
            harness.Store.Snapshot().Spend,
            WatcherFixtureMatrix.NowUtc.AddMinutes(5),
            backlogCases: 0);

        Assert.Equal(8, snapshot.Daily.Proposals);
        Assert.Equal(8, snapshot.Weekly.Proposals);
        Assert.False(snapshot.Exhausted);
        Assert.Equal(harness.Options.Contingent.DailyProposals - 8, snapshot.DailyProposalsRemaining);
    }

    // ------------------------------------------------------ model economy (§5)

    [Fact]
    public async Task Analysis_RunsOnlyForTheTwoAmbiguousClasses()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        // Three cases are ambiguous by class: two contradictions and one drift.
        // Repetition and hygiene are decided by counting and must cost nothing.
        Assert.Equal(3, harness.Analyst.Calls);
        Assert.All(harness.Analyst.Plans, plan =>
            Assert.Equal(WatcherAnalysisRoute.StrongAnalysis, plan.Route));

        var analysed = harness.Store.Proposals()
            .Where(row => row.Analysis.Calls.Count > 0)
            .Select(row => row.DetectorClass)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal([WatcherDetectorClasses.Contradiction, WatcherDetectorClasses.Drift], analysed);
    }

    [Fact]
    public async Task Analysis_RecordsTokensAndCostOnTheProposal()
    {
        using var harness = new WatcherHarness();

        await harness.ReplayUntilProposalsAsync();

        var analysed = harness.Store.Proposals().First(row => row.Analysis.Calls.Count > 0);
        Assert.Equal(40_000, analysed.Analysis.TotalInputTokens);
        Assert.Equal(4_000, analysed.Analysis.TotalOutputTokens);
        Assert.Equal(0.25, analysed.Analysis.TotalDollars);
    }

    [Fact]
    public void AnalysisReceipt_ReportsUnknownCostAsUnknownRatherThanZero()
    {
        var receipt = new WatcherAnalysisReceipt
        {
            Route = "strong",
            Calls =
            [
                new WatcherModelCall { Purpose = "x", Model = "known", InputTokens = 1, OutputTokens = 1, Dollars = 0.5 },
                new WatcherModelCall { Purpose = "y", Model = "unpriced", InputTokens = 1, OutputTokens = 1, Dollars = null },
            ],
        };

        Assert.Null(receipt.TotalDollars);
    }
}
