using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Whether a Dossier can create its implementation cards (AGT-3875). The
/// Dossier view offers Create implementation cards only when this says so and
/// otherwise names the missing piece: no concept card, no concept review, a
/// failed review, or no validated proposals.
/// </summary>
public sealed class WorkbenchPromotionPolicyTests
{
    private static readonly PromoteConceptResponse TwoSlices = new()
    {
        Items = [new() { Title = "API" }, new() { Title = "UI" }],
    };

    [Fact]
    public void Resolve_IsAvailableForTheReviewedDossierOfItsConceptCard()
    {
        var promotion = WorkbenchPromotionPolicy.Resolve("Project", Dossier(["PRJ-7"]),
            [Card("concept", "PRJ-7", TaskModes.Concept)],
            _ => new ConceptPromotionPlanRead(TwoSlices, null, null, "docs/delivery-flow"));

        Assert.True(promotion.Available);
        Assert.Null(promotion.Reason);
        Assert.Equal("PRJ-7", promotion.SourceTaskKey);
        Assert.Equal("concept", promotion.SourceJobId);
        Assert.Equal(2, promotion.ProposalCount);
    }

    [Fact]
    public void Resolve_WithoutSourceKeys_AsksForAConceptCard_EvenWhenRelatedCardsExist()
    {
        var read = 0;
        var promotion = WorkbenchPromotionPolicy.Resolve("Project", Dossier([], related: ["PRJ-1"]),
            [Card("related", "PRJ-1", TaskModes.Concept)],
            _ => { read++; return new ConceptPromotionPlanRead(TwoSlices, null, null); });

        Assert.False(promotion.Available);
        Assert.Equal(ConceptPromotionRefusals.NoConceptCard, promotion.Reason);
        Assert.Contains("no source concept card", promotion.Message);
        Assert.Equal(0, read);
    }

    [Fact]
    public void Resolve_SkipsASourceCardThatIsNotAConceptCard()
    {
        var promotion = WorkbenchPromotionPolicy.Resolve("Project", Dossier(["PRJ-2"]),
            [Card("coding", "PRJ-2", TaskModes.Coding)],
            _ => new ConceptPromotionPlanRead(TwoSlices, null, null));

        Assert.Equal(ConceptPromotionRefusals.NoConceptCard, promotion.Reason);
        Assert.Contains("PRJ-2", promotion.Message);
    }

    [Theory]
    [InlineData(ConceptPromotionRefusals.NoReview, "has not delivered a reviewed Dossier")]
    [InlineData(ConceptPromotionRefusals.ReviewFailed, "did not pass concept review")]
    public void Resolve_PassesThePlanRefusalWithTheConceptCard(string code, string message)
    {
        var promotion = WorkbenchPromotionPolicy.Resolve("Project", Dossier(["PRJ-7"]),
            [Card("concept", "PRJ-7", TaskModes.Concept)],
            _ => ConceptPromotionPlanRead.Refused(code, message));

        Assert.False(promotion.Available);
        Assert.Equal(code, promotion.Reason);
        Assert.Equal(message, promotion.Message);
        Assert.Equal("PRJ-7", promotion.SourceTaskKey);
    }

    [Fact]
    public void Resolve_RefusesWhenTheConceptCardDeliveredAnotherDossier()
    {
        var promotion = WorkbenchPromotionPolicy.Resolve("Project", Dossier(["PRJ-7"]),
            [Card("concept", "PRJ-7", TaskModes.Concept)],
            _ => new ConceptPromotionPlanRead(TwoSlices, null, null, "docs/other-topic"));

        Assert.Equal(ConceptPromotionRefusals.OtherDossier, promotion.Reason);
        Assert.Contains("docs/other-topic", promotion.Message);
    }

    [Fact]
    public void Resolve_RefusesWithoutValidatedProposals()
    {
        var empty = WorkbenchPromotionPolicy.Resolve("Project", Dossier(["PRJ-7"]),
            [Card("concept", "PRJ-7", TaskModes.Concept)],
            _ => new ConceptPromotionPlanRead(new PromoteConceptResponse(), null, null, "docs/delivery-flow"));
        var none = WorkbenchPromotionPolicy.Resolve("Project", Dossier(["PRJ-7"], slices: 0),
            [Card("concept", "PRJ-7", TaskModes.Concept)],
            _ => new ConceptPromotionPlanRead(TwoSlices, null, null));

        Assert.Equal(ConceptPromotionRefusals.NoProposals, empty.Reason);
        Assert.Contains("no validated implementation cards", empty.Message);
        Assert.Equal(ConceptPromotionRefusals.NoProposals, none.Reason);
    }

    // clock-independent: the policy never reads UpdatedAtUtc; the date only fills the record.
    private static WorkbenchListItem Dossier(string[] sources, string[]? related = null, int slices = 2) =>
        new("delivery-flow", "Delivery flow", "Summary", "decision-pending", "decision-ready",
            new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), "docs/delivery-flow/index.html", true, null,
            [.. sources, .. related ?? []])
        {
            DescriptorSourceTaskKeys = sources,
            RelatedTaskKeys = related ?? [],
            ImplementationTasks = Enumerable.Range(0, slices)
                .Select(index => new ConceptImplementationTask { Title = $"Slice {index}" }).ToList(),
        };

    private static TaskInfo Card(string id, string key, string mode) => new()
    {
        Id = id,
        Key = key,
        TaskKey = $"Project::{key}",
        Title = $"Card {id}",
        State = TaskStates.HumanReview,
        ProjectName = "Project",
        Mode = mode,
    };
}
