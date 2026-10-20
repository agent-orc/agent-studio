using AgentStudio.Docs;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DossierCardsTests
{
    private static readonly DateTime Epoch = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ImplementationProvenance_UsesSectionFromPromptOrSliceId()
    {
        var parsed = TaskDossierProvenance.FromImplementation("docs/operations/one/index.html",
            new ConceptImplementationTask { Id = "NV-2", PromptMarkdown = "Implement section 4: the card list." }, 1);
        Assert.Equal("§4", parsed.Section);
        Assert.Equal(1, parsed.ImplementationIndex);

        var fallback = TaskDossierProvenance.FromImplementation("docs/operations/one/index.html",
            new ConceptImplementationTask { Id = "NV-3", PromptMarkdown = "Remove old Epic code." }, 2);
        Assert.Equal("NV-3", fallback.Section);
    }

    [Fact]
    public void Backfill_UsesUniqueDescriptorKeysAndImplementationSlices()
    {
        var source = Task("AGT-1", "Concept");
        var related = Task("AGT-2", "Follow-up");
        var implementation = Task("AGT-3", "Build API");
        var dossier = Dossier("docs/operations/one/index.html") with
        {
            SourceTaskKeys = ["AGT-1"], RelatedTaskKeys = ["AGT-2"],
            ImplementationTasks = [new ConceptImplementationTask
            {
                Title = "Build API", Section = "§4", DecisionId = "NV-D2",
                Environments = ["staging"],
            }],
        };

        var plan = DossierBackfillPolicy.Plan([source, related, implementation], [dossier], _ => []);

        Assert.Equal(3, plan.Count);
        Assert.Equal(dossier.EntryPath, plan.Single(item => item.Task.Key == "AGT-1").Dossier.Path);
        Assert.Equal(dossier.EntryPath, plan.Single(item => item.Task.Key == "AGT-2").Dossier.Path);
        var slice = plan.Single(item => item.Task.Key == "AGT-3").Dossier;
        Assert.Equal(0, slice.ImplementationIndex);
        Assert.Equal("§4", slice.Section);
        Assert.Equal("NV-D2", slice.Decision);
        Assert.Equal(["staging"], slice.Environments);
    }

    [Fact]
    public void Backfill_LeavesAmbiguousClaimsAndExplicitPrimaryAlone()
    {
        var shared = Task("AGT-1", "Shared");
        var explicitTask = Task("AGT-2", "Explicit") with
        {
            Dossier = new TaskDossier { Path = "docs/operations/existing/index.html" },
        };
        var first = Dossier("docs/operations/one/index.html") with
        {
            RelatedTaskKeys = ["AGT-1", "AGT-2"],
        };
        var second = Dossier("docs/operations/two/index.html") with
        {
            RelatedTaskKeys = ["AGT-1"],
        };

        Assert.Empty(DossierBackfillPolicy.Plan([shared, explicitTask], [first, second], _ => []));
    }

    [Fact]
    public void Backfill_DoesNotGuessWhichImplementationCardOwnsADuplicateTitle()
    {
        var first = Task("AGT-1", "Build API");
        var second = Task("AGT-2", "Build API");
        var dossier = Dossier("docs/operations/one/index.html") with
        {
            ImplementationTasks = [new ConceptImplementationTask { Title = "Build API" }],
        };

        Assert.Empty(DossierBackfillPolicy.Plan([first, second], [dossier], _ => []));
    }

    [Fact]
    public void Backfill_RecognizesLegacyImplementationTaskKeys()
    {
        var card = Task("AGT-1", "Legacy slice");
        var dossier = Dossier("docs/operations/one/index.html") with
        {
            ImplementationTaskKeys = ["AGT-1"],
        };

        var plan = DossierBackfillPolicy.Plan([card], [dossier], _ => []);
        Assert.Equal(0, Assert.Single(plan).Dossier.ImplementationIndex);
        var list = DossierCardsService.Project("Agent Studio", dossier,
            [card with { Dossier = plan[0].Dossier, State = TaskStates.Completed }]);
        Assert.Equal(1, list.ImplementationSlices);
        Assert.Equal(1, list.CompletedSlices);
    }

    [Fact]
    public void Backfill_UsesPromotionLedgerWhenTheTitleHasChanged()
    {
        var source = Task("AGT-1", "Concept");
        var promoted = Task("AGT-2", "Renamed delivery");
        var dossier = Dossier("docs/operations/one/index.html") with
        {
            SourceTaskKeys = ["AGT-1"],
            ImplementationTasks = [new ConceptImplementationTask { Title = "Original title" }],
        };
        var plan = DossierBackfillPolicy.Plan([source, promoted], [dossier], task =>
            task.Key == source.Key
                ? [new SpawnedTaskRecord
                {
                    Reason = $"concept-promotion:{dossier.EntryPath}:0", TargetKey = promoted.Key,
                }]
                : []);

        Assert.Equal(0, plan.Single(item => item.Task.Key == promoted.Key).Dossier.ImplementationIndex);
    }

    [Fact]
    public void CardList_ReportsLaneStateEnvironmentsAndVisibleSliceProgress()
    {
        var dossier = Dossier("docs/operations/one/index.html") with
        {
            ImplementationTasks = [new(), new(), new()],
        };
        var cards = new[]
        {
            Task("AGT-1", "Done") with
            {
                State = TaskStates.Completed,
                Dossier = new TaskDossier { Path = dossier.EntryPath, ImplementationIndex = 0,
                    Environments = ["production"] },
            },
            Task("AGT-2", "Doing") with
            {
                State = TaskStates.Progress, Phase = "running",
                Dossier = new TaskDossier { Path = dossier.EntryPath, ImplementationIndex = 1 },
            },
            Task("AGT-3", "Elsewhere") with
            {
                Dossier = new TaskDossier { Path = "docs/operations/other/index.html" },
            },
        };

        var list = DossierCardsService.Project("Agent Studio", dossier, cards);

        Assert.Equal(3, list.ImplementationSlices);
        Assert.Equal(1, list.CompletedSlices);
        Assert.Equal(2, list.Cards.Count);
        Assert.Equal(TaskStates.Completed, list.Cards[0].Lane);
        Assert.Equal("running", list.Cards[1].State);
        Assert.Equal(["production"], list.Cards[0].Environments);
    }

    [Fact]
    public void CardList_WaitsForAllImplementationCardsAfterADecision()
    {
        var dossier = Dossier("docs/operations/one/index.html") with
        {
            ImplementationTasks = [new ConceptImplementationTask { Title = "Choose approach" }],
        };
        var reference = new TaskDossier { Path = dossier.EntryPath, ImplementationIndex = 0 };
        var decision = Task("AGT-1", "Choose approach") with
        {
            Kind = TaskKinds.Decision, State = TaskStates.Completed, Dossier = reference,
        };
        var first = Task("AGT-2", "Implement API") with
        {
            State = TaskStates.Completed, Dossier = reference,
        };
        var second = Task("AGT-3", "Document API") with
        {
            State = TaskStates.Ready, Dossier = reference,
        };

        Assert.Equal(0, DossierCardsService.Project("Agent Studio", dossier,
            [decision, first, second]).CompletedSlices);
        Assert.Equal(1, DossierCardsService.Project("Agent Studio", dossier,
            [decision, first, second with { State = TaskStates.Completed }]).CompletedSlices);
    }

    private static TaskInfo Task(string key, string title) => new()
    {
        Id = key.ToLowerInvariant(), TaskKey = key, Key = key, Title = title,
        ProjectName = "Agent Studio", State = TaskStates.Backlog, CreatedAt = Epoch,
    };

    private static WorkbenchListItem Dossier(string path) =>
        new(path.Split('/')[^2], "Dossier", "", "active", "shaping",
            Epoch, path, true, null, []) { Key = "AGT-W1" };
}
