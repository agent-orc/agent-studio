using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class SalvageRetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SalvageRetentionSettings Settings = new(RetentionDays: 14, MaxPerCard: 3);

    [Fact]
    public void Entry_without_a_card_is_never_deleted()
    {
        var entry = Tarball("notes.tgz", card: null, ageDays: 400);

        var decision = Single(entry, Terminal("AGT-1", daysAgo: 400));

        Assert.Equal(SalvageRetentionReason.KeepUnrecognized, decision.Reason);
        Assert.False(decision.Delete);
    }

    [Fact]
    public void Active_run_protects_every_entry_of_its_card_even_past_retention_and_cap()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(index => Tarball($"AGT-1-{index:0000}.tgz", "AGT-1", ageDays: 60 + index))
            .ToArray();

        var decisions = SalvageRetentionPolicy.Decide(
            entries,
            Cards(Terminal("AGT-1", daysAgo: 45)),
            new HashSet<string>(["agt-1"]),
            Settings,
            Now);

        Assert.All(decisions, decision => Assert.Equal(SalvageRetentionReason.KeepActiveRun, decision.Reason));
    }

    [Theory]
    [InlineData(13.9, SalvageRetentionReason.KeepWithinRetention)]
    [InlineData(14.0, SalvageRetentionReason.DeleteRetentionElapsed)]
    [InlineData(30.0, SalvageRetentionReason.DeleteRetentionElapsed)]
    public void Terminal_card_tarball_is_kept_for_the_retention_window(double daysSinceCompletion, SalvageRetentionReason expected)
    {
        var decision = Single(
            Tarball("AGT-2-1200.tgz", "AGT-2", ageDays: 90),
            Terminal("AGT-2", daysSinceCompletion));

        Assert.Equal(expected, decision.Reason);
    }

    [Theory]
    [InlineData(SalvageCardLifecycle.Open, SalvageRetentionReason.KeepCardOpen)]
    [InlineData(SalvageCardLifecycle.Unknown, SalvageRetentionReason.KeepCardStateUnknown)]
    [InlineData(SalvageCardLifecycle.Missing, SalvageRetentionReason.KeepCardMissing)]
    public void Non_terminal_card_keeps_its_newest_tarballs_and_caps_the_rest(
        SalvageCardLifecycle lifecycle,
        SalvageRetentionReason keptReason)
    {
        var entries = Enumerable.Range(0, 5)
            .Select(index => Tarball($"AGT-3-{index:0000}.tgz", "AGT-3", ageDays: 100 + index))
            .ToArray();

        var decisions = SalvageRetentionPolicy.Decide(
            entries,
            Cards(new SalvageCardFacts("AGT-3", lifecycle, null)),
            new HashSet<string>(),
            Settings,
            Now);

        Assert.Equal(
            [keptReason, keptReason, keptReason,
                SalvageRetentionReason.DeleteOverPerCardLimit, SalvageRetentionReason.DeleteOverPerCardLimit],
            decisions.Select(decision => decision.Reason));
    }

    [Fact]
    public void Card_without_resolved_facts_is_treated_as_unknown()
    {
        var decision = Single(Tarball("AGT-4-0100.tgz", "AGT-4", ageDays: 200), facts: null);

        Assert.Equal(SalvageRetentionReason.KeepCardStateUnknown, decision.Reason);
    }

    [Fact]
    public void Terminal_card_without_a_timestamp_is_kept_within_the_cap()
    {
        var decision = Single(
            Tarball("AGT-5-0100.tgz", "AGT-5", ageDays: 200),
            new SalvageCardFacts("AGT-5", SalvageCardLifecycle.Terminal, null));

        Assert.Equal(SalvageRetentionReason.KeepWithinRetention, decision.Reason);
    }

    [Fact]
    public void Ranking_is_newest_first_and_breaks_timestamp_ties_by_name()
    {
        var sameTime = Now.AddDays(-1);
        var entries = new[]
        {
            Tarball("AGT-6-0001.tgz", "AGT-6", created: sameTime),
            Tarball("AGT-6-0002.tgz", "AGT-6", created: sameTime),
            Tarball("AGT-6-0003.tgz", "AGT-6", created: sameTime),
            Tarball("AGT-6-0004.tgz", "AGT-6", created: sameTime),
        };

        var decisions = SalvageRetentionPolicy.Decide(
            entries, Cards(Open("AGT-6")), new HashSet<string>(), Settings, Now);

        Assert.Equal(
            ["AGT-6-0001.tgz"],
            decisions.Where(decision => decision.Delete).Select(decision => decision.Entry.Id));
    }

    [Fact]
    public void Tarballs_and_refs_are_ranked_separately_per_card()
    {
        var entries = new List<SalvageEntry>();
        entries.AddRange(Enumerable.Range(0, 3).Select(index => Tarball($"AGT-7-{index:0000}.tgz", "AGT-7", ageDays: index)));
        entries.AddRange(Enumerable.Range(0, 3).Select(index => Ref($"r{index}", "AGT-7", ageDays: index, onIntegration: true)));

        var decisions = SalvageRetentionPolicy.Decide(
            entries, Cards(Terminal("AGT-7", daysAgo: 1)), new HashSet<string>(), Settings, Now);

        Assert.All(decisions, decision => Assert.Equal(SalvageRetentionReason.KeepWithinRetention, decision.Reason));
    }

    [Theory]
    [InlineData(SalvageCardLifecycle.Open, true, SalvageRetentionReason.KeepCardOpen)]
    [InlineData(SalvageCardLifecycle.Unknown, true, SalvageRetentionReason.KeepCardStateUnknown)]
    [InlineData(SalvageCardLifecycle.Missing, true, SalvageRetentionReason.KeepCardMissing)]
    [InlineData(SalvageCardLifecycle.Terminal, false, SalvageRetentionReason.KeepNotOnIntegrationBranch)]
    [InlineData(SalvageCardLifecycle.Terminal, null, SalvageRetentionReason.KeepIntegrationUnknown)]
    [InlineData(SalvageCardLifecycle.Terminal, true, SalvageRetentionReason.DeleteRetentionElapsed)]
    public void Ref_is_only_eligible_when_its_card_is_terminal_and_its_commit_is_integrated(
        SalvageCardLifecycle lifecycle,
        bool? onIntegration,
        SalvageRetentionReason expected)
    {
        var decision = Single(
            Ref("agent-studio/salvage/r/AGT-8/a/fence-1/abc", "AGT-8", ageDays: 90, onIntegration),
            new SalvageCardFacts("AGT-8", lifecycle, lifecycle == SalvageCardLifecycle.Terminal ? Now.AddDays(-30) : null));

        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    public void Open_card_keeps_every_ref_even_over_the_cap()
    {
        var refs = Enumerable.Range(0, 6)
            .Select(index => Ref($"agent-studio/salvage/r/AGT-9/a{index}/fence-1/abc", "AGT-9", ageDays: index, onIntegration: true))
            .ToArray();

        var decisions = SalvageRetentionPolicy.Decide(
            refs, Cards(Open("AGT-9")), new HashSet<string>(), Settings, Now);

        Assert.All(decisions, decision => Assert.False(decision.Delete));
    }

    [Fact]
    public void Only_integrated_refs_of_a_recently_completed_card_consume_retention_slots()
    {
        var refs = new[]
        {
            Ref("n0", "AGT-10", ageDays: 0, onIntegration: true),
            Ref("n1", "AGT-10", ageDays: 1, onIntegration: true),
            Ref("n2", "AGT-10", ageDays: 2, onIntegration: false),
            Ref("n3", "AGT-10", ageDays: 3, onIntegration: true),
            Ref("n4", "AGT-10", ageDays: 4, onIntegration: null),
            Ref("n5", "AGT-10", ageDays: 5, onIntegration: true),
        };

        var decisions = SalvageRetentionPolicy.Decide(
            refs, Cards(Terminal("AGT-10", daysAgo: 2)), new HashSet<string>(), Settings, Now);

        Assert.Equal(
            [
                SalvageRetentionReason.KeepWithinRetention,
                SalvageRetentionReason.KeepWithinRetention,
                SalvageRetentionReason.KeepNotOnIntegrationBranch,
                SalvageRetentionReason.KeepWithinRetention,
                SalvageRetentionReason.KeepIntegrationUnknown,
                SalvageRetentionReason.DeleteOverPerCardLimit,
            ],
            decisions.Select(decision => decision.Reason));
    }

    [Fact]
    public void Invalid_settings_are_clamped_to_one()
    {
        var entries = new[]
        {
            Tarball("AGT-11-0002.tgz", "AGT-11", ageDays: 1),
            Tarball("AGT-11-0001.tgz", "AGT-11", ageDays: 2),
        };

        var decisions = SalvageRetentionPolicy.Decide(
            entries,
            Cards(Terminal("AGT-11", daysAgo: 0.5)),
            new HashSet<string>(),
            new SalvageRetentionSettings(RetentionDays: 0, MaxPerCard: 0),
            Now);

        Assert.Equal(
            [SalvageRetentionReason.KeepWithinRetention, SalvageRetentionReason.DeleteOverPerCardLimit],
            decisions.Select(decision => decision.Reason));
    }

    private static SalvageRetentionDecision Single(SalvageEntry entry, SalvageCardFacts? facts)
        => Assert.Single(SalvageRetentionPolicy.Decide(
            [entry],
            facts is null ? Cards() : Cards(facts),
            new HashSet<string>(),
            Settings,
            Now));

    private static Dictionary<string, SalvageCardFacts> Cards(params SalvageCardFacts[] facts)
        => facts.ToDictionary(item => item.CardKey, StringComparer.Ordinal);

    private static SalvageCardFacts Terminal(string card, double daysAgo)
        => new(card, SalvageCardLifecycle.Terminal, Now.AddDays(-daysAgo));

    private static SalvageCardFacts Open(string card) => new(card, SalvageCardLifecycle.Open, null);

    private static SalvageEntry Tarball(string name, string? card, double ageDays = 1, DateTime? created = null)
        => new(SalvageEntryKind.Tarball, name, null, card, created ?? Now.AddDays(-ageDays), 1024);

    private static SalvageEntry Ref(string name, string card, double ageDays, bool? onIntegration)
        => new(SalvageEntryKind.GitRef, name, "PROJ-1", card, Now.AddDays(-ageDays), 0, onIntegration, "abc");
}
