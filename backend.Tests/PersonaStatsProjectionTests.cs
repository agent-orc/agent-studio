using AgentStudio.Personas;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PersonaStatsProjectionTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Rates_hide_below_five_decisions_and_revisions_remain_separate()
    {
        var activation = Activation();
        var store = new PersonaItemStore((string?)null);
        var items = Enumerable.Range(0, 6).Select(index =>
        {
            var draft = new PersonaItemDraftV1(PersonaItemKinds.Finding, "Finding " + index,
                "Evidence", null, [], 3, 3, .5, "finding-" + index);
            return store.Upsert("main", activation.PersonaId, "security", "occ-" + index,
                draft, 0, Now.AddDays(-index)) with
            {
                State = index < 4 ? "accepted" : "rejected",
                ActivationRevision = index < 4 ? 1 : 2,
            };
        }).ToArray();
        var receipts = new[] { new PersonaOccurrenceReceipt("occ-0", "main", activation.PersonaId,
            "security", "2026.11.1", Now, Now.AddDays(-1), PersonaOccurrenceStates.Completed,
            null, [], "runner", 100, 20) { ActivationRevision = 1, CostUsd = .5 } };
        var otherPersona = items[0] with { Id = "other-persona-item", PersonaId = "aster", RoleId = "security" };
        var result = PersonaStatsProjection.Build(activation, [.. items, otherPersona], receipts, [], Now);
        Assert.Equal(6, result.Windows[0].Acceptance.Denominator);
        Assert.Equal(4, result.Windows[0].Acceptance.Numerator);
        Assert.Equal(66.7, result.Windows[0].Acceptance.Percent);
        Assert.Equal(2, result.Revisions.Count);
        Assert.All(result.Revisions, revision => Assert.Null(revision.Acceptance.Percent));
        Assert.Equal("Probation", result.Rank);
        Assert.Equal(activation.Allowance.TokensPerWeek, 100_000);
    }

    [Fact]
    public void Senior_is_eligibility_and_unknown_price_never_becomes_zero_cost_per_finding()
    {
        var activation = Activation() with { ActivatedAt = Now.AddDays(-120) };
        var store = new PersonaItemStore((string?)null);
        var items = Enumerable.Range(0, 10).Select(index => store.Upsert("main", activation.PersonaId,
            "security", "occ-" + index, new PersonaItemDraftV1(PersonaItemKinds.Finding,
                "Finding " + index, "Evidence", null, [], 3, 3, .5, "finding-" + index),
                0, Now.AddDays(-index)) with { State = index < 7 ? "accepted" : "rejected",
                    ActivationRevision = 2, PromotedTaskKey = index == 0 ? "AGT-1" : null }).ToArray();
        var receipt = new PersonaOccurrenceReceipt("occ-0", "main", activation.PersonaId,
            "security", "2026.11.1", Now, Now.AddDays(-1), PersonaOccurrenceStates.Completed,
            null, [], "runner", 100, 20) { ActivationRevision = 2 };
        var result = PersonaStatsProjection.Build(activation, items, [receipt], [], Now,
            [new TaskInfo { Id = "card-1", TaskKey = "AGT-1", State = TaskStates.Completed }]);
        Assert.Equal("Senior", result.Rank);
        Assert.Null(result.Windows[0].CostPerAcceptedUsd);
        Assert.Equal(1, result.Windows[0].UnknownPriceSessions);
        Assert.Equal(1, result.Windows[0].Cards.Completed);
        Assert.Equal(0, result.Windows[0].Cards.Open);
        Assert.Contains("Eligible", result.Eligibility);
    }

    [Fact]
    public void Misses_count_external_critical_items_and_incident_cards_in_scope_by_revision()
    {
        var activation = Activation() with
        {
            ActivatedAt = Now.AddDays(-120),
            History =
            [
                new PersonaActivationEventV1(Now.AddDays(-120), "owner", "activate", "active", 1,
                    "v1", "digest", null),
                new PersonaActivationEventV1(Now.AddDays(-5), "owner", "attributes", "active", 2,
                    "v1", "digest", null),
            ],
        };
        var store = new PersonaItemStore((string?)null);
        PersonaItemV1 Item(string persona, string fingerprint, string area, DateTime at) =>
            store.Upsert("main", persona, "security", "occ-" + fingerprint,
                new PersonaItemDraftV1(PersonaItemKinds.Finding, fingerprint, "Evidence", null,
                    [], 5, 5, 1, fingerprint, Area: area), 0, at) with
            { Importance = 95, Area = area };
        var ownFirst = Item("mira", "known", "security.code", Now.AddDays(-12));
        var externalKnown = Item("aster", "known", "security.code", Now.AddDays(-10));
        var externalMiss = Item("aster", "missed", "security.code", Now.AddDays(-2));
        var outside = Item("aster", "outside", "operations.backups", Now.AddDays(-2));
        var incident = new TaskInfo { TaskKey = "INC-1", CreatedAt = Now.AddDays(-20),
            ProjectName = "main", Tags = ["incident", "area:security.code"] };
        var result = PersonaStatsProjection.Build(activation,
            [ownFirst, externalKnown, externalMiss, outside], [], [], Now, [incident]);

        Assert.Equal(2, result.Windows[0].Misses);
        Assert.Equal(2, result.Windows[1].Misses);
        Assert.Equal(1, result.Revisions.Single(r => r.ActivationRevision == 1).Misses);
        Assert.Equal(1, result.Revisions.Single(r => r.ActivationRevision == 2).Misses);
    }

    private static PersonaActivationV1 Activation()
    {
        var descriptor = new PersonaCatalogue().Find("security-officer")!.Descriptor;
        return new PersonaActivationV1("mira", PersonaSources.Catalogue, PersonaStates.Active,
            2, descriptor.Revision, PersonaDescriptorDigest.Compute(descriptor), descriptor,
            new PersonaActivationSettingsV1(null, null, null), descriptor.Attributes,
            descriptor.Budget with { TokensPerWeek = 100_000 }, descriptor.Scope,
            PersonaAutonomy.Grantable(descriptor, descriptor.Attributes.Autonomy), Now.AddDays(-10),
            "owner", Now.AddDays(-9), null, null, []) { RoleId = descriptor.Id };
    }
}
