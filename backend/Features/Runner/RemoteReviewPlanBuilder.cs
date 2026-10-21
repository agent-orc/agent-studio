using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Pipeline;
using AgentStudio.Git;
using Microsoft.Extensions.Logging;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Freezes deterministic tool gates and read-only semantic aspect calls into the
/// ReviewSubject before a remote executor claims it. The Task Server still owns
/// admission and the final lane decision. The runner receives only immutable
/// commands for the exact result SHA covered by the ReviewAttempt lease.
/// </summary>
public sealed class RemoteReviewPlanBuilder
{
    private static readonly string[] DefaultAspectIds =
    [
        "requirement-fit",
        "code-quality",
        "documentation-impact",
        "tests-and-evidence",
    ];

    private static readonly AspectDefinition ConceptFitDefinition = new(
        Id: "concept-fit",
        ConcernNamespace: "requirement",
        PromptTemplate: "review-aspect-concept-fit.md",
        Title: "Concept fit",
        FallbackSystem: "Compare the delivered Dossier with the current prompt.md. Block a missing required section, contradicted operator direction, open decision without a recommendation, or absent implementation cards. Cite the exact brief direction and Dossier location.");

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
    private readonly DeepReviewDecisionService? _deepReview;

    public RemoteReviewPlanBuilder(
        AspectRunnerService aspects,
        IConfiguration configuration,
        GitService? git = null,
        TimeProvider? time = null,
        ILogger<RemoteReviewPlanBuilder>? logger = null,
        DeepReviewDecisionService? deepReview = null)
    {
        _aspects = aspects;
        _configuration = configuration;
        _git = git;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _deepReview = deepReview;
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
        // a read credential for it.
        => BuildPlan(task, repositoryPath, projectSettings, integrationRef,
                expectedResultSha, changedFiles, immutableResultRef) with
            {
                PrivateRepository = Contract.ReviewRepositoryAccessPolicy.IsPrivate(
                    projectSettings?.RepositoryVisibility),
            };

    private Contract.ReviewPlanDto BuildPlan(
        TaskInfo? task,
        string? repositoryPath,
        ProjectSettings? projectSettings,
        string? integrationRef,
        string? expectedResultSha,
        IReadOnlyList<string>? changedFiles,
        string? immutableResultRef)
    {
        var toolPlan = V1ReviewPlaneEndpoints.FallbackPlan(
            repositoryPath,
            projectSettings?.BuildProfile,
            integrationRef,
            changedFiles) with
        {
            RequireDifferentHostFailureDomain = projectSettings?.RequireDifferentReviewHost == true,
        };
        var ordinaryToolPlan = toolPlan;
        var subject = task is null ? null : ReviewSubjectStore.Read(task.FolderPath);
        var resultRef = immutableResultRef ?? subject?.ImmutableResultRef;
        var resultSha = expectedResultSha ?? subject?.ResultSha;
        var changedPaths = changedFiles ?? (_git is null || string.IsNullOrWhiteSpace(repositoryPath)
            || string.IsNullOrWhiteSpace(integrationRef) || string.IsNullOrWhiteSpace(resultRef)
            ? null
            : _git.ChangedPathsAgainstMergeBase(
                repositoryPath, integrationRef, resultRef));
        var deferBuildTest = projectSettings?.BatchGate.Enabled == true
            && task is not null && !TaskModes.IsReportOnly(task.Mode)
            && (projectSettings.BatchGate.EffectiveScope == "code"
                || DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths))
            && changedPaths is { Count: > 0 }
            && !string.IsNullOrWhiteSpace(resultRef)
            && ReviewSubjectStore.IsValidResultSha(resultSha);
        var noChange = IsNoChangeResult(changedPaths, resultSha, resultRef);
        string? testLevel = null;
        string? selectionDigest = null;
        if (deferBuildTest)
        {
            var options = projectSettings!.BatchGate;
            var selectionRepo = repositoryPath ?? string.Empty;
            // Selection reads the project inventory from the existing checkout.
            // The built-in Agent Studio verifier reads these fixed command
            // inventory files. A delivery that changes that inventory needs its own full gate;
            // materializing a 7,000-file result worktree for every queued plan
            // would hold review admission behind filesystem work.
            if (changedPaths!.Any(path => path is ".agent-studio/project.yml"
                    or "frontend/package.json" or "frontend/angular.json"
                    or "backend/OrchestratorApi.csproj"
                    or "backend.Tests/OrchestratorApi.Tests.csproj"))
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
                    var selection = TestSelectionPlanner.Plan(selectionRepo,
                        verify, changedPaths, projectSettings.TestExecution, "remote-review", level,
                        ReferenceSelection(selectionRepo, resultSha!));
                    LogReferenceSelection(task!, selection.Audit);
                    var selected = selection.Commands.Where(command =>
                        command.Kind != VerifyCommandKind.Test || command.BlocksWorkPackage).ToArray();
                    if (selection.Audit.Level == TestExecutionLevels.Full
                        || selection.Audit.FullSuiteRequired
                        || selection.Audit.UnmappedSourceDirectories.Count > 0
                        || (level == TestExecutionLevels.WorkPackage
                            && !selected.Any(command => command.Kind == VerifyCommandKind.Test)))
                    {
                        deferBuildTest = false;
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
        var defaultModel = _configuration.GetValue(
            "ReviewDecisionOrchestrator:AspectModel",
            PipelineStepModelDefaults.SupportModel);
        var defaultCli = ReviewDecisionOrchestrator.NormalizeReviewCliType(
            _configuration.GetValue(
                "ReviewDecisionOrchestrator:Cli",
                PipelineStepModelDefaults.DefaultCli));

        var commands = toolPlan.Commands.ToList();
        var skipped = new List<Contract.ReviewSkippedAspectDto>();
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

            var model = PipelineStepConfigResolver.ResolveModel(settings, step, defaultModel);
            var cliType = PipelineStepConfigResolver.ResolveCliType(settings, step) ?? defaultCli;
            var thinking = PipelineStepConfigResolver.ResolveThinkingLevel(
                settings,
                step,
                cliType,
                model,
                PipelineStepModelDefaults.SupportThinkingLevel);
            var prompt = _aspects.BuildAspectPrompt(
                definition,
                inputs,
                model,
                PipelineStepConfigResolver.ResolvePrompt(settings, step.Id),
                step.Id);
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

        if (!isConcept && !noChange)
            AddDeepReview(task, repositoryPath, integrationRef, resultRef, resultSha, changedPaths, inputs,
                commands, skipped);

        var hasModelReview = commands.Any(command =>
            Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind));
        if (deferBuildTest && !hasModelReview)
            commands = ordinaryToolPlan.Commands.ToList();
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
            Preparation = noChange
                ? []
                : deferBuildTest && !hasModelReview
                    ? ordinaryToolPlan.Preparation : toolPlan.Preparation,
            BuildTestDeferredToBatch = deferBuildTest && hasModelReview,
            TestLevel = deferBuildTest && hasModelReview ? testLevel : null,
            TestSelectionDigest = deferBuildTest && hasModelReview ? selectionDigest : null,
            RequiredAspects = commands
                .Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            BriefSha256 = isConcept ? BriefHash(task.FolderPath) : BriefVersionStore.ReadOrCreate(task.FolderPath),
            SkippedAspects = skipped,
            TaskMode = isConcept ? TaskModes.Concept : null,
        };
        if (isConcept) return plan;
        plan = ApplyScopedReview(
            plan,
            task,
            repositoryPath,
            projectSettings,
            integrationRef,
            expectedResultSha);
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

    /// <summary>
    /// The engine-decided deep review (AGT-3268). The decision is frozen per
    /// result SHA; only a dispatched decision (<c>active</c> mode) adds the
    /// read-only agent command at the decided model and reasoning level. Any
    /// other decision is named in the skipped aspects with its reason.
    /// </summary>
    private void AddDeepReview(
        TaskInfo task,
        string? repositoryPath,
        string? integrationRef,
        string? resultRef,
        string? resultSha,
        IReadOnlyList<string>? changedPaths,
        AspectRunInputs inputs,
        List<Contract.ReviewCommandDto> commands,
        List<Contract.ReviewSkippedAspectDto> skipped)
    {
        if (_deepReview?.Decide(task, repositoryPath, integrationRef, resultRef, resultSha, changedPaths)
            is not { } entry)
            return;
        var decision = entry.Decision;
        if (!decision.Dispatch || decision.Model is null)
        {
            skipped.Add(new(DeepReviewAspect.Id, decision.Action == DeepReviewActions.Run
                ? $"Shadow mode: the engine would run a deep review at {decision.Model}/{decision.ThinkingLevel}; {decision.Reason}"
                : decision.Reason));
            return;
        }
        var prompt = _aspects.BuildAspectPrompt(DeepReviewAspect.Definition, inputs, decision.Model,
            pipelineStepId: DeepReviewAspect.StepId);
        var budget = ReviewAspectTimeoutPolicy.Derive(entry.CliType, decision.Model, decision.ThinkingLevel,
            Contract.ReviewAspectBudgetPolicy.MaterialCharacters(prompt), _configuration);
        commands.Add(new Contract.ReviewCommandDto(
            DeepReviewAspect.StepId,
            DeepReviewAspect.Id,
            entry.CliType,
            [],
            TimeoutSeconds: budget.Seconds,
            ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
            Prompt: prompt,
            CliType: entry.CliType,
            Model: decision.Model,
            ThinkingLevel: decision.ThinkingLevel));
    }

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

    private static string? BriefHash(string folder)
    {
        var path = Path.Combine(folder, "prompt.md");
        return File.Exists(path) ? AttemptAuthorityService.Hash(File.ReadAllText(path)) : null;
    }

    private static Contract.ReviewPlanDto ApplyScopedReview(
        Contract.ReviewPlanDto plan,
        TaskInfo task,
        string? repositoryPath,
        ProjectSettings? settings,
        string? integrationRef,
        string? expectedResultSha)
    {
        var ledger = ReviewConcernRoundStore.Read(task.FolderPath);
        if (ledger is not { StillOpen: true, PreviousVerdicts.Count: > 0 }
            || string.IsNullOrWhiteSpace(ledger.ReviewedResultSha)
            || string.IsNullOrWhiteSpace(ledger.ReviewedIntegrationTipSha))
            return plan;

        plan = plan with
        {
            ScopedReview = new Contract.ScopedReviewPlanDto(
                settings?.ScopedReviewAfterFinding ?? true,
                ledger.ReviewAttemptId,
                ledger.ReviewedResultSha,
                ledger.ReviewedIntegrationTipSha,
                Math.Max(0, settings?.ScopedReviewMaximumDeltaFiles ?? 20),
                ledger.AspectIds,
                ledger.PreviousVerdicts),
        };
        if (string.IsNullOrWhiteSpace(repositoryPath)
            || string.IsNullOrWhiteSpace(expectedResultSha))
            return plan;

        var semanticCommands = plan.Commands
            .Where(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .ToArray();
        var previous = ledger.PreviousVerdicts
            .Where(verdict => semanticCommands.Any(command =>
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
            Enabled: settings?.ScopedReviewAfterFinding ?? true,
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
            // A dispatched deep review was decided for this delivery; it is never carried over.
            if (!Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                || DeepReviewAspect.Matches(command.Aspect)
                || !decisions.TryGetValue(command.Aspect, out var decision)
                || decision.Run
                || !previous.TryGetValue(command.Aspect, out var priorVerdict))
            {
                if (Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                    && ledger.AspectIds.Contains(command.Aspect, StringComparer.OrdinalIgnoreCase)
                    && previous.TryGetValue(command.Aspect, out var raised))
                {
                    command = command with
                    {
                        Prompt = (command.Prompt ?? string.Empty)
                                 + $"\n\n## Previous finding to verify\n\nThis aspect raised the previous finding below in review {ledger.ReviewAttemptId}. Verify that it is fixed and that the fix introduced no new issue.\n\n- Previous status: {raised.Status}\n- Previous summary: {raised.Summary}\n- Previous evidence: {raised.EvidenceChecked ?? "not recorded"}\n- Previous finding: {raised.Missing ?? "not recorded"}",
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
            using var process = Process.Start(start);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? stdout.Trim() : null;
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

    private static AspectRunInputs Inputs(TaskInfo task, string? integrationRef)
    {
        var briefPath = Path.Combine(task.FolderPath, "prompt.md");
        var taskBody = Read(briefPath, 64_000, task.Id);
        var recentLog = Read(Path.Combine(task.FolderPath, "cli-output.log"), 16_000, string.Empty);
        var status = Read(Path.Combine(task.FolderPath, "status.md"), 16_000, string.Empty);
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
            status)
        {
            ResultsInventory = ResultsInventory.Render(task.FolderPath),
            CardMode = ReviewCardMode.Describe(task.Mode),
            AcceptanceScope = RequirementAcceptanceScope.Describe(
                task.AcceptanceScope,
                taskBody),
        };
    }

    private static string Read(string path, int maximumCharacters, string fallback)
    {
        if (!File.Exists(path)) return fallback;
        // Only the tail is kept, so only the tail is read (AGT-2991).
        return BoundedFileRead.ReadTailChars(path, maximumCharacters);
    }
}
