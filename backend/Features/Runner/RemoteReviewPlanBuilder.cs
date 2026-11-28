using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Prompts;
using AgentStudio.Git;
using AgentStudio.Cli;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using Microsoft.Extensions.Logging;
using Contract = AgentStudio.TaskServer.Contracts;
using AgentStudio.Review;
using AgentStudio.Tasks;
using AgentStudio.Security;

namespace AgentStudio.Runner;

/// <summary>
/// Freezes deterministic tool gates and read-only semantic aspect calls into the
/// ReviewSubject before a remote executor claims it. The Task Server still owns
/// admission and the final lane decision. The runner receives only immutable
/// commands for the exact result SHA covered by the ReviewAttempt lease.
/// </summary>
public sealed partial class RemoteReviewPlanBuilder
{
    /// <summary>Applies a recorded optional-check disposition without changing mandatory proof or the result identity.</summary>
    internal static Contract.ReviewPlanDto WithoutUnavailableOptionalCommands(Contract.ReviewPlanDto plan, string reason)
        => plan with
        {
            Commands = plan.Commands.Where(command => command.Required).ToArray(),
            RequiredAspects = plan.RequiredAspects.Where(aspect =>
                !plan.Commands.Any(command => command.Aspect == aspect && !command.Required)
                || plan.Commands.Any(command => command.Aspect == aspect && command.Required)).ToArray(),
            SkippedAspects = [.. plan.SkippedAspects ?? [],
                .. plan.Commands.Where(command => !command.Required)
                    .Select(command => new Contract.ReviewSkippedAspectDto(command.Aspect, reason))],
        };

    private static readonly string[] DefaultAspectIds =
    [
        "requirement-fit",
        "code-quality",
        "documentation-impact",
        "tests-and-evidence",
        "visual-review",
    ];

    private static readonly AspectDefinition ConceptFitDefinition = new(
        Id: "concept-fit",
        ConcernNamespace: "requirement",
        PromptTemplate: "review-aspect-concept-fit.md",
        Title: "Concept fit",
        FallbackSystem: "Compare the delivered Dossier with the current prompt.md. Block a missing required section, contradicted operator direction, open decision without a recommendation, or absent implementation cards. Cite the exact brief direction and Dossier location.");

    private static readonly AspectDefinition DeepReviewDefinition = new(
        Id: "deep-review",
        ConcernNamespace: "review",
        PromptTemplate: "review-aspect-deep-review.md",
        Title: "Deep review",
        FallbackSystem: "Review the delivered change for concrete defects and cite the exact missing contract.");

    // The deterministic tool aspects a no-change result skips (AGT-3079).
    private static readonly string[] NoChangeSkippedToolAspects =
    [
        "build-tests",
        "lint",
        "review-verification",
    ];

    private readonly AspectRunnerService _aspects;
    private readonly IConfiguration _configuration;
    private readonly GitService? _git;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoteReviewPlanBuilder>? _logger;
    private readonly DecisionRuleVersionStore? _rules;
    private readonly ProjectRegistry? _projects;
    private readonly QuotaAdmissionService? _quota;
    private readonly ModelRoutingPolicyRegistry? _routing;
    private readonly ReviewObservationStore? _observations;
    private readonly PostMergeModeService? _postMergeMode;
    private readonly AgentStudio.Projects.ProjectRepositoryVisibility? _visibility;

    public RemoteReviewPlanBuilder(
        AspectRunnerService aspects,
        IConfiguration configuration,
        GitService? git = null,
        TimeProvider? time = null,
        ILogger<RemoteReviewPlanBuilder>? logger = null,
        DecisionRuleVersionStore? rules = null,
        ProjectRegistry? projects = null,
        QuotaAdmissionService? quota = null,
        ModelRoutingPolicyRegistry? routing = null,
        ReviewObservationStore? observations = null,
        PostMergeModeService? postMergeMode = null,
        AgentStudio.Projects.ProjectRepositoryVisibility? visibility = null)
    {
        _aspects = aspects;
        _configuration = configuration;
        _git = git;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _rules = rules;
        _projects = projects;
        _quota = quota;
        _routing = routing;
        _observations = observations;
        _postMergeMode = postMergeMode;
        _visibility = visibility;
    }

    /// <summary>
    /// AGT-3079: a valid result that changes no path against its merge base
    /// with the integration ref is already contained in the integration line,
    /// so build, test and lint would only compare that SHA with itself. Null
    /// paths mean the diff could not be computed and never count as no change.
    /// The caller must measure <paramref name="changedPaths"/> against the
    /// integration ref; an empty diff against any other base (a continuation
    /// round's pickup base) proves nothing about the integration line. See
    /// <see cref="CompletionChangedFiles"/>.
    /// </summary>
    internal static bool IsNoChangeResult(
        IReadOnlyList<string>? changedPaths,
        string? resultSha,
        string? resultRef)
        => changedPaths is { Count: 0 }
           && ReviewSubjectStore.IsValidResultSha(resultSha)
           && !string.IsNullOrWhiteSpace(resultRef);

    internal static string NoChangeSkipReason(string? integrationRef)
        => $"Result has no changed path against its merge base with {integrationRef}; "
           + "build, test and lint would compare this SHA with itself. "
           + "The integration lane verifies the contained tree before completion (AGT-3002).";

    internal static string ConceptDossierSkipReason(string dossierDirectory)
        => $"Concept delivery changes only the Dossier in {dossierDirectory}/; "
           + "the concept-fit aspect judges it, and build, test, lint and dependency "
           + "preparation of the repository do not apply (AGT-3698).";

    /// <summary>
    /// AGT-3740: the first rule that keeps build and tests in the review
    /// instead of deferring them to the batch gate, or null when the plan may
    /// defer. Each rule names itself, and the plan carries the text as
    /// <c>BuildTestDeferralDisabledReason</c>, so the card shows why a review
    /// ran its own build and tests. Before, only the test selection rules
    /// named themselves; a plan sealed while the batch gate looked disabled or
    /// the diff was unavailable gave no reason at all.
    /// </summary>
    internal static string? BatchDeferralBlocker(bool batchGateEnabled, bool postMerge,
        bool noCodingTask, string? batchScope, IReadOnlyList<string>? changedPaths,
        string? resultRef, string? resultSha, bool concept = false)
    {
        if (!batchGateEnabled) return "The project batch gate was off when the review plan was sealed.";
        if (postMerge) return "The project integrates post-merge, so no batch gate runs.";
        if (concept) return "A concept Dossier requires per-delivery placement and review before sight review.";
        if (noCodingTask) return "A report-only or card-less review has no batch gate.";
        if (changedPaths is null)
            return "The changed paths against the integration branch were unavailable when the review plan was sealed.";
        if (changedPaths.Count == 0) return "The delivery changes no path against the integration branch.";
        if (batchScope != "code" && !DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths))
            return "The batch gate scope is documentation only and this delivery changes code.";
        if (string.IsNullOrWhiteSpace(resultRef)) return "The delivery has no immutable result ref.";
        if (!ReviewSubjectStore.IsValidResultSha(resultSha)) return "The delivery has no valid result SHA.";
        return null;
    }

    private string? InventoryDeferralReason(string repositoryPath, string? integrationRef,
        string? resultSha, IReadOnlyList<string> changedPaths)
    {
        foreach (var path in changedPaths)
        {
            if (path is ".agent-studio/project.yml" or "frontend/package.json"
                or "frontend/angular.json" or "backend/OrchestratorApi.csproj")
                return $"Verify command inventory changed: {path}.";
            if (path != "backend.Tests/OrchestratorApi.Tests.csproj") continue;
            if (_git is null || string.IsNullOrWhiteSpace(integrationRef)
                || !ReviewSubjectStore.IsValidResultSha(resultSha))
                return $"Cannot classify verify command inventory change: {path}.";
            var baseSha = _git.GetMergeBase(repositoryPath, integrationRef, resultSha);
            if (baseSha is null)
                return $"Cannot classify verify command inventory change: {path}.";
            var before = _git.ReadFilesAtRevision(repositoryPath, baseSha, [path]);
            var after = _git.ReadFilesAtRevision(repositoryPath, resultSha!, [path]);
            if (before.Failure is not null || after.Failure is not null
                || !before.Files.TryGetValue(path, out var oldContent)
                || !after.Files.TryGetValue(path, out var newContent))
                return $"Cannot classify verify command inventory change: {path}.";
            if (!ReviewInventoryChangePolicy.IsContentOnlyTestProjectChange(oldContent, newContent))
                return $"Test project verify command inventory changed beyond copy content items: {path}.";
        }
        return null;
    }

    /// <param name="changedFiles">
    /// The delivery's paths against its merge base, when known. A diff that
    /// can change the Compose stack adds the Compose-render steps, whose host
    /// requirement routes the review to an executor that can render Compose
    /// (AGT-2981). Null keeps the diff-independent plan. An empty list marks a
    /// no-change result (<see cref="IsNoChangeResult"/>), so it must come from
    /// a diff against <paramref name="integrationRef"/>.
    /// </param>
    public Contract.ReviewPlanDto Build(
        TaskInfo? task,
        string? repositoryPath,
        ProjectSettings? projectSettings,
        string? integrationRef,
        string? expectedResultSha = null,
        IReadOnlyList<string>? changedFiles = null,
        string? immutableResultRef = null)
        // AGT-3077: every plan shape carries the repository visibility, so a
        // private repository is refused at claim time on any executor without
        // a read credential for it. AGT-3408: what GitHub reports through the
        // App joins the project setting; GitHub's "private" always holds.
        => WithVerifyDefinition(task, BuildPlan(task, repositoryPath, projectSettings, integrationRef,
                expectedResultSha, changedFiles, immutableResultRef) with
            {
                Quarantines = task is null ? [] : _postMergeMode?.ReadState(task.ProjectName)?.Quarantines ?? [],
                PrivateRepository = task is not null && _visibility is not null
                    ? _visibility.IsPrivate(task, projectSettings)
                    : Contract.ReviewRepositoryAccessPolicy.IsPrivate(projectSettings?.RepositoryVisibility),
                PlanCommitSha = repositoryPath is not null && integrationRef is not null
                    ? Git(repositoryPath, "rev-parse", "--verify", integrationRef + "^{commit}")
                    : null,
            }, repositoryPath, projectSettings?.BuildProfile, integrationRef,
            changedFiles, immutableResultRef ?? (task is null ? null : ReviewSubjectStore.Read(task.FolderPath)?.ImmutableResultRef));

    /// <summary>
    /// AGT-3428: the review states at its start that no check can verify this
    /// delivery, instead of passing and leaving the merge to find out. A plan
    /// that runs any build, test or lint command, a deferred batch gate, a
    /// docs-only or no-change result, and a delivery that adds the definition
    /// itself are left alone.
    /// </summary>
    private Contract.ReviewPlanDto WithVerifyDefinition(
        TaskInfo? task,
        Contract.ReviewPlanDto plan,
        string? repositoryPath,
        BuildProfile? profile,
        string? integrationRef,
        IReadOnlyList<string>? changedFiles,
        string? resultRef)
    {
        if (task is null || TaskModes.IsReportOnly(task.Mode) || TaskModes.IsConcept(task.Mode)
            || plan.BuildTestDeferredToBatch || string.IsNullOrWhiteSpace(repositoryPath)
            || plan.Commands.Any(command => command.Aspect is "build-tests" or "lint" or "review-verification"))
            return plan;
        var report = VerifyDefinitionPolicy.Inspect(repositoryPath, profile);
        if (!report.IsMissing) return plan;
        var changedPaths = changedFiles ?? (_git is null || string.IsNullOrWhiteSpace(integrationRef)
            || string.IsNullOrWhiteSpace(resultRef)
            ? null
            : _git.ChangedPathsAgainstMergeBase(repositoryPath, integrationRef, resultRef));
        if (changedPaths is { Count: 0 } || DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths)
            || changedPaths?.Any(path => string.Equals(path.Replace('\\', '/'),
                Contract.ProjectPreparationPaths.Definition, StringComparison.Ordinal)) == true)
            return plan;
        _logger?.LogWarning(
            "remote-review-plan-verify-definition-missing task={TaskKey} repository={Repository}",
            string.IsNullOrWhiteSpace(task.TaskKey) ? task.Id : task.TaskKey, repositoryPath);
        return plan with
        {
            VerifyDefinitionMissing = report.Message,
            SkippedAspects = [.. plan.SkippedAspects ?? [],
                new Contract.ReviewSkippedAspectDto("build-tests", VerifyDefinitionPolicy.Summary)],
        };
    }

    private Contract.ReviewPlanDto BuildPlan(
        TaskInfo? task,
        string? repositoryPath,
        ProjectSettings? projectSettings,
        string? integrationRef,
        string? expectedResultSha,
        IReadOnlyList<string>? changedFiles,
        string? immutableResultRef)
    {
        var subject = task is null ? null : ReviewSubjectStore.Read(task.FolderPath);
        var resultRef = immutableResultRef ?? subject?.ImmutableResultRef;
        var resultSha = expectedResultSha ?? subject?.ResultSha;
        var changedPaths = changedFiles ?? (_git is null || string.IsNullOrWhiteSpace(repositoryPath)
            || string.IsNullOrWhiteSpace(integrationRef) || string.IsNullOrWhiteSpace(resultRef)
            ? null
            : _git.ChangedPathsAgainstMergeBase(
                repositoryPath, integrationRef, resultRef));
        var toolPlan = V1ReviewPlaneEndpoints.FallbackPlan(repositoryPath,
            projectSettings?.BuildProfile, integrationRef, changedPaths) with
        {
            RequireDifferentHostFailureDomain = projectSettings?.RequireDifferentReviewHost == true,
        };
        var ordinaryToolPlan = toolPlan;
        var usesSharedSelection = toolPlan.SuiteSelection is not null;
        // AGT-3698: a concept delivery confined to one docs/<topic>/ Dossier is
        // judged by concept-fit alone. Installing and testing the repository
        // proves nothing about the Dossier and let unrelated tooling block it.
        // All concept deliveries still use the per-delivery merge so Dossier
        // placement and review run before sight review (AGT-3696).
        var conceptDossier = task is not null && TaskModes.IsConcept(task.Mode)
            ? ConceptWorkbenchContract.SingleDossierDirectory(changedPaths)
            : null;
        var deferralDisabledReason = conceptDossier is not null
            ? ConceptDossierSkipReason(conceptDossier)
            : BatchDeferralBlocker(
                projectSettings?.BatchGate.Enabled == true,
                projectSettings is not null
                && PostMergeModeService.Effective(_postMergeMode, task?.ProjectName ?? string.Empty, projectSettings)
                    == IntegrationTestModes.PostMerge,
                task is null || TaskModes.IsReportOnly(task.Mode),
                projectSettings?.BatchGate.EffectiveScope, changedPaths, resultRef, resultSha,
                concept: task is not null && TaskModes.IsConcept(task.Mode));
        var deferBuildTest = deferralDisabledReason is null;
        var noChange = IsNoChangeResult(changedPaths, resultSha, resultRef);
        string? testLevel = null;
        string? selectionDigest = null;
        if (deferBuildTest)
        {
            var options = projectSettings!.BatchGate;
            var selectionRepo = repositoryPath ?? string.Empty;
            // Selection reads the existing checkout's command inventory. A
            // test-project content item cannot change the verify commands;
            // all other inventory edits still require the per-card gate.
            deferralDisabledReason = InventoryDeferralReason(selectionRepo,
                integrationRef, resultSha, changedPaths!);
            if (deferralDisabledReason is not null)
                deferBuildTest = false;
            if (deferBuildTest)
            {
                    var verify = VerifyCommandPlanner.Plan(selectionRepo, projectSettings.BuildProfile);
                    var level = DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths)
                        || options.ReviewTestLevel == "build-only"
                            ? TestExecutionLevels.BuildOnly : TestExecutionLevels.WorkPackage;
                    // AGT-3085: the deferred plan alone selects .NET test classes
                    // by reference to the changed types; the batch gate's Full
                    // run on the combined candidate covers what that misses.
                    var selection = ProjectAffectedVerifyPlanner.TryPlan(selectionRepo, verify, changedPaths,
                        "remote-review", TestExecutionLevels.WorkPackage)
                        ?? TestSelectionPlanner.Plan(selectionRepo,
                            verify, changedPaths, projectSettings.TestExecution, "remote-review", level,
                            ReferenceSelection(selectionRepo, resultSha!));
                    if (usesSharedSelection) level = selection.Audit.Level;
                    LogReferenceSelection(task!, selection.Audit);
                    if (TestingStrategies.Resolve(projectSettings) == TestingStrategies.Fast)
                    {
                        selection = FixedReviewSelection(task!, selectionRepo, verify, selection, changedPaths, resultSha);
                        level = selection.Audit.Level;
                    }
                    var selected = selection.Commands.Where(command =>
                        command.Kind != VerifyCommandKind.Test || command.BlocksWorkPackage).ToArray();
                    if (selection.Audit.Level == TestExecutionLevels.Full
                        || selection.Audit.FullSuiteRequired
                        || selection.Audit.UnmappedSourceDirectories.Count > 0
                        || (level == TestExecutionLevels.WorkPackage
                            && !selected.Any(command => command.Kind == VerifyCommandKind.Test)))
                    {
                        deferBuildTest = false;
                        deferralDisabledReason = selection.Audit.FullSuiteRequired
                            ? "Test selection requires the full suite."
                            : selection.Audit.UnmappedSourceDirectories.Count > 0
                                ? "Test selection found source directories without a test mapping."
                                : "Test selection found no class-filtered test command for this work package.";
                    }
                    else
                    {
                        toolPlan = V1ReviewPlaneEndpoints.FallbackPlan(selectionRepo,
                            projectSettings.BuildProfile, integrationRef, changedPaths,
                            verify with { Commands = selected }) with
                        {
                            BuildTestDeferredToBatch = true,
                        };
                        toolPlan = toolPlan with
                        {
                            Commands = toolPlan.Commands.Select(command =>
                                command.Aspect == "build-tests"
                                    ? command with { Aspect = "review-verification" }
                                    : command).ToArray(),
                        };
                        testLevel = level;
                        selectionDigest = Convert.ToHexStringLower(SHA256.HashData(
                            Encoding.UTF8.GetBytes(string.Join("\n", level,
                                string.Join("\n", changedPaths ?? []),
                                string.Join("\n", selected.Select(command =>
                                    $"{command.Kind}:{command.WorkingSubdir}:{command.Command}"))))));
                    }
            }
        }
        // AGT-3310: testing.perCard: affected runs tier T1 per card. Full keeps
        // the plan above byte for byte; so does a T1 scope that falls back.
        // AGT-3965: perCard is derived from testing.strategy; only strict is full.
        var perCardApplied = false;
        if (!deferBuildTest && !noChange && conceptDossier is null
            && task is not null && !TaskModes.IsReportOnly(task.Mode)
            && projectSettings is not null
            && (usesSharedSelection || PerCardTestModes.Resolve(projectSettings) == PerCardTestModes.Affected)
            && changedPaths is { Count: > 0 } && !string.IsNullOrWhiteSpace(repositoryPath)
            && ReviewSubjectStore.IsValidResultSha(resultSha))
        {
            var verify = VerifyCommandPlanner.Plan(repositoryPath, projectSettings!.BuildProfile);
            var selection = PerCardVerifyPlanner.Plan(repositoryPath, verify, changedPaths, null,
                projectSettings.TestExecution, ReferenceSelection(repositoryPath, resultSha!));
            LogReferenceSelection(task, selection.Audit);
            _logger?.LogInformation(
                "remote-review-plan-per-card task={TaskKey} selector={Selector} full={Full} mapDigest={MapDigest} selection={Digest} reasons={Reasons}",
                string.IsNullOrWhiteSpace(task.TaskKey) ? task.Id : task.TaskKey, selection.Audit.Selector,
                selection.Audit.FullSuiteRequired, selection.Audit.ProjectMapDigest, selection.Audit.Digest,
                string.Join(" | ", selection.Audit.Reasons));
            if (!selection.Audit.FullSuiteRequired || usesSharedSelection)
            {
                if (!usesSharedSelection && TestingStrategies.Resolve(projectSettings) == TestingStrategies.Fast)
                {
                    var fullCommands = verify.Commands.Where(command => command.Kind == VerifyCommandKind.Test)
                        .Select(command => (command.Command, command.WorkingSubdir)).ToHashSet();
                    var omitted = selection.Commands.Where(command => command.Kind == VerifyCommandKind.Test
                        && fullCommands.Contains((command.Command, command.WorkingSubdir))).ToArray();
                    if (omitted.Length > 0)
                        selection = DeterministicTestScope.WithDigest(selection with
                        {
                            Commands = selection.Commands.Except(omitted).ToArray(),
                            Audit = selection.Audit with
                            {
                                SelectedCommands = selection.Audit.SelectedCommands.Except(omitted.Select(TestSelectionPlanner.Describe)).ToArray(),
                                OmittedTestCommands = selection.Audit.OmittedTestCommands.Concat(omitted.Select(TestSelectionPlanner.Describe)).Distinct().ToArray(),
                                Reasons = [.. selection.Audit.Reasons, "fast: unchanged full test commands run after integration"],
                            },
                        });
                }
                if (TestingStrategies.Resolve(projectSettings) == TestingStrategies.Fast)
                    selection = FixedReviewSelection(task, repositoryPath, verify, selection, changedPaths, resultSha);
                toolPlan = V1ReviewPlaneEndpoints.FallbackPlan(repositoryPath, projectSettings.BuildProfile,
                    integrationRef, changedPaths, verify with { Commands = selection.Commands }) with
                {
                    RequireDifferentHostFailureDomain = projectSettings.RequireDifferentReviewHost == true,
                };
                testLevel = selection.Audit.Level;
                selectionDigest = selection.Audit.Digest;
                perCardApplied = true;
            }
        }
        // Fast never turns an unmapped T1 selection into a full pre-merge suite.
        // The merge window owns that coverage; keep build and lint here.
        if (!deferBuildTest && !noChange && conceptDossier is null
            && task is not null && !TaskModes.IsReportOnly(task.Mode)
            && projectSettings is not null
            && TestingStrategies.Resolve(projectSettings) == TestingStrategies.Fast
            && !string.IsNullOrWhiteSpace(repositoryPath))
        {
            if (!perCardApplied)
            {
                var verify = VerifyCommandPlanner.Plan(repositoryPath, projectSettings.BuildProfile);
                var selection = DeterministicTestScope.WithDigest(TestSelectionPlanner.Plan(
                    repositoryPath, verify, changedPaths, projectSettings.TestExecution,
                    TaskStates.AutoReview, TestExecutionLevels.BuildOnly));
                selection = FixedReviewSelection(task, repositoryPath, verify, selection, changedPaths, resultSha);
                toolPlan = V1ReviewPlaneEndpoints.FallbackPlan(repositoryPath, projectSettings.BuildProfile,
                    integrationRef, changedPaths, verify with { Commands = selection.Commands }) with
                {
                    RequireDifferentHostFailureDomain = projectSettings.RequireDifferentReviewHost == true,
                };
                testLevel = selection.Audit.Level;
                selectionDigest = selection.Audit.Digest;
                perCardApplied = true;
            }
            ordinaryToolPlan = toolPlan;
        }
        if (task is null || TaskModes.IsReportOnly(task.Mode))
            return toolPlan;

        var settings = PipelineTypeSettings.ForTask(projectSettings, task);
        var pipeline = PipelineCatalogue.ForTask(task);
        var configured = ConfiguredAspectIds();
        var condition = new PipelineStepConditionContext
        {
            ExitCode = 0,
            TaskType = task.TaskType,
            Tags = task.Tags,
        };
        var inputs = Inputs(task, integrationRef);
        // AGT-3420: the default aspect model belongs to the review CLI in use.
        var hostDefault = ReviewAspectModelPolicy.ResolveDefault(
            _configuration[ReviewAspectModelPolicy.CliSetting] ?? PipelineStepModelDefaults.DefaultCli,
            _configuration[ReviewAspectModelPolicy.ModelSetting]);
        if (hostDefault.Fault is not null)
            _logger?.LogError(
                "remote-review-plan-model-pair-rejected task={TaskKey} setting={Setting} cli={Cli} model={Model}: {Fault}",
                string.IsNullOrWhiteSpace(task.TaskKey) ? task.Id : task.TaskKey,
                ReviewAspectModelPolicy.ModelSetting, hostDefault.CliType, hostDefault.Model, hostDefault.Fault);

        var commands = conceptDossier is null ? toolPlan.Commands.ToList() : [];
        var skipped = new List<Contract.ReviewSkippedAspectDto>();
        if (conceptDossier is not null)
        {
            skipped.AddRange(toolPlan.Commands.Select(command => command.Aspect)
                .Prepend("build-tests")
                .Where(aspect => aspect != "completion")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(aspect => new Contract.ReviewSkippedAspectDto(
                    aspect, ConceptDossierSkipReason(conceptDossier))));
            _logger?.LogInformation(
                "remote-review-plan-concept-dossier task={TaskKey} dossier={Dossier} skipped={Skipped}",
                string.IsNullOrWhiteSpace(task.TaskKey) ? task.Id : task.TaskKey, conceptDossier,
                string.Join(",", skipped.Select(item => item.Aspect)));
        }
        if (noChange)
        {
            static bool Skipped(Contract.ReviewCommandDto command)
                => NoChangeSkippedToolAspects.Contains(command.Aspect, StringComparer.OrdinalIgnoreCase);
            skipped.AddRange(commands.Where(Skipped)
                .Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(aspect => new Contract.ReviewSkippedAspectDto(
                    aspect, NoChangeSkipReason(integrationRef))));
            commands.RemoveAll(Skipped);
        }
        var isConcept = TaskModes.IsConcept(task.Mode);
        PipelineStep[] aspectSteps = isConcept
            ? [PipelineCatalogue.RemoteConceptFitStep]
            : pipeline.Post.Where(step => step.Kind == StepKind.Aspect).ToArray();
        foreach (var step in aspectSteps)
        {
            var aspectId = step.Id.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
                ? step.Id["aspect-".Length..]
                : step.Id;
            if (aspectId == "visual-review" && changedPaths is not null
                && !Contract.VisualReviewEvidence.IsFrontendVisualChange(changedPaths))
            {
                skipped.Add(new(aspectId, "No frontend template or style changed."));
                continue;
            }
            if (!isConcept && !configured.Contains(aspectId))
            {
                skipped.Add(new(aspectId, "Not selected by ReviewDecisionOrchestrator:AspectRunners."));
                continue;
            }
            if (!PipelineStepConfigResolver.ShouldRun(settings, step, condition))
            {
                var configuredCondition = PipelineStepConfigResolver.ResolveCondition(settings, step);
                var reason = !PipelineStepConfigResolver.IsEnabled(settings, step)
                    ? "Disabled by the project pipeline-step setting."
                    : $"Project pipeline-step condition '{configuredCondition?.When ?? "unknown"}'"
                      + (string.IsNullOrWhiteSpace(configuredCondition?.Value)
                          ? string.Empty : $" = '{configuredCondition.Value}'")
                      + " did not match this card.";
                skipped.Add(new(aspectId, reason));
                continue;
            }
            var definition = isConcept
                ? ConceptFitDefinition
                : AspectRunnerService.Catalogue[aspectId];

            var choice = ReviewAspectModelPolicy.ResolveStep(hostDefault, step.Id,
                PipelineStepConfigResolver.ResolveCliType(settings, step),
                PipelineStepConfigResolver.ResolveModelWithSource(settings, step, hostDefault.Model));
            if (choice.Fault is not null)
                _logger?.LogWarning(
                    "remote-review-plan-model-pair-fallback task={TaskKey} step={StepId} cli={Cli} model={Model}: {Fault}",
                    string.IsNullOrWhiteSpace(task.TaskKey) ? task.Id : task.TaskKey,
                    step.Id, choice.CliType, choice.Model, choice.Fault);
            var model = choice.Model;
            var cliType = choice.CliType;
            var thinking = PipelineStepConfigResolver.ResolveThinkingLevel(
                settings,
                step,
                cliType,
                model,
                PipelineStepModelDefaults.SupportThinkingLevel);
            var dossier = isConcept ? ConceptDossierReviewMaterial.Render(task.FolderPath, conceptDossier) : null;
            var prompt = _aspects.BuildAspectPrompt(
                definition,
                inputs,
                model,
                PipelineStepConfigResolver.ResolvePrompt(settings, step.Id),
                step.Id,
                isConcept ? new Dictionary<string, string?>
                {
                    ["dossier_material"] = dossier,
                } : null);
            if (dossier is not null && !prompt.Contains(dossier, StringComparison.Ordinal))
                prompt += "\n\n## Dossier files\n\n" + dossier;
            // Frozen prompt bytes version the stock layout. Explicit overrides
            // and specialised aspects retain their authored evidence and scope.
            if (!isConcept)
                prompt = ReviewAspectCachePrompt.ComposeStock(prompt, inputs, cliType, aspectId,
                    PipelineStepConfigResolver.ResolvePrompt(settings, step.Id));
            // AGT-2820: the budget is derived from the toolchain, the model, the
            // thinking level, and the size of the prompt - not from a constant.
            // The executor re-derives it once it has appended the authoritative
            // diff, which is material this frozen prompt does not yet carry.
            var budget = ReviewAspectTimeoutPolicy.Derive(
                cliType,
                model,
                thinking,
                Contract.ReviewAspectBudgetPolicy.MaterialCharacters(prompt),
                _configuration);
            commands.Add(new Contract.ReviewCommandDto(
                step.Id,
                aspectId,
                cliType,
                [],
                TimeoutSeconds: budget.Seconds,
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                Prompt: prompt,
                CliType: cliType,
                Model: model,
                ThinkingLevel: thinking));
        }

        var deepDecision = isConcept ? null : DecideDeepReview(task, inputs.TaskBody, projectSettings, changedPaths,
            noChange ? 0 : _git is null || string.IsNullOrWhiteSpace(repositoryPath)
                || string.IsNullOrWhiteSpace(integrationRef) || !ReviewSubjectStore.IsValidResultSha(resultSha)
                    ? -1 : _git.ChangedLinesAgainstMergeBase(repositoryPath, integrationRef, resultSha!) ?? -1,
            resultSha ?? string.Empty);
        if (deepDecision is { Dispatched: true })
        {
            var prompt = _aspects.BuildAspectPrompt(DeepReviewDefinition, inputs, deepDecision.Model,
                pipelineStepId: "post-deep-review",
                extraValues: new Dictionary<string, string?>
                {
                    ["decision_receipt"] = deepDecision.ReceiptId,
                    ["acceptance_scope_json"] = JsonSerializer.Serialize(inputs.AcceptanceScope),
                    ["task_body_json"] = JsonSerializer.Serialize(inputs.TaskBody),
                    ["results_inventory_json"] = JsonSerializer.Serialize(inputs.ResultsInventory),
                    ["earlier_deep_findings_json"] = JsonSerializer.Serialize(
                        AgentStudio.Review.ReviewProjectionReader.Read(task, [], null).Attempts
                            .SelectMany(attempt => attempt.Aspects
                                .Where(aspect => string.Equals(aspect.Aspect, "deep-review", StringComparison.OrdinalIgnoreCase)
                                    && Contract.ReviewGradingPolicy.IsBlockingToken(aspect.Status))
                                .Select(aspect => new { attempt.AttemptId, aspect.Status, aspect.Summary }))
                            .Take(2).ToArray()),
                });
            var reviewCli = deepDecision.Model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
                ? "claude" : "codex";
            var budget = ReviewAspectTimeoutPolicy.Derive(reviewCli, deepDecision.Model,
                deepDecision.ThinkingLevel, Contract.ReviewAspectBudgetPolicy.MaterialCharacters(prompt),
                _configuration);
            commands.Add(new Contract.ReviewCommandDto(
                "post-deep-review", "deep-review", reviewCli, [],
                TimeoutSeconds: budget.Seconds,
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                Prompt: prompt, CliType: reviewCli, Model: deepDecision.Model,
                ThinkingLevel: deepDecision.ThinkingLevel));
        }
        else if (deepDecision is { Mode: DeepReviewPolicy.Active })
            skipped.Add(new("deep-review", $"{deepDecision.Mode} decision {deepDecision.ReceiptId}: "
                + string.Join(", ", deepDecision.Grounds)));

        var hasModelReview = commands.Any(command =>
            Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind));
        if (deferBuildTest && !hasModelReview)
        {
            commands = ordinaryToolPlan.Commands.ToList();
            deferralDisabledReason = "No model review is planned for this delivery.";
        }
        // A disabled concept-fit leaves a Dossier plan without commands; the
        // concept policy then fails the review for the missing aspect.
        if (conceptDossier is not null && commands.Count == 0)
            commands.Add(V1ReviewPlaneEndpoints.CompletionOnlyCommand());
        if (noChange)
        {
            if (commands.Count == 0)
                commands.Add(V1ReviewPlaneEndpoints.CompletionOnlyCommand());
            _logger?.LogInformation(
                "remote-review-plan-no-change task={TaskKey} resultSha={ResultSha} integrationRef={IntegrationRef}",
                string.IsNullOrWhiteSpace(task.TaskKey) ? task.Id : task.TaskKey, resultSha, integrationRef);
        }

        var plan = toolPlan with
        {
            Commands = commands,
            Preparation = noChange || conceptDossier is not null
                ? []
                : deferBuildTest && !hasModelReview
                    ? ordinaryToolPlan.Preparation : toolPlan.Preparation,
            BuildTestDeferredToBatch = deferBuildTest && hasModelReview,
            BuildTestDeferralDisabledReason = !deferBuildTest || !hasModelReview
                ? deferralDisabledReason : null,
            TestLevel = (deferBuildTest && hasModelReview) || perCardApplied ? testLevel : null,
            TestSelectionDigest = (deferBuildTest && hasModelReview) || perCardApplied ? selectionDigest : null,
            RequiredAspects = commands
                .Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            BriefSha256 = isConcept ? BriefVersionStore.ReadCurrent(task.FolderPath)
                : BriefVersionStore.ReadOrCreate(task.FolderPath),
            SkippedAspects = skipped,
            TaskMode = isConcept ? TaskModes.Concept : null,
            DeepReviewDecision = deepDecision is null ? null : new Contract.DeepReviewDecisionDto(
                deepDecision.ReceiptId, deepDecision.Mode, deepDecision.Selected,
                deepDecision.Dispatched, deepDecision.Model, deepDecision.ThinkingLevel,
                deepDecision.Grounds, deepDecision.RuleVersionId),
        };
        if (isConcept) return plan;
        var requireFullReview = false;
        if (ReviewConcernRoundStore.Read(task.FolderPath) is { StillOpen: true, PreviousVerdicts.Count: > 0 }
            && _observations is not null)
        {
            try
            {
                var cohorts = _observations.Project(task.ProjectName, task.WatchPath).ByModelAndReasoning
                    .Where(row => plan.Commands.Any(command =>
                        Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                        && string.Equals(AgentStudio.Tokens.ModelIdentity.StoredId(command.Model),
                            row.Model, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(command.ThinkingLevel ?? "unknown",
                            row.ReasoningLevel, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                requireFullReview = ReviewObservationPolicy.RequireFullReview(cohorts);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "RemoteReviewPlanBuilder: observation lookup unavailable; use scoped review");
            }
        }
        plan = ApplyScopedReview(
                plan,
                task,
                repositoryPath,
                projectSettings,
                integrationRef,
                expectedResultSha,
                requireFullReview);
        plan = ApplyEquivalentDeliveryReview(plan, task, repositoryPath, integrationRef, resultSha);
        plan = ApplyMergeOnlyRoundReview(plan, task, repositoryPath, projectSettings, integrationRef,
            resultSha, requireFullReview);
        // A scoped review drops carried-over agent commands. A no-change plan
        // has no tool command left to keep it non-empty, so it falls back to
        // the completion-only command the Task Server can admit.
        return noChange && plan.Commands.Count == 0
            ? plan with
            {
                Commands = [V1ReviewPlaneEndpoints.CompletionOnlyCommand()],
                RequiredAspects = plan.RequiredAspects
                    .Append("completion")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            }
            : plan;
    }

    private static Contract.ReviewPlanDto ApplyEquivalentDeliveryReview(
        Contract.ReviewPlanDto plan, TaskInfo task, string? repositoryPath,
        string? integrationRef, string? resultSha)
    {
        if (plan.CarriedVerdicts is { Count: > 0 }
            || string.IsNullOrWhiteSpace(repositoryPath)
            || string.IsNullOrWhiteSpace(integrationRef)
            || !ReviewSubjectStore.IsValidResultSha(resultSha)
            || string.IsNullOrWhiteSpace(plan.BriefSha256))
            return plan;

        var previous = LatestPassedSettlement(task, plan.BriefSha256);
        if (previous is null) return plan;

        var currentBase = Git(repositoryPath, "merge-base", integrationRef, resultSha!);
        if (!ReviewSubjectStore.IsValidResultSha(currentBase)) return plan;
        var priorDiff = OwnChangeDiff(repositoryPath, previous.Report.Workspace.MergeBaseSha!,
            previous.Report.Workspace.ExpectedResultSha);
        var currentDiff = OwnChangeDiff(repositoryPath, currentBase!, resultSha!);
        if (priorDiff is null || currentDiff is null) return plan;

        var ordinaryEqual = DiffForPaths(priorDiff, OrdinaryPath) == DiffForPaths(currentDiff, OrdinaryPath);
        if (!ordinaryEqual) return plan;
        var evidenceEqual = DiffForPaths(priorDiff, EvidencePath) == DiffForPaths(currentDiff, EvidencePath);
        var carryAspects = evidenceEqual
            ? DefaultAspectIds.Where(id => id != "visual-review").ToArray()
            : ["code-quality", "documentation-impact"];
        var previousVerdicts = previous.Report.Verdicts
            .Where(verdict => string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase))
            .GroupBy(verdict => verdict.Aspect, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var carry = new List<Contract.ReviewVerdictDto>();
        foreach (var aspect in carryAspects)
        {
            if (!plan.Commands.Any(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                    && string.Equals(command.Aspect, aspect, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!previousVerdicts.TryGetValue(aspect, out var verdict)) return plan;
            carry.Add(verdict with
            {
                Classification = "carried-over",
                CarriedOverFrom = previous.AttemptId,
            });
        }
        if (carry.Count == 0) return plan;
        return plan with
        {
            Commands = plan.Commands.Where(command => !carry.Any(verdict =>
                string.Equals(command.Aspect, verdict.Aspect, StringComparison.OrdinalIgnoreCase))).ToArray(),
            CarriedOverFrom = previous.AttemptId,
            CarriedVerdicts = carry,
        };
    }

    private static bool EvidencePath(string path)
        => string.Equals(Path.GetFileName(path), "status.md", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("results/", StringComparison.OrdinalIgnoreCase)
           || path.Contains("/results/", StringComparison.OrdinalIgnoreCase);

    private static bool OrdinaryPath(string path) => !EvidencePath(path);

    private static Dictionary<string, string>? OwnChangeDiff(string repositoryPath, string baseSha, string resultSha)
    {
        var paths = Git(repositoryPath, "diff", "--name-only", "-z", baseSha, resultSha);
        if (paths is null) return null;
        var diffs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var diff = Git(repositoryPath, "diff", "--no-ext-diff", "--no-textconv", "--binary",
                "--unified=0", baseSha, resultSha, "--", path);
            if (diff is null) return null;
            diffs[path] = string.Join('\n', diff.Split('\n')
                .Where(line => !line.StartsWith("index ", StringComparison.Ordinal))
                .Select(line => line.StartsWith("@@ ", StringComparison.Ordinal)
                    ? "@@" : line));
        }
        return diffs;
    }

    private static string DiffForPaths(Dictionary<string, string> diffs, Func<string, bool> predicate)
        => string.Join("\n", diffs.Where(item => predicate(item.Key))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key + "\n" + item.Value));

    private DeepReviewDecision DecideDeepReview(TaskInfo task, string taskBody, ProjectSettings? settings,
        IReadOnlyList<string>? changedPaths, int changedLines, string resultSha)
    {
        var ledger = ReviewRoundBudgetStore.Read(task.FolderPath);
        var blocked = ledger.Rounds.SelectMany(round => round.BlockingAspects).ToArray();
        var gateFailures = blocked.Count(aspect => aspect is "build-tests" or "lint" or "review-verification");
        var project = _projects?.FindByIdOrDisplayName(task.ProjectName);
        var ruleSet = project?.WorkspaceId is { } workspaceId && _rules is not null
            ? _rules.EffectiveRules(workspaceId, project.Id) : [];
        var rules = ruleSet.Where(rule => rule.Trigger == DecisionRuleTriggers.DeepReview)
            .OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
        var values = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["review.changed-lines"] = changedLines,
            ["review.previous-blocks"] = blocked.Length,
            ["review.gate-failures"] = gateFailures,
            ["review.delivered-rounds"] = ledger.Delivered,
        };
        var matching = rules.LastOrDefault(rule => values.TryGetValue(rule.FactId, out var value)
            && (rule.WarningThreshold is null
                || int.TryParse(rule.WarningThreshold, out var threshold) && value >= threshold));
        var typedSettings = PipelineTypeSettings.ForTask(settings, task);
        var deepSetting = typedSettings?.PipelineSteps?.GetValueOrDefault("post-deep-review");
        var selectedDeepModel = matching?.Model ?? deepSetting?.Model;
        var selectedDeepThinking = matching?.ThinkingLevel ?? deepSetting?.ThinkingLevel;
        var ruleSelection = matching is null ? (bool?)null
            : matching.CandidateActions.Contains(Contract.DecisionActionKinds.Prepare, StringComparer.Ordinal);
        var critical = _routing?.CorrectnessFloor(task.TaskType, task.Title, taskBody)?.Id == "sol-xhigh"
            || task.Tags.Any(tag => tag.Equals("p0", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("security", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("data-loss", StringComparison.OrdinalIgnoreCase));
        var ruleVersion = project?.WorkspaceId is { } id && _rules is not null
            ? _rules.ActiveVersionId(id, project.Id) ?? _rules.ActiveVersionId(id) : null;
        var preliminaryFacts = new DeepReviewGrounds(task.Id, resultSha,
            critical ? "critical" : "routine", changedPaths ?? [], changedLines,
            task.Model, task.ThinkingLevel, blocked.Length, ledger.Delivered,
            settings?.MaxDeliveredReviewRounds ?? ReviewRoundBudgetPolicy.DefaultMaximumRounds,
            gateFailures, "unknown");
        var preliminary = MonolithTransitionAdapter.DeepReview(preliminaryFacts, DeepReviewPolicy.Shadow,
            ruleVersion, ruleSelection, selectedDeepModel, selectedDeepThinking);
        var reviewCli = preliminary.Model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? "claude" : "codex";
        var quota = _quota?.Plan(new QuotaAdmissionRequest(reviewCli, preliminary.Model,
            preliminary.ThinkingLevel, task.ProjectName, 0, "remote-deep-review-plan"));
        var operatorSwitch = _quota?.IsOperatorSwitchActive(reviewCli) == true;
        var quotaState = DeepReviewQuotaState(quota, operatorSwitch);
        return MonolithTransitionAdapter.DeepReview(preliminaryFacts with { QuotaState = quotaState },
            _configuration["DeepReview:Mode"] ?? DeepReviewPolicy.Shadow,
            ruleVersion, ruleSelection, selectedDeepModel, selectedDeepThinking);
    }

    internal static string DeepReviewQuotaState(QuotaAdmissionPlan? plan, bool operatorSwitch)
        => plan is null ? "unknown" : operatorSwitch ? "available"
            : !plan.ShouldLaunch ? "capped"
            : plan.IsFallback ? "unavailable" : "available";

    /// <summary>
    /// Reads unchanged files from the checkout and the delivery's changed paths
    /// from the result commit (one bounded <c>git cat-file --batch</c>). The
    /// identifier index is memoised per test project and checkout HEAD.
    /// </summary>
    private DotNetReferenceSelection ReferenceSelection(string repositoryPath, string resultSha)
        => new()
        {
            Overlay = paths =>
            {
                if (_git is null) return DotNetSourceOverlay.Failed("no git service to read the delivery");
                var read = _git.ReadFilesAtRevision(repositoryPath, resultSha, paths);
                return new DotNetSourceOverlay(read.Files, read.Failure);
            },
            IndexMemo = (testProject, build) => _git is null
                ? build()
                : _git.MemoizeByHead(
                    repositoryPath,
                    $"dotnet-reference-index\n{Path.GetFullPath(repositoryPath)}\n{testProject}",
                    build),
            Time = _time,
        };

    private StagedVerifyPlan FixedReviewSelection(TaskInfo task, string repositoryPath, VerifyPlan verify,
        StagedVerifyPlan selection, IReadOnlyList<string>? changedPaths, string? resultSha)
    {
        // The checkout may be the integration tip. Ownership and consumer
        // bindings must come from the immutable result, including renamed files.
        var frontendTree = FrontendWorkPackagePlanner.TouchesFrontend(changedPaths)
            ? FrontendTree(repositoryPath, resultSha) : null;
        var selected = PerCardVerifyPlanner.WithReviewBase(repositoryPath, verify, selection, changedPaths, frontendTree);
        if (selected.Commands.Any(command => command.TestScope == ReviewBaseChecks.Scope))
            _logger?.LogInformation(
                "remote-review-fixed-checks task={TaskKey} selection={Digest} selected={Selected} attempted={Attempted} awaitingExecution={AwaitingExecution}",
                task.TaskKey ?? task.Id, selected.Audit.Digest, string.Join(" | ", selected.Audit.SelectedCommands),
                string.Join(" | ", selected.Audit.AttemptedTestCommands),
                string.Join(" | ", selected.Audit.SelectedCommands.Except(selected.Audit.AttemptedTestCommands)));
        if (frontendTree is not null)
            _logger?.LogInformation(
                "remote-review-angular-selection task={TaskKey} result={ResultSha} selection={Digest} reasons={Reasons} selected={Selected} attempted={Attempted}",
                task.TaskKey ?? task.Id, resultSha, selected.Audit.Digest, string.Join(" | ", selected.Audit.Reasons),
                string.Join(" | ", selected.Audit.SelectedCommands), string.Join(" | ", selected.Audit.AttemptedTestCommands));
        return selected;
    }

    private IReadOnlyDictionary<string, string> FrontendTree(string repositoryPath, string? resultSha)
    {
        if (_git is null || !ReviewSubjectStore.IsValidResultSha(resultSha))
            throw new InvalidDataException("Angular review selection requires a readable pinned result tree.");
        var listing = Git(repositoryPath, "ls-tree", "-r", "--name-only", "-z", resultSha!, "--", "frontend");
        if (listing is null) throw new InvalidDataException("Cannot list the pinned Angular result tree.");
        var paths = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(FrontendWorkPackagePlanner.IsSourceTreePath).ToArray();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in paths.Chunk(GitService.MaxRevisionFileReadPaths))
        {
            var read = _git.ReadFilesAtRevision(repositoryPath, resultSha!, chunk);
            if (read.Failure is not null)
                throw new InvalidDataException("Cannot read the pinned Angular result tree: " + read.Failure);
            foreach (var (path, content) in read.Files)
                if (content is not null) files[path] = content;
        }
        return files;
    }

    private void LogReferenceSelection(TaskInfo task, TestSelectionAudit audit)
    {
        if (_logger is null || audit.PlanBuildTime is null) return;
        foreach (var selection in audit.ReferenceSelections)
        {
            _logger.LogInformation(
                "Review test selection for {TaskId}: {Summary}; seed types: {Seeds}",
                task.Id,
                selection.Summary,
                selection.SeedTypes.Count == 0 ? "none" : string.Join(", ", selection.SeedTypes));
        }
        _logger.LogInformation(
            "Review test selection for {TaskId}: plan built in {Milliseconds} ms",
            task.Id,
            (long)audit.PlanBuildTime.Value.TotalMilliseconds);
    }

    private static Contract.ReviewPlanDto ApplyScopedReview(
        Contract.ReviewPlanDto plan,
        TaskInfo task,
        string? repositoryPath,
        ProjectSettings? settings,
        string? integrationRef,
        string? expectedResultSha,
        bool noCarryOver)
    {
        var ledger = ReviewConcernRoundStore.Read(task.FolderPath);
        if (ledger is not { StillOpen: true, PreviousVerdicts.Count: > 0 }
            || string.IsNullOrWhiteSpace(ledger.ReviewedResultSha)
            || string.IsNullOrWhiteSpace(ledger.ReviewedIntegrationTipSha))
            return plan;

        plan = plan with
        {
            ScopedReview = new Contract.ScopedReviewPlanDto(
                !noCarryOver && (settings?.ScopedReviewAfterFinding ?? true),
                ledger.ReviewAttemptId,
                ledger.ReviewedResultSha,
                ledger.ReviewedIntegrationTipSha,
                Math.Max(0, settings?.ScopedReviewMaximumDeltaFiles ?? 20),
                ledger.AspectIds,
                ledger.PreviousVerdicts.Where(verdict =>
                    !string.Equals(verdict.Aspect, "deep-review", StringComparison.OrdinalIgnoreCase)).ToArray()),
        };
        // Freeze the code-quality recheck before deciding which aspects can carry
        // over. Admission and checkpoint recovery then see the same contract.
        if (ledger.AspectIds.Any(Contract.CodeQualityReviewPolicy.IsCodeQuality)
            && ledger.PreviousVerdicts.LastOrDefault(verdict =>
                Contract.CodeQualityReviewPolicy.IsCodeQuality(verdict.Aspect)) is { } previousQuality)
        {
            plan = plan with
            {
                Commands = plan.Commands.Select(command =>
                    Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                    && Contract.CodeQualityReviewPolicy.IsCodeQuality(command.Aspect)
                        ? command with
                        {
                            Prompt = Contract.CodeQualityReviewPolicy.WithPreviousFinding(
                                command.Prompt, ledger.ReviewAttemptId, previousQuality),
                        }
                        : command).ToArray(),
            };
        }

        if (string.IsNullOrWhiteSpace(repositoryPath)
            || string.IsNullOrWhiteSpace(expectedResultSha))
            return plan;

        var semanticCommands = plan.Commands
            .Where(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .ToArray();
        var previous = ledger.PreviousVerdicts
            .Where(verdict => !string.Equals(verdict.Aspect, "deep-review", StringComparison.OrdinalIgnoreCase)
                && semanticCommands.Any(command =>
                string.Equals(command.Aspect, verdict.Aspect, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(verdict => verdict.Aspect, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        if (previous.Count == 0) return plan;

        var integrationTip = string.IsNullOrWhiteSpace(integrationRef)
            ? null
            : Git(repositoryPath, "rev-parse", integrationRef);
        var identityMatches = !string.IsNullOrWhiteSpace(ledger.ReviewedIntegrationTipSha)
                              && string.Equals(
                                  integrationTip,
                                  ledger.ReviewedIntegrationTipSha,
                                  StringComparison.OrdinalIgnoreCase);
        var delta = Git(
            repositoryPath,
            "diff",
            "--name-status",
            $"{ledger.ReviewedResultSha}..{expectedResultSha}");
        var deltaAvailable = delta is not null;
        var changes = ParseDelta(delta);
        var facts = new Contract.ScopedReviewFacts(
            Enabled: !noCarryOver && (settings?.ScopedReviewAfterFinding ?? true),
            ReviewedTreeIdentityMatches: identityMatches && deltaAvailable,
            DeltaFileCount: deltaAvailable ? changes.Count : -1,
            MaximumDeltaFiles: Math.Max(0, settings?.ScopedReviewMaximumDeltaFiles ?? 20),
            ChangedFiles: changes.Select(item => item.Path).ToArray(),
            RemovedFiles: changes.Where(item => item.Removed).Select(item => item.Path).ToArray(),
            PreviousAspects: previous.Values.Select(verdict => new Contract.ScopedReviewAspect(
                verdict.Aspect,
                verdict.Status,
                EvidencePaths(verdict.EvidenceChecked),
                ledger.AspectIds.Contains(verdict.Aspect, StringComparer.OrdinalIgnoreCase))).ToArray());
        var decisions = Contract.ScopedReviewPolicy.Plan(facts)
            .ToDictionary(item => item.Aspect, StringComparer.OrdinalIgnoreCase);
        var carried = new List<Contract.ReviewVerdictDto>();
        var scopedCommands = new List<Contract.ReviewCommandDto>();
        foreach (var plannedCommand in plan.Commands)
        {
            var command = plannedCommand;
            if (!Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                || !decisions.TryGetValue(command.Aspect, out var decision)
                || decision.Run
                || string.Equals(command.Aspect, "visual-review", StringComparison.OrdinalIgnoreCase)
                || noCarryOver
                || !previous.TryGetValue(command.Aspect, out var priorVerdict))
            {
                if (Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                    && ledger.AspectIds.Contains(command.Aspect, StringComparer.OrdinalIgnoreCase)
                    && previous.TryGetValue(command.Aspect, out var raised))
                {
                    command = command with
                    {
                        Prompt = Contract.CodeQualityReviewPolicy.WithPreviousFinding(
                            command.Prompt, ledger.ReviewAttemptId, raised),
                    };
                }
                scopedCommands.Add(command);
                continue;
            }

            carried.Add(priorVerdict with
            {
                Classification = "carried-over",
                CarriedOverFrom = ledger.ReviewAttemptId,
            });
        }

        if (carried.Count == 0) return plan;
        return plan with
        {
            Commands = scopedCommands,
            CarriedOverFrom = ledger.ReviewAttemptId,
            CarriedVerdicts = carried,
        };
    }

    private static IReadOnlyList<string> EvidencePaths(string? evidence)
        => string.IsNullOrWhiteSpace(evidence)
            ? []
            : evidence.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<(string Path, bool Removed)> ParseDelta(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var rows = new List<(string Path, bool Removed)>();
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 2) continue;
            var status = columns[0];
            var path = columns[^1];
            rows.Add((path, status.StartsWith('D')));
        }
        return rows;
    }

    private static string? Git(string repositoryPath, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var result = AgentStudio.Git.GitProcessBoundary.Run(start, timeout: TimeSpan.FromSeconds(10));
            return result.Success ? result.StandardOutput.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The delivery's changed paths against its merge base with
    /// <paramref name="baseRef"/>, or null when the Studio checkout cannot
    /// compute them. A null diff leaves the Compose render to the pre-develop
    /// gate, which derives it from the exact merge result and fails closed on
    /// a host that cannot render.
    /// </summary>
    public static IReadOnlyList<string>? DeliveryChangedFiles(
        AgentStudio.Git.GitService git,
        string? repositoryPath,
        string? baseRef,
        string? resultSha)
        => string.IsNullOrWhiteSpace(repositoryPath)
           || string.IsNullOrWhiteSpace(baseRef)
           || string.IsNullOrWhiteSpace(resultSha)
            ? null
            : git.ChangedPathsAgainstMergeBase(repositoryPath, baseRef, resultSha);

    /// <summary>
    /// The completion path's changed paths. They are measured against the
    /// run's pickup base, which a continuation round (mechanical fallback,
    /// lost-worker or salvage continuation, older-brief start) sets to the
    /// previous, still unintegrated result. A round without a commit then has
    /// an empty pickup diff while its delivery still changes code against the
    /// integration line. An empty pickup diff is therefore measured again
    /// against <paramref name="integrationRef"/>, the base the no-change rule
    /// and every rebuild path use (AGT-3079). A non-empty or null pickup diff
    /// is returned unchanged.
    /// </summary>
    public static IReadOnlyList<string>? CompletionChangedFiles(
        AgentStudio.Git.GitService git,
        string? repositoryPath,
        string? pickupBaseSha,
        string? integrationRef,
        string? resultSha)
    {
        var pickupBase = string.IsNullOrWhiteSpace(pickupBaseSha) ? integrationRef : pickupBaseSha;
        var changed = DeliveryChangedFiles(git, repositoryPath, pickupBase, resultSha);
        return changed is { Count: 0 } && !string.Equals(pickupBase, integrationRef, StringComparison.Ordinal)
            ? DeliveryChangedFiles(git, repositoryPath, integrationRef, resultSha)
            : changed;
    }

    private IReadOnlySet<string> ConfiguredAspectIds()
    {
        var section = _configuration.GetSection("ReviewDecisionOrchestrator:AspectRunners");
        var source = section.Exists()
            ? section.GetChildren().Select(child => child.Value)
            : DefaultAspectIds;
        return source
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // The status text carries results/status.md in full, so the results
    // inventory leaves out its head excerpt (AGT-3880).
    private static readonly IReadOnlySet<string> AgentReportExcerptExclusion =
        new HashSet<string>(StringComparer.Ordinal) { "status.md" };

    private static AspectRunInputs Inputs(TaskInfo task, string? integrationRef)
    {
        var briefPath = Path.Combine(task.FolderPath, "prompt.md");
        var brief = ReviewBrief.Read(briefPath, task.Id);
        var taskBody = brief.Text;
        var recentLog = ReviewActivity.Read(task);
        var concept = TaskModes.IsConcept(task.Mode);
        var status = concept
            ? new ReviewStatus(ReadStatus(task.FolderPath), false, string.Empty)
            : ReviewStatus.Read(task.FolderPath);
        var baseline = string.IsNullOrWhiteSpace(integrationRef) ? "the configured integration ref" : integrationRef;
        var diff =
            $"This aspect runs on a remote Review Executor at the immutable Result-SHA. " +
            $"The executor appends the authoritative changed-file list and unified diff against " +
            $"the merge-base with {baseline} before invoking the aspect. " +
            "Use that appended material; do not infer the change from the Studio checkout.";
        return new AspectRunInputs(
            task.ProjectName,
            task.Id,
            task.Title ?? task.Id,
            task.FolderPath,
            taskBody,
            recentLog,
            diff,
            status.Text)
        {
            // Concept review uses the generated status summary as its authority.
            // Its compact inventory must not reintroduce the agent report after
            // ReadStatus has selected that summary.
            ResultsInventory = concept
                ? ResultsInventory.Render(task.FolderPath)
                : ResultsInventory.RenderForReview(task.FolderPath,
                    excerptExclusions: status.CarriesAgentReport ? AgentReportExcerptExclusion : null),
            StatusReportNote = status.RequirementNote,
            CardMode = ReviewCardMode.Describe(task.Mode),
            AcceptanceScope = RequirementAcceptanceScope.Describe(
                task.AcceptanceScope,
                brief.DeclaresBoundedLegacyScope),
        };
    }

    /// <summary>
    /// A remote agent writes its report to <c>results/status.md</c>. The card's
    /// <c>status.md</c> is the generated summary, or a scaffold that says nothing
    /// about the work until a summary exists, so a scaffold never hides the report.
    /// Concept review reads this selection; the summary is its authority.
    /// </summary>
    private static string ReadStatus(string folderPath)
    {
        var summary = Read(Path.Combine(folderPath, "status.md"), ReviewStatus.Characters, string.Empty);
        if (ReviewStatus.IsSummary(summary))
            return summary;
        return Read(Path.Combine(folderPath, "results", "status.md"), ReviewStatus.Characters, summary);
    }

    /// <summary>
    /// The status every coding aspect reads (AGT-3880). The generated summary
    /// condenses the agent's report, so details a follow-up round asked for in
    /// <c>results/status.md</c> can be gone from it. A plan therefore carries
    /// both under their own headings, within the bound one status file had.
    /// The agent report keeps its tail first; the summary gets what is left.
    /// Without a summary, or with only the scaffold, the text is chosen as
    /// <see cref="ReadStatus"/> chooses it.
    /// </summary>
    internal sealed record ReviewStatus(string Text, bool CarriesAgentReport, string RequirementNote)
    {
        internal const int Characters = 16_000;

        internal const string AgentReportHeading =
            "=== Agent report (results/status.md, written by the agent) ===";

        internal const string SummaryHeading =
            "=== Generated summary (task-root status.md, condensed from the agent report) ===";

        internal const string SummaryTrimmed =
            "... (summary trimmed: the agent report above has priority within the status bound)";

        internal const string SummaryOmitted =
            "(Generated summary omitted: the agent report fills the status bound.)";

        internal const string AgentReportWithheld =
            "(Agent report withheld: results/status.md is a link.)";

        internal const string AgentReportNote =
            "The generated summary points to results/status.md. The agent report is the place "
            + "to check what the task and each follow-up round asked for: judge a round requirement "
            + "against the agent report, not against the condensed summary alone.";

        // Below this many characters a trimmed summary says nothing useful.
        private const int MinimumSummaryCharacters = 200;

        internal static bool IsSummary(string summary)
            => summary.Length > 0
               && !summary.Contains(AgentStudio.Tasks.TaskTransitionService.ResultScaffoldMarker, StringComparison.Ordinal);

        internal static ReviewStatus Read(string folderPath)
        {
            var summary = RedactedTail(RemoteReviewPlanBuilder.Read(
                Path.Combine(folderPath, "status.md"), Characters, string.Empty));
            var resultsPath = Path.Combine(folderPath, "results");
            var reportPath = Path.Combine(resultsPath, "status.md");
            if (IsLink(resultsPath) || IsLink(reportPath))
            {
                var summaryRoom = Characters - AgentReportWithheld.Length - 2;
                if (summary.Length > summaryRoom) summary = summary[^summaryRoom..];
                return new(summary.Length == 0 ? AgentReportWithheld : summary.TrimEnd() + "\n\n" + AgentReportWithheld,
                    false, string.Empty);
            }
            var report = RemoteReviewPlanBuilder.Read(reportPath, Characters, string.Empty).Trim();
            if (File.Exists(reportPath) && new FileInfo(reportPath).Length > Characters)
            {
                using var reader = new StreamReader(reportPath);
                var head = new char[1_000];
                var count = reader.Read(head, 0, head.Length);
                const string omitted = "\n... (report middle omitted)\n";
                var tailRoom = Characters - count - omitted.Length;
                report = new string(head, 0, count) + omitted + report[^Math.Min(tailRoom, report.Length)..];
            }
            report = CredentialRedactor.Redact(report);
            if (report.Length > Characters)
            {
                const string omitted = "\n... (report middle omitted)\n";
                const int headCharacters = 1_000;
                report = report[..headCharacters] + omitted
                    + report[^(Characters - headCharacters - omitted.Length)..];
            }
            if (report.Length == 0) return new(summary, false, string.Empty);
            if (!IsSummary(summary)) return new(report, true, string.Empty);
            return Compose(summary.Trim(), report);
        }

        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static string RedactedTail(string text)
        {
            var redacted = CredentialRedactor.Redact(text);
            return redacted.Length <= Characters ? redacted : redacted[^Characters..];
        }

        internal static ReviewStatus Compose(string summary, string report)
        {
            var note = summary.Contains("results/status.md", StringComparison.OrdinalIgnoreCase)
                ? AgentReportNote : string.Empty;
            var reportRoom = Characters - AgentReportHeading.Length - 1;
            if (report.Length > reportRoom)
            {
                const int headCharacters = 1_000;
                const string marker = "\n... (report middle omitted)\n";
                report = report[..headCharacters] + marker
                    + report[^(reportRoom - headCharacters - marker.Length)..];
            }
            var text = new StringBuilder();
            text.AppendLf(AgentReportHeading).Append(report);
            var summaryRoom = Characters - text.Length - SummaryHeading.Length - 3;
            if (summary.Length <= summaryRoom)
                text.AppendLf().AppendLf().AppendLf(SummaryHeading).Append(summary);
            else if (summaryRoom - SummaryTrimmed.Length - 1 >= MinimumSummaryCharacters)
                text.AppendLf().AppendLf().AppendLf(SummaryHeading)
                    .AppendLf(SummaryTrimmed)
                    .Append(summary[^(summaryRoom - SummaryTrimmed.Length - 1)..]);
            else if (text.Length + SummaryOmitted.Length + 2 <= Characters)
                text.AppendLf().AppendLf().Append(SummaryOmitted);
            return new(text.ToString(), true, note);
        }
    }

    /// <summary>
    /// Brief selection preserves opening authority and closing instructions.
    /// The complete bounded source determines legacy scope before redaction or
    /// selection. Oversized sources refuse plan creation instead of silently
    /// inventing a full-task boundary from an incomplete brief.
    /// </summary>
    internal sealed record ReviewBrief(string Text, bool DeclaresBoundedLegacyScope)
    {
        internal const int Characters = 64_000;
        internal const int SourceBytes = BoundedFileRead.EvidenceTextBytes;

        internal static ReviewBrief Read(string path, string fallback)
        {
            if (!File.Exists(path)) return new(fallback, false);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > SourceBytes)
                throw new InvalidDataException($"Review brief prompt.md has {stream.Length} source bytes; "
                    + $"the complete-source limit is {SourceBytes}. Shorten the brief before freezing review.");
            // Snapshot a bounded byte sequence once: digest, length and decoded
            // authority must all describe the same source, including its BOM.
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var source = reader.ReadToEnd();
            var legacy = RequirementAcceptanceScope.DeclaresBoundedLegacyScope(source);
            var text = CredentialRedactor.Redact(source);
            if (text.Length <= Characters) return new(text, legacy);

            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            // Leave a fixed metadata allowance so redaction expansion, source
            // lengths and the omission notice count against the same budget.
            var head = SafeHead(text, 48_000);
            var tail = SafeTail(text, Characters - head.Length - 512);
            var omitted = text.Length - head.Length - tail.Length;
            var note = $"\n[brief middle omitted: {omitted} redacted UTF-16 units; source: prompt.md; "
                + $"source bytes={bytes.Length}; source UTF-16 units={source.Length}; "
                + $"SHA-256={digest} (original bytes, before redaction).]\n";
            return new(head + note + tail, legacy);
        }
    }

    /// <summary>
    /// Activity has a tail policy, independent of the brief's head/tail policy.
    /// The active canonical file is authoritative; neither root leftovers nor
    /// rotations can replace it. A visible start marker narrows the retained
    /// window to the latest invocation, including its post-exit context.
    /// </summary>
    internal static class ReviewActivity
    {
        internal const int Characters = 16_000;

        internal static string Read(TaskInfo task)
        {
            var path = TaskPaths.CliOutputLog(task.FolderPath);
            if (!File.Exists(path)) return "(Source: logs/cli-output.log unavailable.)";
            var text = BoundedFileRead.ReadTailChars(path, Characters + 1);
            var trimmed = text.Length > Characters;
            if (trimmed)
            {
                // Discard the leading partial line, including any torn token
                // or surrogate, before redaction. A giant single line is withheld.
                var newline = text.IndexOf('\n');
                text = newline < 0 ? string.Empty : text[(newline + 1)..];
            }
            var lines = text.Split('\n');
            var lastStart = Array.FindLastIndex(lines, line =>
                System.Text.RegularExpressions.Regex.IsMatch(line,
                    @"^\[\d{2}:\d{2}:\d{2}(?:\.\d+)?\] \[system\] \[taskboard\] Started \S+ CLI \(PID",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant));
            if (lastStart >= 0) text = string.Join('\n', lines[lastStart..]);
            var context = lastStart >= 0 ? "latest run start in retained tail"
                : "run boundary unavailable in retained tail; activity may span runs";
            var attempt = task.Runner is { AttemptId: { Length: > 0 } id } runner
                ? $"; run context {id}, epoch {runner.AuthorityEpoch} (file rows have no epoch tags)" : string.Empty;
            var note = $"[Source: logs/cli-output.log; {context}{attempt}; "
                + $"source bytes={new FileInfo(path).Length}; older activity omitted when outside this tail.]\n";
            text = CredentialRedactor.Redact(text);
            note = CredentialRedactor.Redact(note);
            return note + SafeTail(text, Characters - note.Length);
        }
    }

    private static string SafeHead(string text, int characters)
    {
        var count = Math.Min(text.Length, characters);
        if (count > 0 && char.IsHighSurrogate(text[count - 1])) count--;
        return text[..count];
    }

    private static string SafeTail(string text, int characters)
    {
        var start = Math.Max(0, text.Length - characters);
        if (start < text.Length && char.IsLowSurrogate(text[start])) start++;
        return text[start..];
    }

    private static string Read(string path, int maximumCharacters, string fallback)
    {
        if (!File.Exists(path)) return fallback;
        // Only the tail is kept, so only the tail is read (AGT-2991).
        return BoundedFileRead.ReadTailChars(path, maximumCharacters);
    }
}
