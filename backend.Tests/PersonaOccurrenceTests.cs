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

    [Fact]
    public void Every_starter_report_has_two_shadow_periods_with_explicit_coverage()
    {
        var catalogue = new PersonaCatalogue();
        var store = new PersonaReportStore(_root);
        foreach (var descriptor in catalogue.Entries.Select(e => e.Descriptor)
                     .Where(d => (d.Kind is PersonaKinds.Persona or PersonaKinds.Presenter) && d.Id != "operations-warden"))
        {
            var template = PersonaReportTemplates.Find(descriptor.Report.Template)!;
            var activation = Warden() with { PersonaId = "prs_" + descriptor.Id,
                RoleId = descriptor.Id, Descriptor = descriptor, State = PersonaStates.Shadow,
                ShadowUntil = Now.AddDays(15), Scope = descriptor.Scope,
                Operations = descriptor.Operations, Attributes = descriptor.Attributes,
                Allowance = descriptor.Budget, DescriptorRevision = descriptor.Revision,
                DescriptorDigest = PersonaDescriptorDigest.Compute(descriptor) };
            for (var week = 0; week < 2; week++)
            {
                var at = Now.AddDays(week * 7);
                var source = new PersonaGroundsSourceV1("source:" + descriptor.Id + ":" + week,
                    template.SourceKinds[0], null, 100, template.Anchors[0] + " observed", true);
                var grounds = new PersonaGroundsV1(1, "occ:" + descriptor.Id + ":" + week,
                    activation.PersonaId, descriptor.Id, "main", descriptor.Revision,
                    activation.DescriptorDigest, [source], []);
                var line = new PersonaItemDraftV1(PersonaItemKinds.ReportLine,
                    "Shadow coverage", template.Anchors[0] + " observed", null,
                    [new PersonaEvidenceV1(source.Ref, true)], 2, 2, 0.7,
                    descriptor.Id + ":" + week);
                Assert.Null(PersonaOutputValidator.Validate(new([line]), grounds, activation,
                    [], [], [], at));
                var receipt = new PersonaOccurrenceReceipt(grounds.OccurrenceKey, "main", activation.PersonaId,
                    descriptor.Id, descriptor.Revision, at, at, PersonaOccurrenceStates.Completed,
                    null, [], "runner", 120, 30);
                var report = store.SaveCompleted("main", receipt, activation, grounds, [], at);
                Assert.Equal((descriptor.Report.Template, "shadow", 150L),
                    (report.Template, report.State, report.TokensUsed));
                Assert.Equal(template.Anchors.Count, report.Coverage.Count);
                Assert.Contains(report.Coverage, row => row.EndsWith(":observed", StringComparison.Ordinal));
                Assert.Contains(report.Coverage, row => row.EndsWith(":no-evidence", StringComparison.Ordinal));
                Assert.Equal(report.Id, store.SaveCompleted("main", receipt, activation, grounds, [], at).Id);
            }
            Assert.Equal(2, store.List("main").Count(r => r.PersonaId == activation.PersonaId));
        }
    }

    [Fact]
    public void Scope_narrowing_refuses_source_areas_outside_the_persona_role()
    {
        var descriptor = new PersonaCatalogue().Find("security-officer")!.Descriptor;
        var security = Warden() with { PersonaId = "prs_security", RoleId = descriptor.Id,
            Descriptor = descriptor, Scope = descriptor.Scope with { Areas = ["security.permissions"] } };
        Assert.Equal("security.permissions", PersonaGroundsPolicy.Area(security, "permission grant changed"));
        Assert.Null(PersonaGroundsPolicy.Area(security, "dependency package changed"));
        Assert.Null(PersonaGroundsPolicy.Area(security, "runner latency changed"));
        var design = new PersonaCatalogue().Find("design-lead")!.Descriptor;
        Assert.Null(PersonaGroundsPolicy.Area(security, design.Charter));
        var filtered = PersonaGroundsPolicy.Filter(security,
            [new("permission:one", "card", "Alpha", 0, "permission", false)
                { Area = "security.permissions" },
             new("dependency:one", "card", "Alpha", 0, "package", false)
                { Area = "security.dependencies" }]);
        Assert.Equal("permission:one", Assert.Single(filtered).Ref);
        var excluded = security with { Scope = security.Scope with { ExcludedPaths = ["docs/private"] } };
        Assert.False(PersonaGroundsPolicy.PathAllowed(excluded, "docs/private/workbench.json"));
        Assert.True(PersonaGroundsPolicy.PathAllowed(excluded, "docs/public/workbench.json"));
        Assert.All(new PersonaCatalogue().Entries.Where(e => e.Descriptor.Kind != PersonaKinds.Manager),
            entry => Assert.False(PersonaGroundsPolicy.CanCloneRepository(Warden() with
            { Descriptor = entry.Descriptor, RoleId = entry.Descriptor.Id })));
        var cost = new PersonaCatalogue().Find("cost-controller")!.Descriptor;
        var narrowedCost = Warden() with { Descriptor = cost, RoleId = cost.Id,
            Scope = cost.Scope with { Projects = ["Alpha"] } };
        Assert.Equal("project-a", Assert.Single(PersonaGroundsPolicy.QuotaScopes(narrowedCost,
            [("project-a", "Alpha"), ("project-b", "Beta")])).Id);
        var presenter = new PersonaCatalogue().Find("presenter")!.Descriptor;
        Assert.False(PersonaGroundsPolicy.CanReadWorkspaceIssue(Warden() with
        { Descriptor = presenter, RoleId = presenter.Id, Scope = presenter.Scope with { Projects = ["Alpha"] } }));
    }

    [Fact]
    public void Rejected_proposal_stays_suppressed_until_new_evidence_and_deferred_updates_in_place()
    {
        var activation = Warden();
        var grounds = new PersonaGroundsV1(1, "key", activation.PersonaId, activation.RoleId!,
            "main", activation.DescriptorRevision, activation.DescriptorDigest,
            [new PersonaGroundsSourceV1("case:one", "watcher-case", "Alpha", 0, "Signal", true),
             new PersonaGroundsSourceV1("case:two", "watcher-case", "Alpha", 0, "New signal", true),
             new PersonaGroundsSourceV1("dossier:AGT-1", "dossier", "Alpha", 0, "Home Dossier", false)], []);
        var proposal = Draft("Alpha", "case:one") with { Kind = PersonaItemKinds.Proposal,
            DossierKey = "AGT-1", DossierSection = "plan" };
        var store = new PersonaItemStore(_root);
        var stored = store.Upsert("main", activation.PersonaId, activation.RoleId!, "occ-1", proposal, 0, Now, grounds);
        Assert.NotNull(store.RecordDecision("main", stored.Id, "rejected", "not now", Now));

        Assert.Equal("proposal-rejection-suppressed", PersonaOutputValidator.Validate(new([proposal]),
            grounds, activation, store.List("main"), [], [], Now));
        var withNewEvidence = proposal with { Evidence = [new PersonaEvidenceV1("case:two", true)] };
        Assert.Null(PersonaOutputValidator.Validate(new([withNewEvidence]), grounds, activation,
            store.List("main"), [], [], Now));

        var deferred = store.Upsert("main", activation.PersonaId, activation.RoleId!, "occ-2", withNewEvidence, 0, Now, grounds);
        store.RecordDecision("main", deferred.Id, "deferred", "review later", Now, Now.AddDays(7));
        var again = store.Upsert("main", activation.PersonaId, activation.RoleId!, "occ-3", withNewEvidence, 0, Now.AddHours(1), grounds);
        Assert.Equal((deferred.Id, "deferred"), (again.Id, again.State));
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
