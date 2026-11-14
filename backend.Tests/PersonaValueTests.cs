using AgentStudio.Personas;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PersonaValueTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private readonly PersonaItemStore _store = new((string?)null);

    [Fact]
    public void Probation_triggers_for_first_four_weeks_only()
    {
        Assert.Contains(Suggest(Activation(Now.AddDays(-27)), [], [], []), s => s.Kind == "probation");
        Assert.DoesNotContain(Suggest(Activation(Now.AddDays(-28)), [], [], []), s => s.Kind == "probation");
    }

    [Fact]
    public void Low_acceptance_requires_ten_decisions_and_strictly_below_thirty_percent()
    {
        var activation = Activation(Now.AddDays(-35));
        Assert.Contains(Suggest(activation, Findings(10, 2), [], []), s => s.Kind == "pause");
        Assert.DoesNotContain(Suggest(activation, Findings(10, 3), [], []), s => s.Kind == "pause");
        Assert.DoesNotContain(Suggest(activation, Findings(9, 2), [], []), s => s.Kind == "pause");
    }

    [Fact]
    public void Expensive_finding_requires_two_consecutive_complete_months()
    {
        var activation = Activation(Now.AddDays(-120));
        var items = new[] { Finding("oct", Now.AddDays(-25), true), Finding("sep", Now.AddDays(-55), true) };
        var costly = new[] { Receipt("oct", Now.AddDays(-25), 3.1), Receipt("sep", Now.AddDays(-55), 3.1) };
        Assert.Contains(Suggest(activation, items, costly, [], 1), s => s.Kind == "pause");
        Assert.DoesNotContain(Suggest(activation, items, [costly[0]], [], 1), s => s.Kind == "pause");
        Assert.DoesNotContain(Suggest(activation, items, [Receipt("oct", Now.AddDays(-25), 3), costly[1]], [], 1), s => s.Kind == "pause");
    }

    [Fact]
    public void Scope_suggestion_requires_two_high_misses_in_one_month()
    {
        var activation = Activation(Now.AddDays(-35));
        var one = new PersonaMissV1("First", "item", "pit_1", "/items/pit_1", 70, Now.AddDays(-2), 1);
        var two = one with { Name = "Second", SourceId = "pit_2", AtUtc = Now.AddDays(-3) };
        Assert.Contains(Suggest(activation, [], [], [one, two]), s => s.Kind == "focus");
        Assert.DoesNotContain(Suggest(activation, [], [], [one, two with { Importance = 69 }]), s => s.Kind == "focus");
    }

    [Fact]
    public void Retirement_suggestion_requires_eight_weeks_without_acceptance()
    {
        var old = Activation(Now.AddDays(-70));
        Assert.Contains(Suggest(old, [], [], []), s => s.Kind == "retirement");
        Assert.DoesNotContain(Suggest(Activation(Now.AddDays(-55)), [], [], []), s => s.Kind == "retirement");
        Assert.DoesNotContain(Suggest(old, [Finding("recent", Now.AddDays(-1), true)], [], []), s => s.Kind == "retirement");
    }

    [Fact]
    public void Misses_link_to_first_external_item_or_incident_card_and_ratings_do_not_affect_value()
    {
        var activation = Activation(Now.AddDays(-80));
        var missed = Finding("external", Now.AddDays(-2), false) with
        { PersonaId = "other", Importance = 95, Area = "security.code" };
        var incident = new TaskInfo { TaskKey = "INC-1", Title = "Backup incident", CreatedAt = Now.AddDays(-3),
            ProjectName = "main", Tags = ["incident", "area:security.code"] };
        var plain = PersonaValueProjection.Build("main", activation, [activation], [missed], [], [], [incident], Now);
        var rated = PersonaValueProjection.Build("main", activation, [activation], [missed], [],
            [new PersonaReportRatingV1("report", activation.PersonaId, "useful", Now)], [incident], Now);
        Assert.Contains(plain.Misses, m => m.Source == "item" && m.Url.EndsWith(missed.Id, StringComparison.Ordinal));
        Assert.Contains(plain.Misses, m => m.Source == "card" && m.Url.EndsWith("INC-1", StringComparison.Ordinal));
        Assert.Equal(plain.WeightedAcceptedPer100kTokens, rated.WeightedAcceptedPer100kTokens);
        Assert.Equal(plain.CostPerAcceptedUsd, rated.CostPerAcceptedUsd);
        Assert.Equal(1, rated.Useful);
    }

    [Fact]
    public void Report_rating_is_one_optional_click_per_report()
    {
        var store = new PersonaReportRatingStore((string?)null);
        var report = new PersonaReportRecordV1("prp_one", "mira", "security", "occ", "security-v1",
            "published", "", Now, [], [], [], 0, 1);
        Assert.Equal("useful", store.Rate("main", report, "useful", Now).Rating);
        Assert.Equal("useful", store.Rate("main", report, "not-useful", Now).Rating);
        Assert.Single(store.List("main"));
    }

    [Fact]
    public void Value_uses_importance_and_tokens_and_revision_comparison_lasts_four_weeks()
    {
        var activation = Activation(Now.AddDays(-80)) with { History =
        [new PersonaActivationEventV1(Now.AddDays(-6), "owner", "attributes", "active", 2,
            "v1", "digest", "Adjust focus")] };
        var finding = Finding("accepted", Now.AddDays(-2), true) with { Importance = 80 };
        var receipt = Receipt("accepted", Now.AddDays(-2), 2) with { InputTokens = 800, OutputTokens = 200 };
        var value = PersonaValueProjection.Build("main", activation, [activation], [finding], [receipt], [], [], Now);
        Assert.Equal(8_000, value.WeightedAcceptedPer100kTokens);
        Assert.Equal(2, value.CostPerAcceptedUsd);
        Assert.True(value.ShowRevisionComparison);
        Assert.False(PersonaValueProjection.Build("main", activation, [activation], [finding], [receipt], [], [],
            Now.AddDays(22)).ShowRevisionComparison);
    }

    private IReadOnlyList<PersonaSuggestionV1> Suggest(PersonaActivationV1 activation,
        IReadOnlyList<PersonaItemV1> items, IReadOnlyList<PersonaOccurrenceReceipt> receipts,
        IReadOnlyList<PersonaMissV1> misses, double? median = null)
        => PersonaValueProjection.Suggest(activation, items, receipts, misses, median, Now);

    private PersonaItemV1[] Findings(int count, int accepted) => Enumerable.Range(0, count)
        .Select(index => Finding("f" + index, Now.AddDays(-1), index < accepted)).ToArray();
    private PersonaItemV1 Finding(string key, DateTime at, bool accepted) => _store.Upsert("main", "mira",
        "security", "occ-" + key, new PersonaItemDraftV1(PersonaItemKinds.Finding, key, "Evidence",
            null, [], 5, 5, 1, key, Area: "security.code"), 0, at) with
        { State = accepted ? "accepted" : "rejected", UpdatedAtUtc = at, Importance = 95 };
    private static PersonaOccurrenceReceipt Receipt(string key, DateTime at, double cost) =>
        new("occ-" + key, "main", "mira", "security", "window", at.AddDays(1), at,
            PersonaOccurrenceStates.Completed, null, [], "runner", 100, 20) { CostUsd = cost };
    private static PersonaActivationV1 Activation(DateTime at)
    {
        var descriptor = new PersonaCatalogue().Find("security-officer")!.Descriptor;
        return new PersonaActivationV1("mira", PersonaSources.Catalogue, PersonaStates.Active, 1,
            descriptor.Revision, PersonaDescriptorDigest.Compute(descriptor), descriptor,
            new PersonaActivationSettingsV1(null, null, null), descriptor.Attributes, descriptor.Budget,
            descriptor.Scope, PersonaAutonomy.Grantable(descriptor, descriptor.Attributes.Autonomy),
            at, "owner", at.AddDays(1), null, null, []) { RoleId = descriptor.Id };
    }
}
