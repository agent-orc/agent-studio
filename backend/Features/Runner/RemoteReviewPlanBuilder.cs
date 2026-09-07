using AgentStudio.Pipeline;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Freezes deterministic tool gates and semantic aspect identities into the
/// ReviewSubject. Immediately before claim it rebuilds each aspect's current
/// route intent and resolves quota admission into an attempt-local executable
/// command. The runner receives only commands for the exact Result-SHA and
/// fenced ReviewAttempt lease it owns.
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

    private readonly AspectRunnerService _aspects;
    private readonly IConfiguration _configuration;

    public RemoteReviewPlanBuilder(
        AspectRunnerService aspects,
        IConfiguration configuration)
    {
        _aspects = aspects;
        _configuration = configuration;
    }

    public Contract.ReviewPlanDto Build(
        TaskInfo? task,
        string? repositoryPath,
        ProjectSettings? projectSettings,
        string? integrationRef)
    {
        var toolPlan = V1ReviewPlaneEndpoints.FallbackPlan(
            repositoryPath,
            projectSettings?.BuildProfile,
            integrationRef);
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
        var timeoutSeconds = Math.Clamp(
            _configuration.GetValue("ReviewDecisionOrchestrator:AspectTimeoutSeconds", 60),
            1,
            7200);

        var commands = toolPlan.Commands.ToList();
        foreach (var step in pipeline.Post.Where(step => step.Kind == StepKind.Aspect))
        {
            var aspectId = step.Id.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
                ? step.Id["aspect-".Length..]
                : step.Id;
            if (!configured.Contains(aspectId)
                || !AspectRunnerService.Catalogue.TryGetValue(aspectId, out var definition)
                || !PipelineStepConfigResolver.ShouldRun(settings, step, condition))
                continue;

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
            commands.Add(new Contract.ReviewCommandDto(
                step.Id,
                aspectId,
                cliType,
                [],
                TimeoutSeconds: timeoutSeconds,
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                Prompt: prompt,
                CliType: cliType,
                Model: model,
                ThinkingLevel: thinking));
        }

        return toolPlan with
        {
            Commands = commands,
            RequiredAspects = commands
                .Select(command => command.Aspect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    /// <summary>
    /// Resolves the executable route for every semantic command immediately
    /// before a ReviewAttempt is leased. The persisted subject remains the
    /// immutable description of what must be reviewed; its stable step/aspect
    /// identity is joined to current project settings, then quota admission
    /// selects the attempt-local CLI/model/thinking tuple.
    /// </summary>
    public RemoteReviewPlanAdmission ResolveForClaim(
        Contract.ReviewPlanDto authoredPlan,
        TaskInfo? task,
        string? repositoryPath,
        ProjectSettings? projectSettings,
        string? integrationRef,
        CliQuotaFallbackService? quotaFallback,
        CliQuotaCapsService quotaCaps,
        Func<string?, QuotaSnapshot?> snapshotFor,
        DateTime nowUtc,
        int occupiedSlots,
        ResolvedCliQuotaWaitPolicy? waitPolicy)
    {
        ArgumentNullException.ThrowIfNull(authoredPlan);
        ArgumentNullException.ThrowIfNull(quotaCaps);
        ArgumentNullException.ThrowIfNull(snapshotFor);

        // Rebuild only the route intent. Command membership remains owned by
        // the immutable subject, so disabling or adding an aspect later cannot
        // silently change what an already-open review is required to cover.
        var currentIntents = Build(task, repositoryPath, projectSettings, integrationRef)
            .Commands
            .Where(command => Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .GroupBy(CommandIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var decisions = new List<RemoteReviewCommandAdmission>();
        var commands = new List<Contract.ReviewCommandDto>(authoredPlan.Commands.Count);
        var deferred = false;

        foreach (var command in authoredPlan.Commands)
        {
            if (!Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            {
                commands.Add(command);
                continue;
            }

            var intent = currentIntents.GetValueOrDefault(CommandIdentity(command)) ?? command;
            var requestedCli = string.IsNullOrWhiteSpace(intent.CliType)
                ? intent.FileName
                : intent.CliType!;
            var admission = QuotaAdmissionPlanner.Plan(
                requestedCli,
                intent.Model,
                intent.ThinkingLevel,
                quotaFallback,
                quotaCaps,
                snapshotFor,
                nowUtc,
                occupiedSlots,
                waitPolicy,
                QuotaAdmissionContext.ForTask(
                    QuotaExecutionPath.ReviewAspect,
                    task?.TaskType,
                    intent.ThinkingLevel));
            decisions.Add(new RemoteReviewCommandAdmission(
                command.StepId,
                command.Aspect,
                requestedCli,
                intent.Model,
                intent.ThinkingLevel,
                admission));

            if (!admission.ShouldLaunch)
            {
                deferred = true;
                commands.Add(command);
                continue;
            }

            commands.Add(command with
            {
                FileName = admission.CliType,
                // Claim-time admission may change only the execution route.
                // Keep the authored prompt frozen with the immutable subject
                // so edits to task files cannot change an open review.
                Prompt = command.Prompt,
                CliType = admission.CliType,
                Model = admission.Model ?? intent.Model,
                ThinkingLevel = admission.ThinkingLevel ?? intent.ThinkingLevel,
            });
        }

        return new RemoteReviewPlanAdmission(
            deferred ? null : authoredPlan with { Commands = commands },
            decisions);
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
        var taskBody = Read(Path.Combine(task.FolderPath, "prompt.md"), 64_000, task.Id);
        var recentLog = Read(Path.Combine(task.FolderPath, "cli-output.log"), 16_000, string.Empty);
        var status = Read(Path.Combine(task.FolderPath, "status.md"), 16_000, string.Empty);
        var baseline = string.IsNullOrWhiteSpace(integrationRef) ? "the configured integration ref" : integrationRef;
        var diff =
            $"This aspect runs on a remote Review Executor at the immutable Result-SHA. " +
            $"Inspect the materialized repository directly and compare HEAD with {baseline}; " +
            "do not infer the change from the Studio checkout.";
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
        var value = File.ReadAllText(path);
        return value.Length <= maximumCharacters
            ? value
            : value[^maximumCharacters..];
    }

    private static string CommandIdentity(Contract.ReviewCommandDto command)
        => $"{command.StepId}\u001f{command.Aspect}";
}

public sealed record RemoteReviewPlanAdmission(
    Contract.ReviewPlanDto? EffectivePlan,
    IReadOnlyList<RemoteReviewCommandAdmission> Commands)
{
    public bool ShouldLaunch => EffectivePlan is not null;

    public QuotaAdmissionPlan? DeferredDecision => Commands
        .Select(command => command.Decision)
        .FirstOrDefault(decision => !decision.ShouldLaunch);
}

public sealed record RemoteReviewCommandAdmission(
    string StepId,
    string Aspect,
    string RequestedCliType,
    string? RequestedModel,
    string? RequestedThinkingLevel,
    QuotaAdmissionPlan Decision);
