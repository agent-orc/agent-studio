using System.Text.Json;
using AgentStudio.Personas;
using AgentStudio.Shared;
using AgentStudio.Operator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PersonaManagerPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Manager_bounds_refuse_with_stable_codes()
    {
        Assert.Equal("manager-role-forbidden", Refused(() =>
            PersonaManagerPolicy.RequireRole("budget-steward", "card.requeue")));
        var card = new TaskInfo { State = TaskStates.Backlog,
            CreationSource = "persona-proposal:pit_one", Tags = [] };
        Assert.Equal("daily-move-limit", Refused(() =>
            PersonaManagerPolicy.RequireMove(card, TaskStates.Ready, null, 5)));
        Assert.Equal("manager-lane-forbidden", Refused(() =>
            PersonaManagerPolicy.RequireMove(card, TaskStates.Progress, null, 0)));
        Assert.Equal("proposal-origin-required", Refused(() =>
            PersonaManagerPolicy.RequireMove(card with { CreationSource = "human" },
                TaskStates.Ready, null, 0)));
        Assert.Equal("weekly-rebalance-limit", Refused(() =>
            PersonaManagerPolicy.RequireRebalance(26, 100, 100, 0, 0)));
        Assert.Equal("weekly-rebalance-limit", Refused(() =>
            PersonaManagerPolicy.RequireRebalance(20, 100, 100, 10, 0)));
    }

    [Fact]
    public void Promotion_refuses_fourth_and_own_draft()
    {
        var item = Item();
        Assert.Equal("daily-promotion-limit", Refused(() =>
            PersonaManagerPolicy.RequirePromotion(item, "chief-of-staff", 3)));
        Assert.Equal("own-draft-forbidden", Refused(() =>
            PersonaManagerPolicy.RequirePromotion(item with { PersonaId = "chief-of-staff" },
                "chief-of-staff", 0)));
    }

    [Fact]
    public void Grounds_serialize_only_typed_item_fields()
    {
        var catalogue = new PersonaCatalogue().Find("chief-of-staff")!;
        var descriptor = catalogue.Descriptor;
        var activation = new PersonaActivationV1("chief-of-staff", PersonaSources.Catalogue,
            PersonaStates.Active, 1, descriptor.Revision, catalogue.Digest, descriptor,
            new PersonaActivationSettingsV1(null, null, null), descriptor.Attributes,
            descriptor.Budget, descriptor.Scope, descriptor.Operations, Now, "owner", Now,
            null, null, []) { RoleId = descriptor.Id };
        var receipt = new PersonaOccurrenceReceipt("occ", "main", activation.PersonaId,
            descriptor.Id, descriptor.Revision, Now, Now, PersonaOccurrenceStates.Admitted,
            null, [], null);
        var grounds = PersonaManagerPolicy.Grounds(receipt, activation, [Item()]);
        var json = JsonSerializer.Serialize(grounds);
        Assert.Single(grounds.ManagerItems);
        Assert.DoesNotContain("untrusted body", json, StringComparison.Ordinal);
        Assert.DoesNotContain("repository content", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"State\"", json, StringComparison.Ordinal);
        Assert.Contains("fingerprint", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Importance_adjustment_refuses_floor_and_large_delta()
    {
        var store = new PersonaItemStore((string?)null);
        var item = store.Upsert("main", "security-a", "security", "occ",
            new PersonaItemDraftV1(PersonaItemKinds.Finding, "Title", "Body", null,
                [new PersonaEvidenceV1("fact:one")], 1, 1, 0.5, "fingerprint"),
            90, Now);
        Assert.Equal("adjustment-out-of-bounds", Refused(() =>
            store.Adjust("main", item.Id, -16, "reason", "manager", Now)));
        Assert.Equal("importance-floor-or-ceiling", Refused(() =>
            store.Adjust("main", item.Id, -1, "reason", "manager", Now)));
        var raised = store.Adjust("main", item.Id, 10, "reason", "manager", Now);
        Assert.Equal(100, raised.Importance);
        store.RecomputeOpen("main", Now.AddDays(1));
        Assert.Equal(100, store.Find("main", item.Id)!.Importance);
        Assert.Equal(item.Importance, store.UndoManagerItem("main", item.Id, 100,
            item.Importance, false, "owner", Now).Importance);
    }

    [Fact]
    public void Owner_attention_lease_refuses_manager_acquisition()
    {
        var root = Path.Combine(Path.GetTempPath(), "persona-manager-attention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var attention = new OperatorCoordinationStore(config,
                new FakeTimeProvider(new DateTimeOffset(Now)));
            Assert.NotNull(attention.Acquire("card-one", "human:owner", true));
            Assert.Null(attention.Acquire("card-one", "persona:chief/session:one", false));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static PersonaItemV1 Item() => new("pit_one", "scout-one", "scout",
        PersonaItemKinds.Proposal, "repository content", "untrusted body", null,
        [new PersonaEvidenceV1("case:one")], 3, 3, 0.5, 0, 27,
        "fingerprint", "draft", Now, Now, []);

    private static string Refused(Action action)
        => Assert.Throws<PersonaManagerRefusal>(action).Code;
}
