using AgentStudio.Personas;
using AgentStudio.Shared;
using AgentStudio.Watcher;
using AgentStudio.Bus;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PersonaOccurrenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "persona-occurrence-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unresolvable_reference_and_out_of_scope_project_fail_the_receipt()
    {
        var activation = Warden();
        var grounds = new PersonaGroundsV1(1, "key", activation.PersonaId, activation.RoleId!,
            "main", activation.DescriptorRevision, activation.DescriptorDigest,
            [new PersonaGroundsSourceV1("card:one", "card", "Alpha", 20, "A card", false)], []);
        var missing = Draft("Alpha", "card:missing");
        Assert.Equal("reference-unresolvable", PersonaOutputValidator.Validate(new([missing]), grounds,
            activation, [], [], [], Now));
        var outside = Draft("Beta", "card:one");
        Assert.Equal("project-out-of-scope", PersonaOutputValidator.Validate(new([outside]), grounds,
            activation, [], [], [], Now));
        var dossier = Draft("Alpha", "card:one") with { Kind = PersonaItemKinds.Proposal,
            Evidence = [new PersonaEvidenceV1("card:one")], DossierKey = "AGT-MISSING",
            DossierSection = "plan" };
        Assert.Equal("dossier-reference-unresolvable", PersonaOutputValidator.Validate(new([dossier]),
            grounds, activation, [], [], [], Now));

        var scheduling = new PersonaSchedulingService(_root);
        var receipt = Assert.Single(scheduling.Sweep("main", Now, null, [activation],
            new Dictionary<string, IReadOnlyList<PersonaTrigger>>
            {
                [activation.PersonaId] = [new PersonaTrigger(Now, "deploy.completed", "event:one")],
            }, _ => new PersonaAdmissionFacts("ok", true, ["runner-a"], false, true)));
        var claim = scheduling.Claim("main", receipt.Key, "runner-a", Now, "sha256:grounds", "model")!;
        var failed = scheduling.Finish("main", receipt.Key, "runner-a", claim.ClaimToken!,
            false, "reference-unresolvable", 10, 1, null)!;
        Assert.Equal((PersonaOccurrenceStates.Failed, "reference-unresolvable"), (failed.State, failed.Reason));
    }

    [Fact]
    public void Importance_matches_three_examples_and_preserves_fact_floor()
    {
        Assert.Equal(100, PersonaImportanceService.Score(5, 5, 1));
        Assert.Equal(43, PersonaImportanceService.Score(4, 3, 0.8));
        Assert.Equal(13, PersonaImportanceService.Score(2, 2, 0.6));
        Assert.Equal(95, PersonaImportanceService.Score(1, 1, 0, 95));
        Assert.Equal(0.7, PersonaImportanceService.CapConfidence(1,
            [new PersonaEvidenceV1("card:one", true)]));
        Assert.Equal(0.5, PersonaImportanceService.CapConfidence(1,
            [new PersonaEvidenceV1("card:one"), new PersonaEvidenceV1("case:one")]));
    }

    [Fact]
    public void Validator_enforces_item_lengths_and_evidence_truth()
    {
        var activation = Warden();
        var grounds = new PersonaGroundsV1(1, "key", activation.PersonaId, activation.RoleId!,
            "main", activation.DescriptorRevision, activation.DescriptorDigest,
            [new PersonaGroundsSourceV1("card:one", "card", "Alpha", 0, "A card", false)], []);
        var oversized = Draft("Alpha", "card:one") with
        {
            Title = new string('x', 121), Evidence = [new PersonaEvidenceV1("card:one")],
        };
        Assert.Equal("item-schema-invalid", PersonaOutputValidator.Validate(new([oversized]), grounds,
            activation, [], [], [], Now));
        var inventedMeasurement = Draft("Alpha", "card:one");
        Assert.Equal("measurement-unverified", PersonaOutputValidator.Validate(new([inventedMeasurement]),
            grounds, activation, [], [], [], Now));
    }

    [Fact]
    public void Watcher_fingerprint_blocks_a_duplicate_proposal_even_with_a_new_title()
    {
        var activation = Warden();
        const string fingerprint = "rep-0123456789abcdef";
        var grounds = new PersonaGroundsV1(1, "key", activation.PersonaId, activation.RoleId!,
            "main", activation.DescriptorRevision, activation.DescriptorDigest,
            [new PersonaGroundsSourceV1("case:one", "watcher-case", "Alpha", 0,
                "Repeated signal", true, Fingerprint: fingerprint),
             new PersonaGroundsSourceV1("dossier:AGT-1", "dossier", "Alpha", 0,
                "Home Dossier", false)], []);
        var proposal = Draft("Alpha", "case:one") with { Kind = PersonaItemKinds.Proposal,
            Title = "A differently worded proposal", DossierKey = "AGT-1", DossierSection = "plan" };
        var watcher = new WatcherProposal { Id = "WPR-1", CaseId = "WCH-1",
            DetectorClass = WatcherDetectorClasses.Repetition, Fingerprint = fingerprint,
            Project = "Alpha", Title = "Original proposal" };

        Assert.Equal("proposal-duplicate-existing", PersonaOutputValidator.Validate(new([proposal]),
            grounds, activation, [], [watcher], [], Now));
    }

    [Fact]
    public void Persona_is_an_additive_bus_participant_kind()
    {
        var participant = new AgentParticipant { Id = "persona:prs_one", Kind = "Persona",
            DisplayName = "Security analyst" };
        Assert.True(AgentMessageValidator.TryValidate(participant, out var error), error);
    }

    [Fact]
    public void Repeated_fingerprint_updates_one_item_and_records_score_history()
    {
        var store = new PersonaItemStore(_root);
        var first = store.Upsert("main", "prs_one", "operations-warden", "occ-one",
            Draft("Alpha", "card:one"), 70, Now);
        var second = store.Upsert("main", "prs_one", "operations-warden", "occ-two",
            Draft("Alpha", "card:one") with { Title = "Updated fact", Urgency = 2 }, 70,
            Now.AddHours(1));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(new PersonaItemStore(_root).List("main"));
        Assert.Equal(2, second.History.Count);
        Assert.True(second.Importance >= second.Floor);
        var aged = Assert.Single(store.RecomputeOpen("main", Now.AddDays(35)));
        Assert.Equal(5, aged.Urgency);
        Assert.Equal(3, aged.History.Count);
    }

    [Fact]
    public void Exhausted_allowance_has_stub_and_model_free_fact_at_floor()
    {
        var activation = Warden();
        var receipt = new PersonaOccurrenceReceipt("key", "main", activation.PersonaId,
            activation.RoleId!, activation.DescriptorRevision, Now, Now,
            PersonaOccurrenceStates.Waiting, "persona-allowance-exhausted",
            ["backup:backup-missing"], null)
        {
            NoCalls = [new PersonaNoCallAttempt(Now, "persona-allowance-exhausted")],
            FactFloors = new Dictionary<string, int> { ["backup:backup-missing"] = 90 },
        };
        var drafts = PersonaOccurrenceService.RefusalDrafts(receipt, activation.Descriptor);

        Assert.Equal(2, drafts.Count);
        Assert.Equal(PersonaItemKinds.ReportLine, drafts[0].Draft.Kind);
        Assert.Equal(PersonaItemKinds.Finding, drafts[1].Draft.Kind);
        Assert.True(drafts[1].Floor >= 70);
        Assert.Contains("No model call", drafts[0].Draft.Body);
        var item = new PersonaItemStore(_root).Upsert("main", activation.PersonaId, activation.RoleId!,
            receipt.Key, drafts[1].Draft, drafts[1].Floor, Now);
        Assert.True(item.Importance >= 90);
        var reports = new PersonaReportStore(_root);
        var report = reports.SaveStub("main", receipt, activation.Descriptor,
            new PersonaItemStore(_root).List("main"), [receipt], Now);
        Assert.Equal("stub", report.State);
        Assert.Contains(report.OpenItems, row => row.ItemId == item.Id && row.Importance >= 90);
        Assert.Contains("backup:backup-missing", report.SourcesMissing);
        Assert.Equal(report.Id, reports.SaveStub("main", receipt, activation.Descriptor,
            [], [receipt], Now.AddMinutes(1)).Id);
    }

    private static PersonaItemDraftV1 Draft(string project, string reference) => new(
        PersonaItemKinds.Finding, "Example fact", "A measured finding.", project,
        [new PersonaEvidenceV1(reference, true)], 4, 3, 0.8, "stable-fact");

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
