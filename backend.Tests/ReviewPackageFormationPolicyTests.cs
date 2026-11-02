using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the formation policy receives explicit timestamps as pure inputs.
/// <summary>
/// Package formation of AGT-3400 PK5 A: one Dossier, card checks within 60
/// minutes of the first, at most four members, within 200 files and 12,000
/// lines of union material, never waiting for a coding card.
/// </summary>
public sealed class ReviewPackageFormationPolicyTests
{
    private const string Dossier = "docs/review-gate-packages/index.html";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Forms_the_cards_of_one_dossier_whose_check_passed_within_sixty_minutes_of_the_first()
    {
        var decisions = Plan(T0.AddMinutes(75),
            Passed("A", 0), Passed("B", 30), Passed("C", 70), Passed("X", 10, dossier: "docs/other/index.html"));

        var wave = decisions.Where(decision => decision.DossierPath == Dossier).ToArray();
        Assert.Equal(2, wave.Length);
        Assert.Equal(["A", "B"], wave[0].Members);
        Assert.Equal(ReviewPackageFormationDecisions.Form, wave[0].Decision);
        Assert.Equal(ReviewPackageFormationPolicy.WindowReason, wave[0].Reason);
        Assert.Equal(["C"], wave[1].Members);
        var other = Assert.Single(decisions, decision => decision.DossierPath == "docs/other/index.html");
        Assert.Equal(["X"], other.Members);
    }

    [Fact]
    public void A_lone_member_forms_a_package_of_one()
    {
        var decision = Assert.Single(Plan(T0.AddMinutes(1), Passed("A", 0)));

        Assert.Equal(ReviewPackageFormationDecisions.Form, decision.Decision);
        Assert.Equal(ReviewPackageFormationPolicy.LoneReason, decision.Reason);
        Assert.Equal(["A"], decision.Members);
    }

    [Fact]
    public void A_coding_card_is_never_waited_for()
    {
        var decision = Assert.Single(Plan(T0.AddMinutes(1), Passed("A", 0),
            Card("B", ReviewPackageCandidateStages.Coding), Card("C", ReviewPackageCandidateStages.Coding)));

        Assert.Equal(ReviewPackageFormationDecisions.Form, decision.Decision);
        Assert.Equal(["A"], decision.Members);
        Assert.Empty(decision.Waiting);
    }

    [Fact]
    public void A_card_in_review_is_waited_for_until_the_window_closes()
    {
        var waiting = Assert.Single(Plan(T0.AddMinutes(5), Passed("A", 0), Card("B", ReviewPackageCandidateStages.InReview)));
        Assert.Equal(ReviewPackageFormationDecisions.Wait, waiting.Decision);
        Assert.Equal(T0.AddMinutes(60), waiting.CloseAtUtc);
        Assert.Equal(["B"], waiting.Waiting);

        var closed = Assert.Single(Plan(T0.AddMinutes(60), Passed("A", 0), Card("B", ReviewPackageCandidateStages.InReview)));
        Assert.Equal(ReviewPackageFormationDecisions.Form, closed.Decision);
        Assert.Equal(ReviewPackageFormationPolicy.WindowReason, closed.Reason);
        Assert.Equal(["A"], closed.Members);
    }

    [Fact]
    public void Caps_a_package_at_four_members()
    {
        var decisions = Plan(T0.AddMinutes(20), Passed("A", 0), Passed("B", 1), Passed("C", 2), Passed("D", 3), Passed("E", 4));

        Assert.Equal(["A", "B", "C", "D"], decisions[0].Members);
        Assert.Equal(ReviewPackageFormationPolicy.MemberCapReason, decisions[0].Reason);
        Assert.Equal(["E"], decisions[1].Members);
    }

    [Fact]
    public void Splits_at_the_union_file_budget_and_counts_shared_paths_once()
    {
        var shared = Paths("shared", 150);
        var together = Assert.Single(Plan(T0.AddMinutes(1), Passed("A", 0, shared), Passed("B", 1, shared)));
        Assert.Equal(["A", "B"], together.Members);
        Assert.Equal(150, together.UnionFiles);

        var split = Plan(T0.AddMinutes(1), Passed("A", 0, Paths("a", 150)), Passed("B", 1, Paths("b", 100)));
        Assert.Equal(["A"], split[0].Members);
        Assert.Equal(ReviewPackageFormationPolicy.BudgetReason, split[0].Reason);
        Assert.Equal(["B"], split[1].Members);
    }

    [Fact]
    public void Splits_at_the_line_budget_and_leaves_an_oversized_card_per_card()
    {
        var split = Plan(T0.AddMinutes(1), Passed("A", 0, lines: 8_000), Passed("B", 1, lines: 5_000));
        Assert.Equal(["A"], split[0].Members);
        Assert.Equal(ReviewPackageFormationPolicy.BudgetReason, split[0].Reason);
        Assert.Equal(12_000, ReviewPackageLimits.Default.MaximumLines);

        var alone = Plan(T0.AddMinutes(1), Passed("A", 0, lines: 12_001), Passed("B", 1, lines: 10));
        Assert.Equal(ReviewPackageFormationDecisions.PerCard, alone[0].Decision);
        Assert.Equal(ReviewPackageFormationPolicy.OverBudgetReason, alone[0].Reason);
        Assert.Equal(["B"], alone[1].Members);
    }

    [Fact]
    public void Stacks_members_by_dependency_then_implementation_index()
    {
        var decision = Assert.Single(Plan(T0.AddMinutes(1),
            Passed("A", 0) with { ImplementationIndex = 3, DependsOn = ["B"] },
            Passed("B", 1) with { ImplementationIndex = 5 },
            Passed("C", 2) with { ImplementationIndex = 1 }));

        Assert.Equal(["C", "B", "A"], decision.Members);
    }

    [Fact]
    public void Cards_without_a_dossier_stay_per_card()
        => Assert.Empty(Plan(T0.AddMinutes(1), Passed("A", 0, dossier: "")));

    private static IReadOnlyList<ReviewPackageFormationDecision> Plan(DateTimeOffset now, params ReviewPackageCandidate[] cards)
        => ReviewPackageFormationPolicy.Plan(new ReviewPackageFormationInput(now, cards, ReviewPackageLimits.Default));

    private static ReviewPackageCandidate Passed(string key, int minute, IReadOnlyList<string>? paths = null,
        int lines = 40, string dossier = Dossier)
        => new(key, dossier, ReviewPackageCandidateStages.CardCheckPassed, T0.AddMinutes(minute),
            paths ?? [$"src/{key}.cs"], lines);

    private static ReviewPackageCandidate Card(string key, string stage)
        => new(key, Dossier, stage, null, [], 0, false);

    private static string[] Paths(string prefix, int count)
        => Enumerable.Range(0, count).Select(index => $"{prefix}/{index}.cs").ToArray();
}
