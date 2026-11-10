using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ConceptPipelineTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "concept-pipeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Scaffold_ProducesReviewableWorkbenchDocument()
    {
        Directory.CreateDirectory(_root);

        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root,
            "concept-pipeline",
            "Concept pipeline",
            "A document-first task pipeline.",
            "AGT-2358");
        var review = ConceptWorkbenchContract.ReviewChangedFiles(
            _root,
            [
                "docs/concept-pipeline/workbench.json",
                "docs/concept-pipeline/index.html",
            ],
            "AGT-2358");

        Assert.True(review.IsComplete, review.Summary);
        Assert.Equal("concept-pipeline", review.Topic);
        Assert.Equal("concept", review.Descriptor!.Pattern);
        Assert.True(File.Exists(Path.Combine(directory, "workbench.json")));
        Assert.True(File.Exists(Path.Combine(directory, "index.html")));
        var html = File.ReadAllText(Path.Combine(directory, "index.html"));
        Assert.Contains("data-article-template=\"v2\"", html);
        Assert.Contains("data-document-section=\"evidence\"", html);
        Assert.Contains(DossierImplementationContract.SectionStartMarker, html);
        Assert.Contains(DossierImplementationContract.LogStartMarker, html);
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(Path.Combine(directory, "workbench.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("decision-pending", descriptor!.Status);
        Assert.Equal(["AGT-2358"], descriptor.SourceTaskKeys);
    }

    [Fact]
    public void Scaffold_RendersTheUiVariantFromTheCanonicalArticleTemplate()
    {
        Directory.CreateDirectory(_root);

        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root,
            "visual-options",
            "Visual options",
            "Compare two interface directions.",
            "AGT-2536",
            "ui");
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(Path.Combine(directory, "workbench.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var html = File.ReadAllText(Path.Combine(directory, "index.html"));

        Assert.Equal("ui", descriptor.Pattern);
        Assert.Contains("data-document-pattern=\"ui\"", html);
        Assert.Contains("data-article-template=\"v2\"", html);
        Assert.Contains("width: min(70ch", html);
        Assert.Contains("[data-document-pattern=\"ui\"] .variant-grid", html);
        Assert.Contains("[data-document-pattern=\"concept\"] .evidence-class", html);
        Assert.Contains("data-article-example=\"evidence-figure\"", html);
        Assert.Contains("docs/operations/&lt;slug&gt;/assets/", html);
        Assert.Contains("figure-as-of", html);
        Assert.Contains("AOW-W1", html);
    }

    [Fact]
    public void Review_RejectsAWorkbenchWithoutRequiredEvidenceSection()
    {
        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root, "incomplete", "Incomplete", "Missing evidence.", "AGT-2358");
        var entrypoint = Path.Combine(directory, "index.html");
        File.WriteAllText(
            entrypoint,
            File.ReadAllText(entrypoint).Replace(
                "data-document-section=\"evidence\"",
                "data-removed-section=\"evidence\"",
                StringComparison.Ordinal));

        var review = ConceptWorkbenchContract.ReviewDirectory(
            _root, "docs/incomplete", "AGT-2358");

        Assert.False(review.IsComplete);
        Assert.Contains(review.Findings, finding => finding.Contains("evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewConceptDelivery_CreatesDossierAndProjectsBothTaskFileReferences()
    {
        var taskFolder = Path.Combine(_root, "task");
        Directory.CreateDirectory(taskFolder);
        var dossierPath = "docs/sidesheet-concept/index.html";

        ConceptWorkbenchContract.CreateScaffold(
            _root,
            "sidesheet-concept",
            "Sidesheet concept",
            "A decision dossier.",
            "AGT-2548");

        ConceptDossierContract.WriteReference(taskFolder, dossierPath);

        var review = ConceptWorkbenchContract.ReviewDirectory(
            _root,
            "docs/sidesheet-concept",
            "AGT-2548");
        Assert.True(review.IsComplete, review.Summary);
        Assert.True(File.Exists(Path.Combine(_root, dossierPath)));
        Assert.True(File.Exists(Path.Combine(_root, "docs", "sidesheet-concept", "workbench.json")));
        Assert.Empty(ConceptDossierContract.ReviewAgentReferences(taskFolder, dossierPath));
        var summary = TaskEndpointHelpers.BuildConceptDossierSummary(new TaskInfo
        {
            Id = "concept-card",
            Mode = TaskModes.Concept,
            FolderPath = taskFolder,
        });
        Assert.NotNull(summary);
        Assert.True(summary.ContractSatisfied);
        Assert.Equal(dossierPath, summary.RepoRelativePath);
        Assert.Equal("results/deliverables.md", summary.ReferenceSource);
        Assert.Contains(dossierPath, File.ReadAllText(Path.Combine(taskFolder, "status.md")));
        Assert.Contains(dossierPath, File.ReadAllText(Path.Combine(taskFolder, "results", "deliverables.md")));
        Assert.Null(ConceptDossierContract.NormalizePath("/docs/sidesheet-concept/index.html"));
        Assert.True(ConceptDossierContract.IsDossierPath("docs/operations/legacy/index.html"));
    }

    [Fact]
    public void Review_RejectsWrongSourceCardAndNonPendingStatus()
    {
        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root, "wrong-source", "Wrong source", "Invalid descriptor.", "AGT-OTHER");
        var descriptorPath = Path.Combine(directory, "workbench.json");
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(descriptorPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))! with { Status = "active" };
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var review = ConceptWorkbenchContract.ReviewDirectory(
            _root, "docs/wrong-source", "AGT-2358");

        Assert.False(review.IsComplete);
        Assert.Contains(review.Findings, finding => finding.Contains("decision-pending", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(review.Findings, finding => finding.Contains("AGT-2358", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Review_KeepsLegacyOperationsWorkbenchReadableWithoutMigration()
    {
        var created = ConceptWorkbenchContract.CreateScaffold(
            _root, "legacy", "Legacy", "Published before the dossier contract.", "AGT-OLD");
        var descriptorPath = Path.Combine(created, "workbench.json");
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(descriptorPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))! with { Status = "active" };
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(descriptor, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var legacyParent = Path.Combine(_root, "docs", "operations");
        Directory.CreateDirectory(legacyParent);
        Directory.Move(created, Path.Combine(legacyParent, "legacy"));

        var review = ConceptWorkbenchContract.ReviewDirectory(
            _root, "docs/operations/legacy", "AGT-NEW");

        Assert.True(review.IsComplete, review.Summary);
        Assert.Equal("legacy", review.Topic);
    }

    [Fact]
    public void Promotion_CreatesCodingCardsFromPublishedDocument_Idempotently()
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);
        var (_, scanner, _, mutations) = BuildServices(repository, taskStore);
        var sourceId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "concept-source",
            Title = "Concept source",
            Mode = TaskModes.Concept,
            RequiresIntegration = false,
            WatchPath = taskStore,
            TargetState = TaskStates.HumanReview,
        });
        Assert.NotNull(sourceId);
        var source = scanner.FindJob(sourceId!, taskStore);
        Assert.NotNull(source);

        var workbenchDirectory = ConceptWorkbenchContract.CreateScaffold(
            repository,
            "delivery-flow",
            "Delivery flow",
            "Approved delivery design.",
            source!.Key ?? source.Id);
        var descriptorPath = Path.Combine(workbenchDirectory, "workbench.json");
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(descriptorPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        descriptor = descriptor with
        {
            ImplementationTasks =
            [
                new ConceptImplementationTask
                {
                    Title = "Implement delivery API",
                    PromptMarkdown = "Add the approved delivery endpoint.",
                    Section = "§4",
                    DecisionId = "delivery-mode",
                },
                new ConceptImplementationTask
                {
                    Title = "Implement delivery UI",
                    PromptMarkdown = "Add the approved delivery controls.",
                },
            ],
        };
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(
                descriptor,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.AppendAllText(Path.Combine(workbenchDirectory, "index.html"), """
            <div data-decision-id="delivery-mode" data-decision-kind="single" data-decision-label="Delivery mode">
              <label data-option-id="queued" data-option-label="A: queued (recommended)"><input checked></label>
              <label data-option-id="direct" data-option-label="B: direct"><input></label>
            </div>
            """);
        Assert.True(ConceptWorkbenchStore.Write(source.FolderPath, new ConceptWorkbenchRecord
        {
            RepoRelativeDirectory = "docs/delivery-flow",
            RepoRelativeEntrypoint = "docs/delivery-flow/index.html",
            Title = descriptor.Title,
            PublishedAt = DateTime.UtcNow,
        }));

        var plan = scanner.BuildPromoteConceptPlan(source.Id, taskStore);
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Items.Count);
        var service = new ConceptPromotionService(
            scanner, mutations, NullLogger<ConceptPromotionService>.Instance);

        var first = service.Promote(source, plan, new PromoteConceptRequest());
        var repeated = service.Promote(source, plan, new PromoteConceptRequest());

        Assert.Equal(2, first.Created.Count);
        Assert.Equal(
            first.Created.Select(item => item.JobId),
            repeated.Created.Select(item => item.JobId));
        foreach (var promoted in first.Created)
        {
            var created = scanner.FindJob(promoted.JobId, taskStore);
            Assert.NotNull(created);
            Assert.Equal(TaskModes.Coding, created!.Mode);
            Assert.Equal(TaskStates.Preparation, created.State);
            Assert.Equal(TaskAcceptanceDeliveryModes.BoundedSlice,
                created.AcceptanceScope!.DeliveryMode);
            Assert.Equal(created.Title, created.AcceptanceScope.Slice);
            Assert.Contains(
                "docs/delivery-flow/index.html",
                File.ReadAllText(Path.Combine(created.FolderPath, "prompt.md")));
            Assert.Contains("Delivery mode [delivery-mode]: A: queued (recommended) [queued] (recommended working assumption)",
                File.ReadAllText(Path.Combine(created.FolderPath, "prompt.md")));
            Assert.Equal("docs/delivery-flow/index.html", created.Dossier?.Path);
        }
        var apiCard = scanner.FindJob(first.Created[0].JobId, taskStore);
        Assert.Equal("§4", apiCard?.Dossier?.Section);
        Assert.Equal("delivery-mode", apiCard?.Dossier?.Decision);
        Assert.Equal(0, apiCard?.Dossier?.ImplementationIndex);
    }

    [Fact]
    public void Review_RejectsSliceDependenciesThatAreUnknownForwardOrDuplicated()
    {
        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root, "slices", "Slices", "Three slices.", "AGT-2358");
        WriteImplementationTasks(directory,
        [
            Slice("slice-1", "Build store"),
            Slice("slice-2", "Build API", "slice-3"),
            Slice("slice-3", "Build UI", "slice-9"),
            Slice("slice-1", "Build docs", "slice-1"),
        ]);

        var review = ConceptWorkbenchContract.ReviewDirectory(_root, "docs/slices", "AGT-2358");

        Assert.False(review.IsComplete);
        Assert.Contains("implementationTasks[1].dependsOn names the later slice 'slice-3'. List every slice after the slices it depends on.", review.Findings);
        Assert.Contains("implementationTasks[2].dependsOn names unknown slice id 'slice-9'.", review.Findings);
        Assert.Contains("implementationTasks[3] id 'slice-1' is already used by implementationTasks[0].", review.Findings);
    }

    [Fact]
    public void Review_AcceptsSlicesThatDependOnEarlierSlices()
    {
        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root, "ordered-slices", "Ordered slices", "Three slices.", "AGT-2358");
        WriteImplementationTasks(directory,
        [
            Slice("slice-1", "Build store"),
            Slice("slice-2", "Build API", "slice-1"),
            Slice("slice-3", "Build UI", "SLICE-1", "slice-2"),
        ]);

        var review = ConceptWorkbenchContract.ReviewDirectory(_root, "docs/ordered-slices", "AGT-2358");

        Assert.True(review.IsComplete, review.Summary);
        // Slice ids match case-insensitively; the descriptor keeps them as written.
        Assert.Equal(["SLICE-1", "slice-2"], review.Descriptor!.ImplementationTasks[2].DependsOn);
    }

    [Fact]
    public void Promotion_CreatesDependsOnEdgesBetweenSlicesAndPromotesPredecessorsFirst()
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);
        var (_, scanner, _, mutations) = BuildServices(repository, taskStore);
        var source = CreateConceptSource(scanner, mutations, taskStore);
        var directory = ConceptWorkbenchContract.CreateScaffold(
            repository, "sliced-flow", "Sliced flow", "Three dependent slices.", source.Key ?? source.Id);
        WriteImplementationTasks(directory,
        [
            Slice("slice-1", "Build store"),
            Slice("slice-2", "Build API", "slice-1"),
            Slice("slice-3", "Build UI", "slice-1", "slice-2"),
        ]);
        PublishDossier(source, "docs/sliced-flow");
        var plan = scanner.BuildPromoteConceptPlan(source.Id, taskStore);
        Assert.NotNull(plan);
        var service = new ConceptPromotionService(
            scanner, mutations, NullLogger<ConceptPromotionService>.Instance);

        // Selecting only the last slice still creates its predecessors, first.
        var result = service.Promote(source, plan!, new PromoteConceptRequest { ItemIndexes = [2] });

        Assert.Equal(["Build store", "Build API", "Build UI"], result.Created.Select(card => card.Title));
        var cards = result.Created.Select(card => scanner.FindJob(card.JobId, taskStore)!).ToList();
        var keys = cards.Select(card => card.Key ?? card.Id).ToList();
        Assert.Empty(cards[0].References.DependsOn);
        Assert.Equal([keys[0]], cards[1].References.DependsOn.Select(edge => edge.Key));
        Assert.Equal([keys[0], keys[1]], cards[2].References.DependsOn.Select(edge => edge.Key));
        Assert.All(cards.SelectMany(card => card.References.DependsOn), edge =>
        {
            Assert.False(edge.RequiresCompletion);
            Assert.False(edge.ReleaseGate);
        });
        Assert.All(cards, card =>
        {
            if (source.Key is { } sourceKey) Assert.Equal([sourceKey], card.References.RelatedTo);
            Assert.DoesNotContain(card.References.DependsOn, edge => edge.Key == (source.Key ?? source.Id));
            Assert.Equal(3, card.Dossier?.ImplementationCount);
        });
        Assert.Equal([0, 1, 2], cards.Select(card => card.Dossier?.ImplementationIndex ?? -1));
        Assert.True(cards[0].Order < cards[1].Order && cards[1].Order < cards[2].Order);

        // A repeated request reuses the ledger and creates no second set.
        var repeated = service.Promote(source, plan!, new PromoteConceptRequest());
        Assert.Equal(result.Created.Select(card => card.JobId), repeated.Created.Select(card => card.JobId));
    }

    [Fact]
    public void Promotion_LinksALaterSliceToThePredecessorCardPromotedEarlier()
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);
        var (_, scanner, _, mutations) = BuildServices(repository, taskStore);
        var source = CreateConceptSource(scanner, mutations, taskStore);
        var directory = ConceptWorkbenchContract.CreateScaffold(
            repository, "two-step", "Two step", "Two dependent slices.", source.Key ?? source.Id);
        WriteImplementationTasks(directory, [Slice("store", "Build store"), Slice("api", "Build API", "store")]);
        PublishDossier(source, "docs/two-step");
        var plan = scanner.BuildPromoteConceptPlan(source.Id, taskStore)!;
        var service = new ConceptPromotionService(
            scanner, mutations, NullLogger<ConceptPromotionService>.Instance);

        var first = service.Promote(source, plan, new PromoteConceptRequest { ItemIndexes = [0] });
        var second = service.Promote(source, plan, new PromoteConceptRequest { ItemIndexes = [1] });

        Assert.Single(first.Created);
        Assert.Equal(first.Created[0].JobId, second.Created[0].JobId);
        var api = scanner.FindJob(second.Created[1].JobId, taskStore)!;
        Assert.Equal([first.Created[0].TaskKey], api.References.DependsOn.Select(edge => edge.Key));
    }

    [Fact]
    public void Promotion_RecordedOperatorDecision_OverridesRecommendation()
    {
        Directory.CreateDirectory(_root);
        var html = Path.Combine(_root, "index.html");
        var descriptor = Path.Combine(_root, "workbench.json");
        File.WriteAllText(html, """
            <div data-decision-id="delivery-mode" data-decision-kind="single" data-decision-label="Delivery mode">
              <label data-option-id="queued" data-option-label="A: queued (recommended)"><input checked></label>
              <label data-option-id="direct" data-option-label="B: direct"><input></label>
            </div>
            """);
        File.WriteAllText(descriptor, """{"decision":{"responses":[{"decisionId":"delivery-mode","selectedOptionIds":["direct"]}]}}""");

        var decisions = ConceptDecisionAssumptions.Read(html, descriptor);
        Assert.Equal(2, decisions.Count);
        Assert.False(decisions[0].IsWorkingAssumption);
        Assert.Equal("direct", decisions[1].OptionId);
        Assert.True(decisions[1].OperatorSelected);
        Assert.True(decisions[1].IsWorkingAssumption);
        var prompt = ConceptPromotionService.BuildPrompt(
            new ConceptSourceDocument
            {
                RepoRelativePath = "docs/delivery-flow/index.html",
                Decisions = decisions,
            },
            new ConceptImplementationTask
            {
                Title = "Implement delivery",
                PromptMarkdown = "Build the selected delivery path.",
            });
        Assert.Contains("[direct] (operator-selected working assumption)", prompt);
        Assert.Contains("[queued] (alternative)", prompt);
    }

    [Fact]
    public void DossierPolicy_RejectsOneOpenEndedImplementAllRecommendationsCard()
    {
        var item = new ConceptImplementationTask
        {
            Title = "Implement all Dossier recommendations",
            PromptMarkdown = "Use the approved design as the implementation source.",
        };

        var error = Assert.Throws<InvalidDataException>(
            () => DossierImplementationCardPolicy.AcceptanceScopeFor(item));

        Assert.Contains("one implementationTasks entry per slice", error.Message);
    }

    [Fact]
    public async Task CompletingSightReview_IsSuccessfulAndClearsPendingGate()
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);
        var (config, scanner, states, mutations) = BuildServices(repository, taskStore);
        var sourceId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "sight-review",
            Title = "Sight review",
            Mode = TaskModes.Concept,
            RequiresIntegration = false,
            WatchPath = taskStore,
            TargetState = TaskStates.HumanReview,
        });
        var source = scanner.FindJob(sourceId!, taskStore)!;
        Directory.CreateDirectory(TaskPaths.ResultsDir(source.FolderPath));
        File.WriteAllText(Path.Combine(TaskPaths.ResultsDir(source.FolderPath), "deliverables.md"),
            "# Approved concept\n\nThe sight review approved this no-code deliverable.\n");
        CompletionContentFixture.RecordPassedReview(source.FolderPath, "concept-fit");
        var pipelineLog = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        pipelineLog.Begin(
            source.FolderPath,
            PipelineCatalogue.Concept,
            source.ProjectName,
            source.Id);
        var now = DateTime.UtcNow;
        pipelineLog.RecordStep(source.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptSightReviewGateStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Running,
            StartedAt = now,
            Verdict = "awaiting-sight-review",
        });
        SteerPendingMarker.Write(source.FolderPath, new SteerPendingRecord
        {
            Kind = SteerPendingKinds.ConceptSightReview,
            WaitStartedAt = now,
            Question = "Approve the concept.",
        });
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            new GitService(NullLogger<GitService>.Instance, scanner, config, prompts),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config),
            NullLogger<TaskTransitionService>.Instance,
            pipelineLog: pipelineLog);

        var outcome = await transitions.MoveAsync(
            source.Id, TaskStates.Completed, taskStore);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var completed = scanner.FindJob(source.Id, taskStore)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.False(SteerPendingMarker.Exists(completed.FolderPath));
        var execution = JsonSerializer.Deserialize<PipelineExecutionRecord>(
            File.ReadAllText(Path.Combine(completed.FolderPath, PipelineExecutionLog.FileName)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(execution.IsComplete);
        Assert.Equal(
            PipelineStepStatus.Passed,
            execution.Steps.Single(step =>
                step.StepId == PipelineCatalogue.ConceptSightReviewGateStepId).Status);
        Assert.Equal(
            PipelineStepStatus.Skipped,
            execution.Steps.Single(step =>
                step.StepId == PipelineCatalogue.ConceptPromotionStepId).Status);
    }

    private static ConceptImplementationTask Slice(string id, string title, params string[] dependsOn) => new()
    {
        Id = id,
        Title = title,
        PromptMarkdown = $"Deliver the {title.ToLowerInvariant()} slice.",
        DependsOn = [.. dependsOn],
    };

    private static void WriteImplementationTasks(string directory, List<ConceptImplementationTask> items)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var path = Path.Combine(directory, "workbench.json");
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(File.ReadAllText(path), options)!;
        File.WriteAllText(path, JsonSerializer.Serialize(descriptor with { ImplementationTasks = items }, options));
    }

    private static TaskInfo CreateConceptSource(
        TaskScannerService scanner, TaskMutationService mutations, string taskStore)
    {
        var sourceId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "concept-source",
            Title = "Concept source",
            Mode = TaskModes.Concept,
            RequiresIntegration = false,
            WatchPath = taskStore,
            TargetState = TaskStates.HumanReview,
        });
        Assert.NotNull(sourceId);
        return scanner.FindJob(sourceId!, taskStore)!;
    }

    private static void PublishDossier(TaskInfo source, string directory)
    {
        Assert.True(ConceptWorkbenchStore.Write(source.FolderPath, new ConceptWorkbenchRecord
        {
            RepoRelativeDirectory = directory,
            RepoRelativeEntrypoint = directory + "/index.html",
            Title = directory,
            // clock-independent: the publication time is fixture data; promotion never compares it with now.
            PublishedAt = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
        }));
    }

    private (
        IConfiguration Config,
        TaskScannerService Scanner,
        TaskStateMachine States,
        TaskMutationService Mutations) BuildServices(
        string repository,
        string taskStore)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "Concept project",
                ["WatchPaths:0:Path"] = taskStore,
                ["WatchPaths:0:RootPath"] = repository,
                ["WatchPaths:0:RepositoryPath"] = repository,
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        states.EnsureStateFoldersAndMigrate();
        var clients = new ClientIdentityStore(
            config, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();
        var mutations = new TaskMutationService(
            scanner,
            clients,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        return (config, scanner, states, mutations);
    }
}
