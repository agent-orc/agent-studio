using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PromptEnrichmentServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "prompt-enrichment-" + Guid.NewGuid().ToString("N"));

    public PromptEnrichmentServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public void Prepare_RealUiCard_PersistsVisibleReportAndLedgerAttribution()
    {
        var folder = Path.Combine(_root, TaskStates.Ready, "ui-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "ui-card",
            TaskKey = "AGT-TEST",
            ProjectName = "test",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Polish the Angular task detail styling",
            Tags = ["frontend", "ui"],
        };
        var authored = "# Original task\n\nKeep this text byte-for-byte readable.";
        File.WriteAllText(Path.Combine(folder, "task.json"),
            """{"id":"ui-card","title":"Polish UI","state":"2-ready"}""");
        File.WriteAllText(Path.Combine(folder, "prompt.md"), authored);
        var guide = new ProjectStyleGuide(
            "frontend-styling",
            "Frontend styling context",
            "quality/frontend-styling.md",
            "Styling rules",
            "Use semantic tokens, calm surfaces, and no coloured left accent bars.",
            "7",
            new StyleGuideAppliesTo(["*"], ["angular"], ["frontend"]));
        var repository = Path.Combine(_root, "repository");
        WriteSource(repository, "AGENTS.md");
        WriteSource(repository, "docs/quality/frontend-styling.md");
        var pipeline = new PipelineExecutionLog(
            NullLogger<PipelineExecutionLog>.Instance);
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance,
            pipelineLog: pipeline);

        var result = service.Prepare(
            task,
            authored,
            downstreamModel: null,
            enabledOverride: true,
            guidesOverride: [guide],
            styleGuideSnapshotOverride: "style-snapshot-7",
            repositoryRootOverride: repository);

        Assert.StartsWith(authored, result.LaunchPrompt, StringComparison.Ordinal);
        Assert.Contains("## Prompt enrichment", result.LaunchPrompt);
        Assert.Equal(PromptEnrichmentStatuses.Enriched, result.Report.Status);
        Assert.NotEqual(result.Report.OriginalPromptSha256, result.Report.EnrichedPromptSha256);
        Assert.Contains(result.Report.AppendedBlocks,
            block => block.Id == "style-guide:frontend-styling"
                     && block.Revision == "7"
                     && block.Project == "test"
                     && block.Repository == repository
                     && block.ExactContent.Contains("semantic tokens", StringComparison.Ordinal));
        Assert.InRange(result.Report.Tokens.Appended, 1, 1_500);
        Assert.Equal(0, result.Report.Tokens.PreprocessingInput);
        Assert.Equal(0, result.Report.Cost.SelectorUsd);

        var persisted = PromptEnrichmentService.ReadReport(folder);
        Assert.NotNull(persisted);
        Assert.Equal(result.Report.EnrichmentId, persisted!.EnrichmentId);
        Assert.True(File.Exists(Path.Combine(folder, PromptEnrichmentService.ReportFileName)));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _root,
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            summary);
        var detail = scanner.GetJobDetail("ui-card");
        Assert.NotNull(detail);
        Assert.Equal(result.Report.EnrichmentId, detail!.EnrichmentReport?.EnrichmentId);
        Assert.Equal(authored, detail.PromptMarkdown);

        var execution = pipeline.Read(folder);
        Assert.NotNull(execution);
        var step = Assert.Single(execution!.Steps,
            entry => entry.StepId == PipelineCatalogue.PromptEnrichmentStepId);
        Assert.Equal(PipelineStepStatus.Passed, step.Status);
        Assert.Equal(0, step.InputTokens);
        Assert.Contains($"+{result.Report.Tokens.Appended} attributed prompt tokens",
            step.VerdictSummary);
        Assert.Equal("PROMPT ENRICHMENT REPORT / deterministic selector",
            step.TokenUsageSource);
    }

    [Fact]
    public void Prepare_ProjectDisabled_PreservesAuthoredPromptAndAuditsDecision()
    {
        var folder = Path.Combine(_root, "disabled-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "disabled-card",
            ProjectName = "test",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Change the frontend card styling",
        };
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance);

        var result = service.Prepare(
            task,
            "Original only.",
            downstreamModel: null,
            enabledOverride: false);

        Assert.Equal("Original only.", result.LaunchPrompt);
        Assert.Equal(PromptEnrichmentStatuses.Unchanged, result.Report.Status);
        Assert.False(result.Report.Policy.ProjectEnabled);
        Assert.Empty(result.Report.AppendedBlocks);
        Assert.All(result.Report.Candidates,
            candidate => Assert.Equal("rejected-project-disabled", candidate.Decision));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(
            folder,
            IntakeRunner.EnrichedContextRelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Prepare_ForeignRepository_RejectsMissingSourcesAndUsesOnlyItsInstructions()
    {
        var repository = Path.Combine(_root, "voice-lint-repository");
        WriteSource(repository, "AGENTS.md");
        var folder = Path.Combine(_root, "voice-lint-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "VL-NEW",
            ProjectName = "Voice Lint",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Update the runner and frontend card",
        };
        var foreignGuide = new ProjectStyleGuide(
            "foreign-routing", "Foreign routing guide",
            "system/domains/model-routing-policy.md", "Routing", "Follow this missing file.", "1",
            new StyleGuideAppliesTo(["*"], ["dotnet"], ["runner"]));
        var service = new PromptEnrichmentService(NullLogger<PromptEnrichmentService>.Instance);

        var result = service.Prepare(task, "Update runner card styling.", null,
            enabledOverride: true, guidesOverride: [foreignGuide],
            repositoryRootOverride: repository);

        Assert.Equal(["repo-instructions-source"], result.Report.AppendedBlocks.Select(block => block.Id));
        Assert.Equal("AGENTS.md", result.Report.AppendedBlocks[0].Source);
        Assert.DoesNotContain("model-routing-policy.md", result.LaunchPrompt);
        Assert.Null(result.Report.Policy.StyleGuideSnapshotId);
        Assert.Contains(result.Report.Candidates, candidate =>
            candidate.Id == "style-guide:foreign-routing"
            && candidate.Decision == "rejected-source-missing"
            && candidate.MissingPath == "docs/system/domains/model-routing-policy.md");
        Assert.Contains(result.Report.Candidates, candidate =>
            candidate.Id == "task-state-api-first"
            && candidate.Decision == "rejected-source-missing"
            && candidate.MissingPath == "docs/system/contracts/filesystem.md");
        var evidenceDirectory = Environment.GetEnvironmentVariable("PROMPT_ENRICHMENT_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Directory.CreateDirectory(evidenceDirectory);
            File.Copy(Path.Combine(folder, PromptEnrichmentService.ReportFileName),
                Path.Combine(evidenceDirectory, "VL-MISSING-SOURCE-enrichment-report.json"), overwrite: true);
        }
    }

    [Fact]
    public void Prepare_AgentStudioRepository_PreservesCatalogueBlocks()
    {
        var repository = Path.Combine(_root, "agent-studio-repository");
        var folder = Path.Combine(_root, "studio-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "AGT-NEW",
            ProjectName = "Agent Studio",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Improve the frontend runner card",
        };
        var baseline = IntakeRunner.BuildEnrichmentManifest(task, "Improve the frontend runner card.");
        foreach (var source in baseline.Constraints.SelectMany(block => block.Source.Split(';')))
        {
            var path = source.Split('#', 2)[0].Trim();
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                WriteSource(repository, path);
        }
        var service = new PromptEnrichmentService(NullLogger<PromptEnrichmentService>.Instance);

        var result = service.Prepare(task, "Improve the frontend runner card.", null,
            enabledOverride: true, repositoryRootOverride: repository,
            agentStudioCatalogueOverride: true);

        Assert.Equal(baseline.Constraints.Select(block => block.Id),
            result.Report.AppendedBlocks.Select(block => block.Id));
        Assert.Equal(baseline.Constraints.Select(IntakeRunner.RenderConstraintMarkdown),
            result.Report.AppendedBlocks.Select(block => block.ExactContent + "\n"));
        Assert.All(result.Report.AppendedBlocks, block =>
        {
            Assert.Equal("Agent Studio", block.Project);
            Assert.Equal(repository, block.Repository);
        });
    }

    [Fact]
    public void Selector_ProjectPipelineCanExplicitlyAdoptABuiltInBlock()
    {
        var repository = Path.Combine(_root, "explicit-repository");
        WriteSource(repository, "AGENTS.md");
        WriteSource(repository, "docs/system/contracts/filesystem.md");
        var task = new TaskInfo
        {
            Id = "EXPLICIT-1", ProjectName = "Custom", Mode = TaskModes.Coding,
            Title = "Change runner task state handling"
        };

        var manifest = IntakeRunner.BuildEnrichmentManifest(
            task, "Change the runner task state handling.", repositoryRoot: repository,
            agentStudioCatalogue: false,
            explicitlyDeclaredBlockIds: ["task-state-api-first"]);

        Assert.Contains(manifest.Constraints, block =>
            block.Id == "task-state-api-first"
            && block.SourceVerification == "pipeline-explicit");

        File.Delete(Path.Combine(repository, "docs", "system", "contracts", "filesystem.md"));
        var missing = IntakeRunner.BuildEnrichmentManifest(
            task, "Change the runner task state handling.", repositoryRoot: repository,
            agentStudioCatalogue: false,
            explicitlyDeclaredBlockIds: ["task-state-api-first"]);
        Assert.DoesNotContain(missing.Constraints, block => block.Id == "task-state-api-first");
        Assert.Contains(missing.SourceRejections, rejection =>
            rejection.Id == "task-state-api-first"
            && rejection.MissingPath == "docs/system/contracts/filesystem.md");
    }

    [Fact]
    public void Prepare_ExplicitPipelineBlock_RecordsProjectDeclaration()
    {
        var repository = Path.Combine(_root, "configured-repository");
        var folder = Path.Combine(_root, "configured-card");
        Directory.CreateDirectory(folder);
        WriteSource(repository, "AGENTS.md");
        WriteSource(repository, "docs/system/contracts/filesystem.md");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetPipelineStep("Custom", PipelineTypes.Task,
            PipelineCatalogue.PromptEnrichmentStepId,
            new PipelineStepSetting { EnrichmentBlockIds = ["task-state-api-first"] });
        var task = new TaskInfo
        {
            Id = "CUSTOM-1", ProjectName = "Custom", FolderPath = folder,
            State = TaskStates.Ready, Mode = TaskModes.Coding,
            Title = "Change runner task state handling"
        };
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance, projectSettings: settings);

        var result = service.Prepare(task, "Change runner task state handling.", null,
            repositoryRootOverride: repository);

        Assert.Contains(result.Report.AppendedBlocks, block =>
            block.Id == "task-state-api-first"
            && block.SourceVerification == "pipeline-explicit"
            && block.Project == "Custom"
            && block.Repository == repository);
    }

    [Fact]
    public void Selector_ForeignRepositoryWithSamePaths_DoesNotInheritStudioCatalogue()
    {
        var repository = Path.Combine(_root, "lookalike-repository");
        WriteSource(repository, "AGENTS.md");
        WriteSource(repository, "docs/start/README.md");
        WriteSource(repository, "docs/system/contracts/filesystem.md");
        var task = new TaskInfo
        {
            Id = "LOOKALIKE-1", ProjectName = "Other Project", Mode = TaskModes.Coding,
            Title = "Change runner task state handling"
        };

        var manifest = IntakeRunner.BuildEnrichmentManifest(
            task, "Change runner task state handling.", repositoryRoot: repository,
            agentStudioCatalogue: false);

        Assert.Equal(["repo-instructions-source"], manifest.Constraints.Select(block => block.Id));
        Assert.Contains(manifest.SourceRejections, rejection =>
            rejection.Id == "task-state-api-first"
            && rejection.Reason == "project-catalogue-mismatch");
    }

    [Theory]
    [InlineData("Voice Lint", "VL-POST-FIX")]
    [InlineData("Token Economy", "TE-POST-FIX")]
    [InlineData("Quality Studio", "QS-POST-FIX")]
    public void Prepare_NewCardInOtherProject_AppendsOnlySourcesInThatRepository(
        string project, string cardId)
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("PROMPT_ENRICHMENT_EVIDENCE_DIR");
        var repository = Path.Combine(
            string.IsNullOrWhiteSpace(evidenceDirectory) ? _root : Path.Combine(evidenceDirectory, "source-fixtures"),
            project.Replace(' ', '-'));
        var folder = Path.Combine(_root, cardId);
        Directory.CreateDirectory(folder);
        WriteSource(repository, "AGENTS.md");
        if (project == "Quality Studio")
            WriteSource(repository, "docs/quality/project-guide.md");
        var guides = project == "Quality Studio"
            ? new List<ProjectStyleGuide>
            {
                new("project-guide", "Own guide", "quality/project-guide.md", "Own rules",
                    "Follow this project's own guide.", "1",
                    new StyleGuideAppliesTo(["*"], ["dotnet"], ["runner"]))
            }
            : [];
        var task = new TaskInfo
        {
            Id = cardId, ProjectName = project, FolderPath = folder,
            State = TaskStates.Ready, Mode = TaskModes.Coding,
            Title = "Update runner card UI"
        };
        var service = new PromptEnrichmentService(NullLogger<PromptEnrichmentService>.Instance);

        var result = service.Prepare(task, "Update the runner card UI.", null,
            enabledOverride: true, guidesOverride: guides,
            repositoryRootOverride: repository);

        Assert.All(result.Report.AppendedBlocks, block =>
        {
            Assert.Equal(project, block.Project);
            Assert.Equal(repository, block.Repository);
            Assert.Equal("repository", block.SourceVerification);
            foreach (var citation in block.Source.Split(';'))
            {
                var path = citation.Split('#', 2)[0].Trim();
                Assert.True(File.Exists(Path.Combine(repository, path)), path);
            }
        });
        Assert.DoesNotContain(result.Report.AppendedBlocks,
            block => block.Id is "task-state-api-first" or "frontend-design-tokens-components");
        Assert.NotNull(PromptEnrichmentService.ReadReport(folder));
        if (!string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Directory.CreateDirectory(evidenceDirectory);
            File.Copy(Path.Combine(folder, PromptEnrichmentService.ReportFileName),
                Path.Combine(evidenceDirectory, $"{cardId}-enrichment-report.json"), overwrite: true);
        }
    }

    [Fact]
    public void ComposeModeFraming_PreservesModeContractBeforeEnrichment()
    {
        const string modeContract = "**Read-only run.** Preserve the report-only contract.";
        const string enrichment = "## Prompt enrichment\n\n- Apply the selected project context.";

        var composed = PromptEnrichmentService.ComposeModeFraming(modeContract, enrichment);

        Assert.StartsWith(modeContract, composed, StringComparison.Ordinal);
        Assert.True(
            composed.IndexOf(enrichment, StringComparison.Ordinal)
            > composed.IndexOf(modeContract, StringComparison.Ordinal));
        Assert.Equal(1, Count(composed, modeContract));
        Assert.Equal(1, Count(composed, enrichment));
        Assert.EndsWith("\n\n", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_SelectorFailure_PersistsFallbackBeforeUsingAuthoredPrompt()
    {
        var folder = Path.Combine(_root, "fallback-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "fallback-card",
            ProjectName = "test",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Style the frontend card",
        };
        var invalidGuide = new ProjectStyleGuide(
            "invalid",
            "Invalid guide",
            "quality/invalid.md",
            "Invalid",
            "This guide deliberately exercises selector fallback.",
            "1",
            null!);
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance);

        var result = service.Prepare(
            task,
            "Authored prompt survives.",
            downstreamModel: null,
            enabledOverride: true,
            guidesOverride: [invalidGuide]);

        Assert.Equal("Authored prompt survives.", result.LaunchPrompt);
        Assert.Equal(PromptEnrichmentStatuses.FallbackUnenriched, result.Report.Status);
        Assert.True(result.Report.Policy.ProjectEnabled);
        Assert.Empty(result.Report.AppendedBlocks);
        Assert.Contains(result.Report.Warnings,
            warning => warning.StartsWith("Selection failed open", StringComparison.Ordinal));
        Assert.Equal(
            PromptEnrichmentStatuses.FallbackUnenriched,
            PromptEnrichmentService.ReadReport(folder)?.Status);
    }

    [Fact]
    public void Prepare_ReportCannotBePersisted_BlocksDispatch()
    {
        var fileInsteadOfFolder = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(fileInsteadOfFolder, "occupied");
        var task = new TaskInfo
        {
            Id = "blocked-card",
            ProjectName = "test",
            FolderPath = fileInsteadOfFolder,
            State = TaskStates.Ready,
            Title = "Blocked persistence",
        };
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance);

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.Prepare(
                task,
                "A valid authored prompt.",
                downstreamModel: null,
                enabledOverride: true));

        Assert.Contains("Dispatch is blocked", error.Message);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static void WriteSource(string repository, string path)
    {
        var full = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "# Test source\n");
    }
}
