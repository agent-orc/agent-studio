using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class HoldScopePolicyTests
{
    [Fact]
    public void Active_hold_facts_exclude_report_only_lifted_and_expired_entries_and_preserve_exemptions()
    {
        // clock-independent: lifecycle admission uses the supplied fixed observation.
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var held = new ProjectTestQuarantine("quarantine-one", "Product.Tests.Isolated", "repair", DateOnly.MaxValue,
            null, "confirmed twice", "held", "system:post-merge", now, now.AddHours(24), "fingerprint", [],
            ["backend/Features/Pipeline/PostMerge"])
        { TestFile = "backend.Tests/IsolatedTests.cs", ConfirmedSuspectPaths = ["contracts/Suspect.cs"],
            AttributedRevertCardId = "revert", OwnerExemptCardIds = ["exempt"] };
        var facts = HoldScopePolicy.ActiveHolds([held, held with { Id = "report", Kind = "report-only" },
            held with { Id = "expired", ExpiresAtUtc = now }, held with { Id = "lifted", LiftedAtUtc = now }], now);
        var fact = Assert.Single(facts);
        Assert.Equal("quarantine-one", fact.QuarantineId);
        Assert.Equal(held.ExpiresAtUtc, fact.ExpiresAtUtc);
        Assert.Contains("backend.Tests/IsolatedTests.cs", fact.Files);
        Assert.Contains("quarantine-one", HoldScopePolicy.IntegrationBlock(["backend/Features/Pipeline/PostMerge/A.cs"], facts, "ordinary"));
        Assert.Null(HoldScopePolicy.IntegrationBlock(["frontend/A.ts"], facts, "ordinary"));
        foreach (var card in new[] { "repair", "revert", "exempt" })
            Assert.Null(HoldScopePolicy.IntegrationBlock(["backend/Features/Pipeline/PostMerge/A.cs"], facts, card));
        Assert.NotNull(HoldScopePolicy.IntegrationBlock(["contracts/Suspect.cs"], facts, "ordinary"));
    }

    [Theory]
    [InlineData("backend/Feature/A.cs", false)]
    [InlineData("backend/Feature/Sub/A.cs", true)]
    [InlineData("backend/FeatureOther/A.cs", true)]
    [InlineData("backend.Tests/FailingTests.cs", false)]
    [InlineData("contracts/Suspect.cs", false)]
    [InlineData("contracts/Other.cs", true)]
    public void Hold_covers_exact_directories_test_file_and_confirmed_suspect_files(string path, bool allowed)
    {
        var scope = HoldScopePolicy.Create("backend.Tests/FailingTests.cs", ["backend/Feature"],
            ["contracts/Suspect.cs"], ["backend", "backend.Tests"]);
        Assert.Empty(scope.SuiteAreas);
        Assert.Equal(allowed, HoldScopePolicy.MayIntegrate([path], [new(scope, "repair")], "card"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(12, false)]
    [InlineData(13, true)]
    public void Empty_or_more_than_twelve_covering_folders_falls_back_to_suite_area(int count, bool fallback)
    {
        var folders = Enumerable.Range(0, count).Select(i => $"backend/Folder{i}").ToArray();
        var scope = HoldScopePolicy.Create("backend.Tests/Test.cs", folders, ["contracts/Suspect.cs"], ["backend", "backend.Tests"]);
        Assert.Equal(fallback, scope.SuiteAreas.Count > 0);
        Assert.Equal(!fallback, HoldScopePolicy.MayIntegrate(["backend/Other/Nested/A.cs"], [new(scope, "repair")], "card"));
        Assert.True(HoldScopePolicy.MayIntegrate(["backend-other/A.cs"], [new(scope, "repair")], "card"));
        Assert.False(HoldScopePolicy.MayIntegrate(["contracts/Suspect.cs"], [new(scope, "repair")], "card"));
    }

    [Theory]
    [InlineData("repair", true)]
    [InlineData("revert", true)]
    [InlineData("owner-exempt", true)]
    [InlineData("ordinary", false)]
    public void Exempt_cards_pass_their_hold(string card, bool allowed)
    {
        var scope = HoldScopePolicy.Create("tests/Test.cs", ["source"], [], []);
        Assert.Equal(allowed, HoldScopePolicy.MayIntegrate(["source/A.cs"],
            [new(scope, "repair", "revert", ["owner-exempt"])], card));
    }

    [Fact]
    public void An_exemption_from_one_hold_does_not_exempt_another()
    {
        var scope = HoldScopePolicy.Create("tests/Test.cs", ["source"], [], []);
        Assert.False(HoldScopePolicy.MayIntegrate(["source/A.cs"],
            [new(scope, "repair"), new(scope, "other-repair")], "repair"));
        Assert.True(HoldScopePolicy.MayIntegrate(["source/A.cs"], [], "ordinary"));
    }

    [Fact]
    public void Normalized_distinct_folders_determine_the_fallback_bound()
    {
        var scope = HoldScopePolicy.Create("tests\\Test.cs", Enumerable.Repeat("source\\Feature/", 13).ToArray(), [], []);
        Assert.Empty(scope.SuiteAreas);
        Assert.False(HoldScopePolicy.MayIntegrate(["source/Feature/A.cs"], [new(scope, "repair")], "ordinary"));
        Assert.False(HoldScopePolicy.MayIntegrate(["tests/Test.cs"], [new(scope, "repair")], "ordinary"));
    }

    [Theory]
    [InlineData("source/A.cs", true)]
    [InlineData("source/B.cs", false)]
    [InlineData("source/A.cs/sub", false)]
    public void Later_first_parent_merge_is_coupled_only_by_file_overlap(string laterPath, bool coupled)
        => Assert.Equal(coupled, HoldScopePolicy.LaterMergeIsCoupled(["source/A.cs"], [laterPath]));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(12, false)]
    [InlineData(13, true)]
    public void Runner_fallback_flag_matches_the_hold_scope_bound(int folders, bool fallback)
    {
        // RT-3: the runner records coveringFallback with the same rule Create applies.
        var covering = Enumerable.Range(0, folders).Select(i => $"source/F{i}").ToArray();
        Assert.Equal(fallback, HoldScopePolicy.UsesSuiteAreaFallback(covering));
        Assert.Equal(fallback, HoldScopePolicy.Create("tests/T.cs", covering, [], ["source"]).SuiteAreas.Count > 0);
    }
}
