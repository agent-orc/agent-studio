using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3696: the concept post-steps around a Remote delivery's task-branch
/// merge. Dossier placement refuses an uncontained delivery before anything
/// is merged; an integrated Dossier gets its placement record, concept review
/// and sight-review marker; promotion reads the integrated commit when the
/// developer checkout does not carry the Dossier.
/// </summary>
public sealed class ConceptDeliveryPostStepsTests : IDisposable
{
    private const string Topic = "delivery-contract";
    private const string Entrypoint = "docs/" + Topic + "/index.html";
    private const string CardKey = "AGT-9001";
    private const string CardId = "concept-delivery";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "concept-post-steps-" + Guid.NewGuid().ToString("N"));
    private readonly string _repository;
    private readonly string _taskStore;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly PipelineExecutionLog _pipeline;
    private readonly ConceptDeliveryPostSteps _steps;

    public ConceptDeliveryPostStepsTests()
    {
        _repository = Path.Combine(_root, "repository");
        _taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(_repository);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_taskStore, state));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:Path"] = _taskStore,
            ["WatchPaths:0:RootPath"] = _repository,
            ["WatchPaths:0:RepositoryPath"] = _repository,
        }).Build();
        _scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        _git = new GitService(NullLogger<GitService>.Instance, _scanner, config);
        _pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        _steps = new ConceptDeliveryPostSteps(
            _git,
            _scanner,
            _pipeline,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            NullLogger<ConceptDeliveryPostSteps>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "ConceptDeliveryPostStepsTests cleanup is best-effort."); }
    }

    [Fact]
    public async Task Contained_dossier_is_merged_then_recorded_reviewed_and_parked_for_sight_review()
    {
        var resultSha = SeedDelivery(touchProductCode: false);
        var card = SeedCard();
        var merges = 0;

        var result = await _steps.IntegrateAsync(card, Request(resultSha), () =>
        {
            merges++;
            return Task.FromResult(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, MergeIntoDevelop(resultSha)));
        });

        Assert.Equal(1, merges);
        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.Null(result.PostStepParkReason);
        Assert.Null(RemoteDeliveryParkReason.For(result));
        var record = ConceptWorkbenchStore.Read(card.FolderPath);
        Assert.NotNull(record);
        Assert.Equal("docs/" + Topic, record!.RepoRelativeDirectory);
        Assert.Equal(Entrypoint, record.RepoRelativeEntrypoint);
        Assert.Equal(result.MergedSha, record.CommitSha);
        var steps = _pipeline.Read(card.FolderPath)!.Steps.ToDictionary(step => step.StepId);
        Assert.Equal(PipelineStepStatus.Passed, steps[PipelineCatalogue.ConceptWorkbenchPlacementStepId].Status);
        Assert.Equal(PipelineStepStatus.Passed, steps[PipelineCatalogue.ConceptReviewStepId].Status);
        Assert.Equal(PipelineStepStatus.Running, steps[PipelineCatalogue.ConceptSightReviewGateStepId].Status);
        Assert.Equal(SteerPendingKinds.ConceptSightReview, SteerPendingMarker.TryRead(card.FolderPath)?.Kind);
    }

    [Fact]
    public async Task Delivery_that_changes_product_code_is_refused_before_the_merge()
    {
        var resultSha = SeedDelivery(touchProductCode: true);
        var card = SeedCard();
        var merges = 0;

        var result = await _steps.IntegrateAsync(card, Request(resultSha), () =>
        {
            merges++;
            return Task.FromResult(MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged));
        });

        Assert.Equal(0, merges);
        Assert.Equal(MergeIntoIntegrationOutcome.Error, result.Outcome);
        Assert.Equal(AcceptedIntegrationFailureCodes.ConceptDossierRejected, result.FailureCode);
        var reason = RemoteDeliveryParkReason.For(result);
        Assert.StartsWith(ConceptDeliveryPostSteps.PlacementParkPrefix, reason);
        Assert.Contains("src/product.cs", reason);
        Assert.Null(ConceptWorkbenchStore.Read(card.FolderPath));
        Assert.Null(SteerPendingMarker.TryRead(card.FolderPath));
        var placement = _pipeline.Read(card.FolderPath)!.Steps
            .Single(step => step.StepId == PipelineCatalogue.ConceptWorkbenchPlacementStepId);
        Assert.Equal(PipelineStepStatus.Failed, placement.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.ConceptDossierRejected, placement.FailureCode);
        var merge = _pipeline.Read(card.FolderPath)!.Steps
            .Single(step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            merge.Status, merge.Verdict, merge.Reason, merge.VerdictSummary, merge.FailureCode)!;
        Assert.Equal(AcceptedIntegrationFailureCodes.ConceptDossierRejected, failure.Code);
        Assert.Equal("Dossier placement failed", failure.Label);
        Assert.StartsWith(ConceptDeliveryPostSteps.PlacementParkPrefix, failure.Reason);
    }

    [Fact]
    public async Task Dossier_not_named_by_the_card_results_is_refused_with_the_missing_reference()
    {
        var resultSha = SeedDelivery(touchProductCode: false);
        var card = SeedCard(writeReferences: false);

        var result = await _steps.IntegrateAsync(card, Request(resultSha),
            () => throw new InvalidOperationException("A refused delivery must not merge."));

        Assert.Contains("results/deliverables.md must name the dossier path.", result.PostStepParkReason);
    }

    [Fact]
    public async Task Replayed_integration_of_an_already_merged_dossier_is_not_refused()
    {
        var resultSha = SeedDelivery(touchProductCode: false);
        var baseSha = Git("rev-parse", "main");
        var mergedSha = MergeIntoDevelop(resultSha);
        var card = SeedCard();
        ReviewSubjectStore.Write(card.FolderPath, new ReviewSubjectRecord
        {
            TaskKey = CardKey, RunAttemptId = "run-1", AttemptChainId = "chain-1",
            Project = "Project", Repository = "repo", ResultSha = resultSha, BaseSha = baseSha,
            CompletedAtUtc = Now,
        });

        var result = await _steps.IntegrateAsync(card, Request(resultSha), () => Task.FromResult(
            MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.AlreadyMerged) with { ContainmentSha = mergedSha }));

        Assert.Null(result.PostStepParkReason);
        Assert.Equal(mergedSha, ConceptWorkbenchStore.Read(card.FolderPath)?.CommitSha);
    }

    [Fact]
    public async Task Failed_merge_is_returned_unchanged_without_placement_record()
    {
        var resultSha = SeedDelivery(touchProductCode: false);
        var card = SeedCard();
        var conflict = MergeIntoIntegrationResult.Conflicted(["docs/delivery-contract/index.html"], "conflict");

        var result = await _steps.IntegrateAsync(card, Request(resultSha), () => Task.FromResult(conflict));

        Assert.Same(conflict, result);
        Assert.Null(ConceptWorkbenchStore.Read(card.FolderPath));
    }

    [Fact]
    public async Task Promotion_reads_the_placed_copy_when_the_checkout_lacks_the_dossier()
    {
        var resultSha = SeedDelivery(touchProductCode: false);
        var card = SeedCard();
        await _steps.IntegrateAsync(card, Request(resultSha), () => Task.FromResult(
            MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, MergeIntoDevelop(resultSha))));
        Assert.False(File.Exists(Path.Combine(_repository, Entrypoint)));

        var plan = _scanner.BuildPromoteConceptPlan(CardId, _taskStore);

        Assert.NotNull(plan);
        Assert.Equal(Entrypoint, plan!.Source.RepoRelativePath);
        Assert.Single(plan.Items);
        File.Delete(Path.Combine(ConceptWorkbenchStore.SnapshotRoot(card.FolderPath), "docs", Topic, "workbench.json"));
        Assert.Null(_scanner.BuildPromoteConceptPlan(CardId, _taskStore));
    }

    [Fact]
    public void Concept_card_selection_ignores_coding_cards()
    {
        SeedCard();
        Assert.NotNull(_steps.ConceptCard(Request(new string('a', 40))));
        Assert.Null(_steps.ConceptCard(Request(new string('a', 40)) with { JobId = "missing" }));
    }

    private RemoteDeliveryIntegrationRequest Request(string resultSha) => new(
        "Project", CardId, Path.Combine(_taskStore, TaskStates.AutoReview, CardId), _taskStore,
        "develop", "merge", PipelineTypes.Task, Now, ReviewedResultSha: resultSha);

    /// <summary>
    /// main stays checked out; develop is the integration line and the
    /// delivery branch carries the Dossier commit.
    /// </summary>
    private string SeedDelivery(bool touchProductCode)
    {
        Git("init", "--initial-branch=main");
        Git("config", "user.name", "Concept Test");
        Git("config", "user.email", "concept@example.invalid");
        File.WriteAllText(Path.Combine(_repository, "README.md"), "seed\n");
        Git("add", "--all");
        Git("commit", "-m", "seed");
        Git("branch", "develop");
        Git("checkout", "-b", "task/concept-delivery");
        var dossier = ConceptWorkbenchContract.CreateScaffold(
            _repository, Topic, "Delivery contract", "How a concept is delivered.", CardKey);
        AddImplementationTask(Path.Combine(dossier, ConceptWorkbenchContract.DescriptorFileName));
        if (touchProductCode)
        {
            Directory.CreateDirectory(Path.Combine(_repository, "src"));
            File.WriteAllText(Path.Combine(_repository, "src", "product.cs"), "// product\n");
        }
        Git("add", "--all");
        Git("commit", "-m", "concept dossier");
        var sha = Git("rev-parse", "HEAD");
        Git("checkout", "main");
        return sha;
    }

    /// <summary>A merge commit on develop that leaves the main checkout untouched.</summary>
    private string MergeIntoDevelop(string resultSha)
    {
        var merge = Git("commit-tree", resultSha + "^{tree}", "-p", "develop", "-p", resultSha, "-m", "merge concept");
        Git("update-ref", "refs/heads/develop", merge);
        return merge;
    }

    private TaskInfo SeedCard(bool writeReferences = true)
    {
        var folder = Path.Combine(_taskStore, TaskStates.AutoReview, CardId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = CardId,
            ["key"] = CardKey,
            ["title"] = "Concept delivery",
            ["state"] = TaskStates.AutoReview,
            ["mode"] = TaskModes.Concept,
            ["order"] = 1,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Write the delivery Dossier.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        if (writeReferences) AgentStudio.Tasks.ConceptDossierContract.WriteReference(folder, Entrypoint);
        _scanner.InvalidateCache();
        return _scanner.FindJob(CardId, _taskStore)!;
    }

    private static void AddImplementationTask(string descriptorPath)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(File.ReadAllText(descriptorPath), options)!;
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor with
        {
            ImplementationTasks =
            [
                new ConceptImplementationTask
                {
                    Title = "Implement the delivery contract",
                    PromptMarkdown = "Add the approved delivery contract endpoint.",
                },
            ],
        }, options));
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }
}
