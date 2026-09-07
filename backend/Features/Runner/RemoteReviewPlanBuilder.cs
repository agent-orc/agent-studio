using AgentStudio.Pipeline;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Builds deterministic tool gates and declarative read-only semantic aspects
/// for a ReviewSubject. Agent aspects retain their step and aspect identities;
/// their CLI route is resolved from current settings immediately before claim.
/// The runner receives only the resulting immutable effective command plan for
/// the exact result SHA covered by the ReviewAttempt lease.
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
            var prompt = _aspects.BuildAspectPrompt(
                definition,
                inputs,
                model,
                PipelineStepConfigResolver.ResolvePrompt(settings, step.Id),
                step.Id);
            commands.Add(new Contract.ReviewCommandDto(
                step.Id,
                aspectId,
                string.Empty,
                [],
                TimeoutSeconds: timeoutSeconds,
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                Prompt: prompt));
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
    /// Re-resolves every agent-aspect command from current project settings.
    /// Stored CLI fields are deliberately ignored, which makes review attempts
    /// created by older servers follow a provider switch without being
    /// superseded. Deterministic tool commands remain exactly as authored.
    /// </summary>
    public Contract.ReviewPlanDto ResolveAgentCommandsForClaim(
        TaskInfo task,
        ProjectSettings? projectSettings,
        Contract.ReviewPlanDto sourcePlan,
        string? integrationRef)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(sourcePlan);

        var settings = PipelineTypeSettings.ForTask(projectSettings, task);
        var pipeline = PipelineCatalogue.ForTask(task);
        var defaultModel = _configuration.GetValue(
            "ReviewDecisionOrchestrator:AspectModel",
            PipelineStepModelDefaults.SupportModel);
        var defaultCli = ReviewDecisionOrchestrator.NormalizeReviewCliType(
            _configuration.GetValue(
                "ReviewDecisionOrchestrator:Cli",
                PipelineStepModelDefaults.DefaultCli));

        var commands = sourcePlan.Commands.Select(command =>
        {
            if (!Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
                return command;

            var stepId = string.IsNullOrWhiteSpace(command.StepId)
                ? $"aspect-{command.Aspect}"
                : command.StepId.Trim();
            var catalogueStep = pipeline.Post.FirstOrDefault(step =>
                string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
            var model = catalogueStep is null
                ? PipelineStepConfigResolver.ResolveModel(settings, stepId, defaultModel)
                : PipelineStepConfigResolver.ResolveModel(settings, catalogueStep, defaultModel);
            var cliType = catalogueStep is null
                ? PipelineStepConfigResolver.ResolveCliType(settings, stepId) ?? defaultCli
                : PipelineStepConfigResolver.ResolveCliType(settings, catalogueStep) ?? defaultCli;
            var thinking = catalogueStep is null
                ? PipelineStepConfigResolver.ResolveThinkingLevel(
                    settings,
                    stepId,
                    cliType,
                    model,
                    PipelineStepModelDefaults.SupportThinkingLevel)
                : PipelineStepConfigResolver.ResolveThinkingLevel(
                    settings,
                    catalogueStep,
                    cliType,
                    model,
                    PipelineStepModelDefaults.SupportThinkingLevel);

            return command with
            {
                StepId = stepId,
                FileName = cliType,
                Arguments = [],
                CliType = cliType,
                Model = model,
                ThinkingLevel = thinking,
            };
        }).ToArray();

        return sourcePlan with
        {
            Commands = commands,
            IntegrationRef = integrationRef ?? sourcePlan.IntegrationRef,
        };
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
}
