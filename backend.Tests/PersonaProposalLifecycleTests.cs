using System.Text.Json;
using AgentStudio.Docs;
using AgentStudio.Git;
using AgentStudio.Personas;
using AgentStudio.Watcher;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Persona proposal decisions (persona team PT-6) go through the scout
/// lifecycle: accept promotes through the concept Dossier's promote-concept
/// plan and spawn ledger, reject writes the Watcher fingerprint suppression,
/// defer keeps the proposal decidable, and a closed item answers a conflict.
/// </summary>
public sealed class PersonaProposalLifecycleTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "persona-lifecycle-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Accept_promotes_through_the_concept_spawn_ledger_and_a_second_decision_conflicts()
    {
        var fixture = Build(TaskStates.HumanReview);
        var item = fixture.Proposal();

        var accepted = fixture.Lifecycle.Decide("main", item.Id, new PersonaItemDecision("accepted"), Now);

        Assert.Equal("accepted", accepted.State);
        var card = fixture.Scanner.FindJob(accepted.PromotedTaskKey!, fixture.TaskStore)!;
        Assert.Equal((TaskModes.Coding, TaskStates.Preparation, "Add backup check"), (card.Mode, card.State, card.Title));
        Assert.Equal("docs/delivery-flow/index.html", card.Dossier?.Path);
        Assert.Equal("plan", card.Dossier?.Section);
        Assert.Contains($"item `{item.Id}`", File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md")));
        Assert.Contains(SpawnedTaskLedger.Read(fixture.Source.FolderPath, NullLogger.Instance),
            record => record.Reason == "persona-proposal:" + item.Id && record.TargetJobId == card.Id);
        Assert.Equal("owner-accepted", accepted.History[^1].Reason);

        var conflict = Assert.Throws<PersonaDecisionConflictException>(() =>
            fixture.Lifecycle.Decide("main", item.Id, new PersonaItemDecision("rejected"), Now));
        Assert.Equal("persona-item-already-decided", conflict.Code);
    }

    [Fact]
    public void Outcomes_follow_the_item_kind_and_reject_suppresses_the_fingerprint()
    {
        var fixture = Build(TaskStates.Completed);
        var proposal = fixture.Proposal();
        var finding = fixture.Items.Upsert("main", "warden-1", "operations-warden", "occ-1",
            Draft() with { FingerprintKey = "finding" }, 0, Now);

        Assert.Throws<ArgumentException>(() => fixture.Lifecycle.Decide("main", proposal.Id, new PersonaItemDecision("resolved"), Now));
        Assert.Throws<ArgumentException>(() => fixture.Lifecycle.Decide("main", finding.Id, new PersonaItemDecision("accepted"), Now));
        Assert.Throws<KeyNotFoundException>(() => fixture.Lifecycle.Decide("main", "pit_missing", new PersonaItemDecision("resolved"), Now));
        Assert.Equal("resolved", fixture.Lifecycle.Decide("main", finding.Id, new PersonaItemDecision("Resolved"), Now).State);

        var rejected = fixture.Lifecycle.Decide("main", proposal.Id, new PersonaItemDecision("rejected", "Covered by AGT-9"), Now);
        Assert.Equal("rejected", rejected.State);
        var suppression = Assert.Single(fixture.Suppressions.Active(_root, Now));
        Assert.Equal((proposal.Fingerprint, "Covered by AGT-9"), (suppression.Fingerprint, suppression.Reason));
    }

    [Fact]
    public void Deferred_proposal_records_a_review_date_and_can_be_decided_again()
    {
        var fixture = Build(TaskStates.Completed);
        var item = fixture.Proposal();

        Assert.Throws<ArgumentException>(() => fixture.Lifecycle.Decide("main", item.Id,
            new PersonaItemDecision("deferred", null, Now.AddMinutes(-1)), Now));
        var deferred = fixture.Lifecycle.Decide("main", item.Id, new PersonaItemDecision("deferred"), Now);
        Assert.Equal(("deferred", Now.AddDays(7)), (deferred.State, deferred.DeferredUntilUtc));

        var redeferred = fixture.Lifecycle.Decide("main", item.Id,
            new PersonaItemDecision("deferred", "Next sprint", Now.AddDays(14)), Now.AddDays(1));
        Assert.Equal(Now.AddDays(14), redeferred.DeferredUntilUtc);
        var accepted = fixture.Lifecycle.Decide("main", item.Id, new PersonaItemDecision("accepted"), Now.AddDays(2));
        Assert.Equal(("accepted", (DateTime?)null), (accepted.State, accepted.DeferredUntilUtc));
        Assert.NotNull(accepted.PromotedTaskKey);
    }

    [Fact]
    public void Accept_needs_a_reviewed_concept_dossier()
    {
        var fixture = Build(TaskStates.Backlog);
        var item = fixture.Proposal();
        Assert.Equal("proposal-dossier-not-reviewed", Assert.Throws<PersonaDecisionConflictException>(() =>
            fixture.Lifecycle.Decide("main", item.Id, new PersonaItemDecision("accepted"), Now)).Code);

        var orphan = fixture.Items.Upsert("main", "warden-1", "operations-warden", "occ-1",
            Draft() with { Kind = PersonaItemKinds.Proposal, DossierKey = "AGT-404", FingerprintKey = "orphan" }, 0, Now);
        Assert.Equal("proposal-dossier-unavailable", Assert.Throws<PersonaDecisionConflictException>(() =>
            fixture.Lifecycle.Decide("main", orphan.Id, new PersonaItemDecision("accepted"), Now)).Code);
        Assert.Equal("draft", fixture.Items.Find("main", item.Id)!.State);
    }

    private static PersonaItemDraftV1 Draft() => new(PersonaItemKinds.Finding, "Backup gap",
        "No off-host backup was found.", "Concept project", [new PersonaEvidenceV1("case:one", true)],
        4, 3, 0.8, "backup-gap", DossierSection: "plan", DraftTitle: "Add backup check",
        DraftPrompt: "Add a scheduled off-host backup check.");

    private sealed record Fixture(PersonaProposalLifecycle Lifecycle, PersonaItemStore Items,
        WatcherSuppressionStore Suppressions, TaskScannerService Scanner, TaskInfo Source, string TaskStore)
    {
        public PersonaItemV1 Proposal() => Items.Upsert("main", "warden-1", "operations-warden", "occ-1",
            Draft() with { Kind = PersonaItemKinds.Proposal, DossierKey = Source.Key ?? Source.Id, DraftCard = true }, 0, Now);
    }

    private Fixture Build(string sourceState)
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Concept project",
            ["WatchPaths:0:Path"] = taskStore,
            ["WatchPaths:0:RootPath"] = repository,
            ["WatchPaths:0:RepositoryPath"] = repository,
        }).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance).EnsureStateFoldersAndMigrate();
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();
        var mutations = new TaskMutationService(scanner, clients,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var sourceId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "concept-source", Title = "Concept source", Mode = TaskModes.Concept,
            RequiresIntegration = false, WatchPath = taskStore, TargetState = sourceState,
        })!;
        var source = scanner.FindJob(sourceId, taskStore)!;
        var directory = ConceptWorkbenchContract.CreateScaffold(repository, "delivery-flow", "Delivery flow",
            "Approved delivery design.", source.Key ?? source.Id);
        var descriptorPath = Path.Combine(directory, "workbench.json");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(File.ReadAllText(descriptorPath), web)! with
        {
            ImplementationTasks = [new ConceptImplementationTask { Title = "Implement delivery API", PromptMarkdown = "Add the endpoint." }],
        };
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, web));
        Assert.True(ConceptWorkbenchStore.Write(source.FolderPath, new ConceptWorkbenchRecord
        {
            RepoRelativeDirectory = "docs/delivery-flow",
            RepoRelativeEntrypoint = "docs/delivery-flow/index.html",
            Title = descriptor.Title,
            PublishedAt = Now,
        }));
        var promotion = new ConceptPromotionService(scanner, mutations,
            new ManagedRepositoryMutationService(new GitService(NullLogger<GitService>.Instance, scanner, config)),
            new WorkbenchChangeNotifier(NullLogger<WorkbenchChangeNotifier>.Instance),
            NullLogger<ConceptPromotionService>.Instance);
        var items = new PersonaItemStore(Path.Combine(_root, "persona-items"));
        var suppressions = new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance);
        return new Fixture(new PersonaProposalLifecycle(items, suppressions, scanner, promotion, config),
            items, suppressions, scanner, source, taskStore);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
