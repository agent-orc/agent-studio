using AgentStudio.Personas;
using AgentStudio.Quota;
using AgentStudio.Bus;
using AgentStudio.Tokens;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PersonaSchedulingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "persona-scheduling-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 9, 10, 30, 0, DateTimeKind.Utc);
    private static readonly PersonaAdmissionFacts Open = new("ok", true, ["runner-a", "runner-b"], false, true);

    [Fact]
    public void Restart_and_duplicate_triggers_keep_one_key_and_join_references()
    {
        var activation = Warden();
        var first = new PersonaSchedulingService(_root);
        var events = Events(new PersonaTrigger(Now.AddMinutes(-20), "deploy.completed", "evt-a"));
        var admitted = Assert.Single(first.Sweep("main", Now, null, [activation], events, _ => Open));
        Assert.Equal(PersonaOccurrenceStates.Admitted, admitted.State);

        var restarted = new PersonaSchedulingService(_root);
        var replay = Events(new PersonaTrigger(Now.AddMinutes(-20), "deploy.completed", "evt-a"),
            new PersonaTrigger(Now.AddMinutes(-10), "deploy.completed", "evt-b"));
        restarted.Sweep("main", Now, null, [activation], replay, _ => Open);

        var single = Assert.Single(restarted.Receipts("main"));
        Assert.Equal(admitted.Key, single.Key);
        Assert.Equal(["evt-a", "evt-b"], single.TriggerRefs);
    }

    [Fact]
    public void Workspace_warning_refuses_persona_with_no_call_receipt_while_cards_still_pass_quota()
    {
        var activation = Warden();
        var store = new PersonaSchedulingService(_root);
        var receipt = Assert.Single(store.Sweep("main", Now, 2_000_000, [activation],
            Events(new PersonaTrigger(Now, "deploy.completed", "evt-warning")),
            _ => Open with { QuotaStatus = WorkspaceQuotaPolicy.Warning }));

        Assert.Equal((PersonaOccurrenceStates.Waiting, "workspace-quota-warning", 0),
            (receipt.State, receipt.Reason, receipt.Tokens));
        Assert.Null(receipt.HostId);
        Assert.True(WorkspaceQuotaPolicy.Allows(new QuotaLimits(2_000_000, null, null),
            new QuotaUsage(1_600_000, 0, 0, 0), hosting: false));
        store.Sweep("main", Now.AddMinutes(5), 2_000_000, [activation],
            Events(new PersonaTrigger(Now, "deploy.completed", "evt-warning")), _ => Open);
        var retried = Assert.Single(store.Receipts("main"));
        Assert.Equal(PersonaOccurrenceStates.Admitted, retried.State);
        Assert.Equal("workspace-quota-warning", Assert.Single(retried.NoCalls).Reason);
    }

    [Fact]
    public void Replayed_week_budget_equals_completed_receipts_and_allowance_limits_sessions()
    {
        var activation = Warden() with { Allowance = Warden().Allowance with
            { TokensPerWeek = 40, SessionsPerWeek = 2 } };
        var store = new PersonaSchedulingService(_root);
        var first = Assert.Single(store.Sweep("main", Now, null, [activation],
            Events(new PersonaTrigger(Now, "deploy.completed", "evt-one")), _ => Open));
        store.Complete("main", first.Key, 10, 5, 2, 3, "Demo");
        var secondAt = Now.AddHours(6);
        var second = Assert.Single(store.Sweep("main", secondAt, null, [activation],
            Events(new PersonaTrigger(secondAt, "deploy.completed", "evt-two")), _ => Open));
        store.Complete("main", second.Key, 8, 4, 1, 1, "Demo");

        var replay = new PersonaSchedulingService(_root);
        var budget = replay.Budget("main", secondAt, null, [activation]);
        Assert.Equal(replay.Receipts("main").Sum(r => r.Tokens), budget.UsedTokens);
        Assert.Equal(34, budget.UsedTokens);
        Assert.Equal(2, budget.UsedSessions);
        Assert.Equal(34, budget.Personas[activation.PersonaId].UsedTokens);
        Assert.Equal(2, replay.TokenReceipts("Demo").Count);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        var tokenLedger = new ProjectTokenLedger(new AgentMessageBusStore(), configuration,
            new ProjectTokenReceiptReader(), personas: replay);
        var personaEntries = tokenLedger.LoadFreshSnapshot("Demo", Path.Combine(_root, "tasks")).Entries
            .Where(e => e.ParticipantId == "persona:operations-warden").ToArray();
        Assert.Equal(budget.UsedTokens, personaEntries.Sum(e => WorkspaceQuotaPolicy.ModelTokens(
            e.TokenUsage!.InputTokens, e.TokenUsage.OutputTokens,
            e.TokenUsage.CacheReadTokens, e.TokenUsage.CacheCreationTokens)));

        var thirdAt = secondAt.AddHours(6);
        var third = Assert.Single(replay.Sweep("main", thirdAt, null, [activation],
            Events(new PersonaTrigger(thirdAt, "deploy.completed", "evt-three")), _ => Open));
        Assert.Equal("persona-allowance-exhausted", third.Reason);
        Assert.Equal(0, third.Tokens);
    }

    [Fact]
    public void Floor_trigger_uses_one_hour_window_and_waits_for_cards_and_grants()
    {
        var activation = Warden() with { Descriptor = Warden().Descriptor with
            { Triggers = ["backup.failed"] } };
        var store = new PersonaSchedulingService(_root);
        var first = Assert.Single(store.Sweep("main", Now, null, [activation],
            Events(new PersonaTrigger(Now, "backup.failed", "fact-a", 95)),
            _ => Open with { ReadyCardWaiting = true }));
        Assert.Equal((Now.Date.AddHours(11), "ready-card-waiting"), (first.WindowEndUtc, first.Reason));
        var next = store.Sweep("main", Now.AddHours(1), null, [activation],
            Events(new PersonaTrigger(Now.AddHours(1), "backup.failed", "fact-b", 95)),
            _ => Open with { GrantLive = false }).Single(r => r.WindowEndUtc == Now.Date.AddHours(12));
        Assert.Equal("grant-not-live", next.Reason);
    }

    [Fact]
    public void Workspace_limit_counts_persona_instances_even_when_they_share_one_role()
    {
        var activations = Enumerable.Range(1, 3).Select(i => Warden() with
            { PersonaId = "warden-" + i, RoleId = "operations-warden" }).ToArray();
        var triggers = activations.ToDictionary(a => a.PersonaId,
            a => (IReadOnlyList<PersonaTrigger>)[new PersonaTrigger(Now, "deploy.completed", "evt-" + a.PersonaId)]);
        var store = new PersonaSchedulingService(_root);

        var receipts = store.Sweep("main", Now, null, activations, triggers,
            _ => Open with { IdleHostIds = ["host-a", "host-b", "host-c"] });

        Assert.Equal(2, receipts.Count(r => r.State == PersonaOccurrenceStates.Admitted));
        Assert.Equal("workspace-occurrences-full", Assert.Single(receipts.Where(r => r.State == PersonaOccurrenceStates.Waiting)).Reason);
        Assert.All(receipts, r => Assert.Equal("operations-warden", r.RoleId));
        Assert.Equal(3, store.Budget("main", Now, null, activations).Personas.Count);
    }

    [Fact]
    public void Model_selection_wait_keeps_the_occurrence_as_a_no_call_receipt()
    {
        var activation = Warden();
        var store = new PersonaSchedulingService(_root);
        var receipt = Assert.Single(store.Sweep("main", Now, null, [activation],
            Events(new PersonaTrigger(Now, "deploy.completed", "evt-model")),
            _ => Open with { ModelReady = false }));

        Assert.Equal((PersonaOccurrenceStates.Waiting, "model-route-wait", 0),
            (receipt.State, receipt.Reason, receipt.Tokens));
    }

    [Fact]
    public void Deactivated_persona_keeps_its_week_usage_without_reserving_allowance()
    {
        var activation = Warden();
        var store = new PersonaSchedulingService(_root);
        var admitted = Assert.Single(store.Sweep("main", Now, null, [activation],
            Events(new PersonaTrigger(Now, "deploy.completed", "evt-used")), _ => Open));
        store.Complete("main", admitted.Key, 4, 1, 0, 0, "Demo");

        var budget = store.Budget("main", Now, null,
            [activation with { State = PersonaStates.Inactive }]);

        Assert.Equal(5, budget.UsedTokens);
        Assert.Equal(5, budget.Personas[activation.PersonaId].UsedTokens);
        Assert.Equal(0, budget.Personas[activation.PersonaId].AllowanceTokens);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PersonaTrigger>> Events(params PersonaTrigger[] events)
        => new Dictionary<string, IReadOnlyList<PersonaTrigger>> { ["operations-warden"] = events };

    private static PersonaActivationV1 Warden()
    {
        var descriptor = new PersonaCatalogue().Find("operations-warden")!.Descriptor;
        return new PersonaActivationV1(descriptor.Id, PersonaSources.Catalogue, PersonaStates.Active, 1,
            descriptor.Revision, PersonaDescriptorDigest.Compute(descriptor), descriptor,
            new PersonaActivationSettingsV1(null, null, null), descriptor.Attributes, descriptor.Budget,
            descriptor.Scope, descriptor.Operations, Now.AddHours(-2), "owner", Now.AddHours(-1),
            null, null, []) { RoleId = descriptor.Id };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
