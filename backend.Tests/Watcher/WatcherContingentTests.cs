using Xunit;

namespace AgentStudio.Tests.Watcher;

/// <summary>
/// Contingent accounting: per-day and per-week budgets in counts and in tokens.
/// The rule the dossier cares about is what happens at exhaustion: detection
/// and counting continue, spending stops, and the backlog stays visible.
/// </summary>
public sealed class WatcherContingentTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 19, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void AFreshLedgerAdmitsEverySpendKind()
    {
        var contingent = WatcherContingent.Defaults();

        Assert.True(Admit([], contingent, WatcherSpendKind.ModelCall, tokens: 10_000).Allowed);
        Assert.True(Admit([], contingent, WatcherSpendKind.Proposal).Allowed);
        Assert.True(Admit([], contingent, WatcherSpendKind.Comment).Allowed);
    }

    [Fact]
    public void TheDailyProposalCountBinds()
    {
        var contingent = WatcherContingent.Defaults() with { ProposalsPerDay = 2 };
        var history = Spend(WatcherSpendKind.Proposal, 2, Now.AddHours(-1));

        var verdict = Admit(history, contingent, WatcherSpendKind.Proposal);

        Assert.False(verdict.Allowed);
        Assert.Equal(WatcherContingentBlock.ProposalsPerDay, verdict.Block);
    }

    [Fact]
    public void TheWeeklyCountBindsEvenWhenTodayIsQuiet()
    {
        var contingent = WatcherContingent.Defaults() with { ProposalsPerDay = 8, ProposalsPerWeek = 3 };
        var history = Spend(WatcherSpendKind.Proposal, 3, Now.AddDays(-3));

        var verdict = Admit(history, contingent, WatcherSpendKind.Proposal);

        Assert.False(verdict.Allowed);
        Assert.Equal(WatcherContingentBlock.ProposalsPerWeek, verdict.Block);
    }

    [Fact]
    public void SpendOlderThanTheWindowNoLongerCounts()
    {
        var contingent = WatcherContingent.Defaults() with { ProposalsPerDay = 1, ProposalsPerWeek = 1 };
        var history = Spend(WatcherSpendKind.Proposal, 1, Now.AddDays(-8));

        Assert.True(Admit(history, contingent, WatcherSpendKind.Proposal).Allowed);
    }

    [Fact]
    public void TheTokenBudgetBindsModelCallsIndependentlyOfTheCallCount()
    {
        var contingent = WatcherContingent.Defaults() with { ModelCallsPerDay = 100, TokensPerDay = 50_000 };
        IReadOnlyList<WatcherSpendEntry> history = [new(Now.AddHours(-2), WatcherSpendKind.ModelCall, 45_000)];

        var verdict = Admit(history, contingent, WatcherSpendKind.ModelCall, tokens: 10_000);

        Assert.False(verdict.Allowed);
        Assert.Equal(WatcherContingentBlock.TokensPerDay, verdict.Block);
    }

    [Fact]
    public void ATokenExhaustedDayStillAdmitsADeterministicProposal()
    {
        // Drafting costs no tokens, so an exhausted token window must not block
        // a proposal that no model touched.
        var contingent = WatcherContingent.Defaults() with { TokensPerDay = 1 };
        IReadOnlyList<WatcherSpendEntry> history = [new(Now.AddHours(-1), WatcherSpendKind.ModelCall, 400_000)];

        Assert.True(Admit(history, contingent, WatcherSpendKind.Proposal).Allowed);
    }

    [Fact]
    public void AZeroContingentRefusesEverySpendKind()
    {
        var contingent = WatcherContingent.Zero();

        Assert.False(Admit([], contingent, WatcherSpendKind.ModelCall, tokens: 1).Allowed);
        Assert.False(Admit([], contingent, WatcherSpendKind.Proposal).Allowed);
        Assert.False(Admit([], contingent, WatcherSpendKind.Comment).Allowed);
    }

    [Fact]
    public void UsageReportsEachKindSeparatelyInsideTheWindow()
    {
        IReadOnlyList<WatcherSpendEntry> history =
        [
            new(Now.AddHours(-2), WatcherSpendKind.ModelCall, 1_000),
            new(Now.AddHours(-3), WatcherSpendKind.Proposal, 0),
            new(Now.AddDays(-3), WatcherSpendKind.Comment, 0),
        ];

        var day = WatcherContingentLedger.Usage(history, Now, WatcherContingentLedger.Day);
        var week = WatcherContingentLedger.Usage(history, Now, WatcherContingentLedger.Week);

        Assert.Equal(1, day.ModelCalls);
        Assert.Equal(1, day.Proposals);
        Assert.Equal(0, day.Comments);
        Assert.Equal(1_000, day.Tokens);
        Assert.Equal(1, week.Comments);
    }

    [Fact]
    public void PruneKeepsTheHistoryBoundedToTheWidestWindow()
    {
        IReadOnlyList<WatcherSpendEntry> history =
        [
            new(Now.AddDays(-10), WatcherSpendKind.Proposal, 0),
            new(Now.AddDays(-1), WatcherSpendKind.Proposal, 0),
        ];

        Assert.Single(WatcherContingentLedger.Prune(history, Now));
    }

    private static WatcherContingentVerdict Admit(
        IReadOnlyList<WatcherSpendEntry> history,
        WatcherContingent contingent,
        WatcherSpendKind kind,
        long tokens = 0)
        => WatcherContingentLedger.Admit(history, contingent, kind, Now, tokens);

    private static IReadOnlyList<WatcherSpendEntry> Spend(WatcherSpendKind kind, int count, DateTime at)
        => [.. Enumerable.Range(0, count).Select(i => new WatcherSpendEntry(at.AddMinutes(i), kind, 0))];
}
