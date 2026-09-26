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
        }
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
        var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
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
