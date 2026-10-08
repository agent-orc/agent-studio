using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.Git;
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

    private readonly AspectRunnerService _aspects;
    private readonly IConfiguration _configuration;
    private readonly GitService? _git;

    public RemoteReviewPlanBuilder(
        AspectRunnerService aspects,
        IConfiguration configuration,
        GitService? git = null)
    {
        _aspects = aspects;
        _configuration = configuration;
        _git = git;
    }

    /// <param name="changedFiles">
    /// The delivery's paths against its merge base, when known. A diff that
    /// can change the Compose stack adds the Compose-render steps, whose host
    /// requirement routes the review to an executor that can render Compose
    /// (AGT-2981). Null keeps the diff-independent plan.
    /// </param>
    public Contract.ReviewPlanDto Build(
        TaskInfo? task,
        string? repositoryPath,
        ProjectSettings? projectSettings,
        string? integrationRef,
        string? expectedResultSha = null,
        IReadOnlyList<string>? changedFiles = null)
    {
        var toolPlan = V1ReviewPlaneEndpoints.FallbackPlan(
            repositoryPath,
            projectSettings?.BuildProfile,
            integrationRef,
            changedFiles);
        var ordinaryToolPlan = toolPlan;
        var subject = task is null ? null : ReviewSubjectStore.Read(task.FolderPath);
        var changedPaths = changedFiles ?? (_git is null || string.IsNullOrWhiteSpace(repositoryPath)
            || string.IsNullOrWhiteSpace(integrationRef) || subject is null
            ? null
            : _git.ChangedPathsAgainstMergeBase(
                repositoryPath, integrationRef,
                subject.ImmutableResultRef ?? subject.ResultSha));
        var deferBuildTest = projectSettings?.BatchGate.Enabled == true
            && task is not null && !TaskModes.IsReportOnly(task.Mode)
            && DocsOnlyDeliveryPolicy.IsDocsOnly(changedPaths)
            && subject is { ImmutableResultRef: not null }
            && ReviewSubjectStore.IsValidResultSha(subject.ResultSha);
        if (deferBuildTest)
            toolPlan = toolPlan with
            {
                Commands = toolPlan.Commands.Where(command =>
                    !string.Equals(command.Aspect, "build-tests", StringComparison.OrdinalIgnoreCase)).ToArray(),
                Preparation = [],
                BuildTestDeferredToBatch = true,
            };
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

        var hasModelReview = commands.Any(command =>
            Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind));
        if (deferBuildTest && !hasModelReview)
            commands = ordinaryToolPlan.Commands.ToList();

        var plan = toolPlan with
        {
            Commands = commands,
            Preparation = deferBuildTest && !hasModelReview
                ? ordinaryToolPlan.Preparation : toolPlan.Preparation,
            BuildTestDeferredToBatch = deferBuildTest && hasModelReview,
            RequiredAspects = commands
                .Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            BriefSha256 = isConcept ? BriefHash(task.FolderPath) : BriefVersionStore.ReadOrCreate(task.FolderPath),
            SkippedAspects = skipped,
            TaskMode = isConcept ? TaskModes.Concept : null,
        };
        if (isConcept) return plan;
        return ApplyScopedReview(
            plan,
            task,
            repositoryPath,
            projectSettings,
            integrationRef,
            expectedResultSha);
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
            if (!Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
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
