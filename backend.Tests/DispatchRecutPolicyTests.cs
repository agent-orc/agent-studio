using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: all policy observations and source order are frozen facts.
public sealed class DispatchRecutPolicyTests
{
    private static DispatchRecutCard Card(string key, int order, double? prior = 8, params string[] files)
        => new(key, order, "playthrough", files.Length == 0 ? ["m/a.cs", "m/b.cs"] : files,
            ["m"], 1, prior, null, true);
    private static DispatchRecutPlan Plan(params DispatchRecutCard[] cards)
        => DispatchRecutPolicy.Plan(new("p", "w", cards, ["hub.cs"], [], true));

    [Fact]
    public void Packages_three_findings_at_most_and_chains_the_rest_in_source_order()
    {
        var plan = Plan(Card("D", 3), Card("B", 1), Card("A", 0), Card("C", 2));
        var package = Assert.Single(plan.Actions, a => a.Kind == "package");
        Assert.Equal(new[] { "A", "B", "C" }, package.Cards);
        Assert.True(package.Apply);
        var edge = Assert.Single(plan.Edges);
        Assert.Equal("D", edge.Card);
        Assert.Equal(package.Id, edge.DependsOn);
        Assert.Equal("dispatch:packer", edge.Origin);
    }

    [Theory]
    [InlineData(12.5, true)]
    [InlineData(13, false)]
    [InlineData(26, false)]
    public void Combined_size_prior_is_bounded_at_twenty_five(double prior, bool merged)
    {
        var plan = Plan(Card("A", 0, prior), Card("B", 1, prior));
        Assert.Equal(merged, plan.Actions.Any(a => a.Kind == "package"));
    }

    [Fact]
    public void Missing_prior_prevents_packaging_but_does_not_prevent_a_safe_chain()
    {
        var plan = Plan(Card("A", 0, null), Card("B", 1, null));
        Assert.DoesNotContain(plan.Actions, a => a.Kind == "package");
        Assert.Equal("A", Assert.Single(plan.Edges).DependsOn);
    }

    [Fact]
    public void Hubs_do_not_create_packages_or_overlap_clusters()
    {
        var plan = Plan(Card("A", 0, 5, "hub.cs", "a.cs"), Card("B", 1, 5, "hub.cs", "b.cs"));
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.Edges);
    }

    [Fact]
    public void One_shared_non_hub_file_chains_without_packaging()
    {
        var plan = Plan(Card("A", 0, 5, "hub.cs", "shared.cs", "a.cs"), Card("B", 1, 5, "hub.cs", "shared.cs", "b.cs"));
        Assert.Single(plan.Actions, a => a.Kind == "chain");
        Assert.DoesNotContain(plan.Actions, a => a.Kind == "package");
    }

    [Fact]
    public void A_transitive_cluster_is_chained_but_is_not_a_three_card_package()
    {
        var plan = Plan(Card("A", 0, 5, "a.cs", "b.cs"), Card("B", 1, 5, "a.cs", "b.cs", "c.cs", "d.cs"),
            Card("C", 2, 5, "c.cs", "d.cs"));
        Assert.Equal(2, Assert.Single(plan.Actions, a => a.Kind == "package").Cards.Count);
        Assert.Single(plan.Edges);
    }

    [Fact]
    public void Collection_counts_limit_packages_and_cold_collections_remain_whole()
    {
        var a = Card("A", 0) with { FindingCount = 3 };
        var plan = Plan(a, Card("B", 1));
        Assert.DoesNotContain(plan.Actions, a => a.Kind is "package" or "split");
        Assert.Single(plan.Edges);
        Assert.Empty(Plan(a).Actions);
    }

    [Fact]
    public void Large_and_hot_collection_cards_propose_module_splits_and_chain_the_hot_part()
    {
        var a = Card("A", 0) with { Modules = ["m1", "m2", "m3", "m4"] };
        var b = Card("B", 1, 26);
        var c = Card("C", 2) with { FindingCount = 2, Modules = ["hot", "cold"] };
        var plan = DispatchRecutPolicy.Plan(new("p", "w", [a, b, c], [], ["hot"], true));
        Assert.Equal(3, plan.Actions.Count(a => a.Kind == "split"));
        Assert.All(plan.Actions.Where(a => a.Kind == "split"), a => Assert.False(a.Apply));
        var hot = Assert.Single(plan.Actions, a => a.Reason == "collection-hot-module");
        Assert.Equal(new[] { "cold", "hot" }, hot.Modules);
        Assert.Equal(new[] { "A", "B" }, hot.HotWaitsFor);
    }

    [Fact]
    public void Shadow_unstarted_manual_protected_and_cross_dossier_cuts_remain_proposals()
    {
        var a = Card("A", 0);
        var b = Card("B", 1);
        var shadow = DispatchRecutPolicy.Plan(new("p", "w", [a, b], [], [], false));
        Assert.All(shadow.Actions, a => Assert.False(a.Apply));
        foreach (var changed in new[] { a with { Unstarted = false }, a with { SourceKind = "other" }, a with { Protected = true } })
            Assert.All(Plan(changed, b).Actions, action => Assert.False(action.Apply));
        Assert.DoesNotContain(Plan(a with { Dossier = "one" }, b with { Dossier = "two" }).Actions, action => action.Kind == "package");
    }

    [Fact]
    public void Medians_use_real_history_and_source_classification_is_an_allow_list()
    {
        Assert.Null(DispatchRecutPolicy.Median([]));
        Assert.Equal(17, DispatchRecutPolicy.Median([2, 17, 29]));
        Assert.Equal(12.5, DispatchRecutPolicy.Median([5, 20]));
        Assert.Equal("playthrough", DispatchRecutPolicy.SourceKind("playthrough:product-gaps"));
        Assert.Equal("review-findings", DispatchRecutPolicy.SourceKind("review-finding:wave-1"));
        Assert.Equal("playthrough", DispatchRecutPolicy.SourceKind("playthrough:dispatch:packer:receipt"));
        Assert.Equal("other", DispatchRecutPolicy.SourceKind("human:playthrough"));
    }

    [Fact]
    public void Undo_expires_at_the_exact_twenty_four_hour_boundary()
    {
        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        Assert.True(DispatchRecutPolicy.MayUndo("applied", at.AddHours(24), at.AddHours(24).AddTicks(-1)));
        Assert.False(DispatchRecutPolicy.MayUndo("applied", at.AddHours(24), at.AddHours(24)));
        Assert.False(DispatchRecutPolicy.MayUndo("proposed", at.AddHours(24), at));
    }

    [Fact]
    public void Archive_permits_only_recorded_untouched_members_and_never_bypasses_foreign_edges()
    {
        var f = new DispatchRecutArchiveFacts(true, true, true, false, true, false, true, true);
        Assert.True(DispatchRecutPolicy.MayArchive(f));
        Assert.False(DispatchRecutPolicy.MayArchive(f with { LiveAuthority = false }));
        Assert.False(DispatchRecutPolicy.MayArchive(f with { OriginalPointer = false }));
        Assert.False(DispatchRecutPolicy.MayArchive(f with { UnstartedBacklog = false }));
        Assert.False(DispatchRecutPolicy.MayArchive(f with { ForeignIncoming = true }));
        Assert.False(DispatchRecutPolicy.MayArchive(f with { Protected = true }));
        Assert.False(DispatchRecutPolicy.MayArchive(f with { WindowOpen = false }));
        Assert.True(DispatchRecutPolicy.MayArchive(f with { Applying = false, Undoing = true,
            OriginalMember = false, ReplacementMember = true, LiveAuthority = false }));
    }
}
