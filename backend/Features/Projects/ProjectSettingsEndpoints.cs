

namespace AgentStudio.Projects;

using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Security;

/// <summary>
/// Body for <c>PUT /api/projects/{name}/intake</c>. Enables or disables the
/// orchestrator-intake gate. See <see cref="ProjectSettings.IntakeEnabled"/>.
/// </summary>
public record SetIntakeEnabledRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Body for <c>POST /api/tasks/{jobId}/intake</c>. Optional explicit watch
/// path so a job id that exists in multiple workspaces resolves to the
/// caller's project.
/// </summary>
public record IntakeRunRequestBody
{
    public string WatchPath { get; init; } = "";
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/pipeline-step-order</c>. Stores the
/// project-specific order of configurable pre/post pipeline steps.
/// </summary>
public record SetPipelineStepOrderRequest
{
    public string PipelineType { get; init; } = PipelineTypes.Task;
    public List<string> StepIds { get; init; } = [];
}

/// <summary>Body for project-level boolean feature toggles.</summary>
public record SetCrashRecoveryRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Per-project preferences under <c>/api/projects</c> — read-all
/// for the header bar plus the per-project auto-commit toggle.
/// </summary>
public static class ProjectSettingsEndpoints
{
    public static void MapProjectSettingsEndpoints(this WebApplication app)
    {
        // Per-project preferences (auto-commit on/off today). Read-all returns a
        // flat map keyed by project name so the header can render every toggle
        // in one shot without N round-trips.
        app.MapGet("/api/projects/settings", (
            HttpContext context,
            ProjectSettingsService settings,
            ProjectRegistry projects,
            OrchestratorDefaultsProvider defaults) =>
        {
            var all = settings.GetAll().AsEnumerable();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
                all = all.Where(kv => ProjectAccessAuthorization.Allows(human.User, kv.Key, projects));
            var projected = all.ToDictionary(
                kv => kv.Key,
                kv => new
                {
                    autoCommit = kv.Value.AutoCommit,
                    crashRecoveryEnabled = kv.Value.CrashRecoveryEnabled,
                    autoPushStrategy = AutoPushStrategies.Normalize(kv.Value.AutoPushStrategy),
                    runnerMode = kv.Value.RunnerMode,
                    pickupMode = ProjectExecutionPolicy.ResolvePickupMode(kv.Value),
                    executionLocation = ProjectExecutionPolicy.ResolveExecutionLocation(kv.Value),
                    executionRunner = kv.Value.ExecutionRunner,
                    remoteExecutionEnabled = kv.Value.RemoteExecutionEnabled,
                    orchestratorModel = kv.Value.OrchestratorModel,
                    orchestratorThinkingLevel = kv.Value.OrchestratorThinkingLevel,
                    cliExecutionEngine = defaults.ResolveCliExecutionEngine(kv.Key).ExecutionEngine,
                    cliExecutionEngineSource = defaults.ResolveCliExecutionEngine(kv.Key).Source,
                    cliExecutionEngineOverride = kv.Value.CliExecutionEngine,
                    // Epic decomposition (planning) run knobs (way 3): null
                    // model means "use the epic card's own model"; subTasksToReady
                    // null/false lands generated sub-tasks in 0-backlog.
                    epicPlanningModel = kv.Value.EpicPlanningModel,
                    epicPlanningThinkingLevel = kv.Value.EpicPlanningThinkingLevel,
                    epicSubTasksToReady = kv.Value.EpicSubTasksToReady ?? false,
                    intakeEnabled = kv.Value.IntakeEnabled,
                    autonomyLevel = kv.Value.AutonomyLevel,
                    waitOnQuotaEnabled = kv.Value.WaitOnQuotaEnabled,
                    waitOnQuotaThresholdMinutes = kv.Value.WaitOnQuotaThresholdMinutes,
                    // ADR-0052: parallel-execution knobs. maxParallelism == 1
                    // means the runner stays sequential; the branch/strategy
                    // pair only matters once it is raised above 1.
                    maxParallelism = kv.Value.MaxParallelism < 1 ? 1 : kv.Value.MaxParallelism,
                    integrationBranch = kv.Value.IntegrationBranch,
                    integrationStrategy = IntegrationStrategies.Normalize(kv.Value.IntegrationStrategy),
                    // Slice P (ASS-1663): per-project build profile + onboarding
                    // status. Null when the project never declared one (legacy
                    // "no gate" behaviour). pickupAllowed mirrors the runner's
                    // BuildProfileGate so the UI can show why a declared-but-
                    // unvalidated project is not being picked up.
                    buildProfile = kv.Value.BuildProfile,
                    buildProfilePickupAllowed = BuildProfileGate.AllowsAutoPickup(kv.Value.BuildProfile),
                    // F35: resolved per-lane strategy map (defaults filled in).
                    // The board uses this for the lane-header icon + the
                    // drag-disabled hint without a per-project round-trip.
                    laneSortStrategies = TaskStates.All.ToDictionary(
                        lane => lane,
                        lane => LaneSortStrategies.Resolve(kv.Value, lane),
                        StringComparer.OrdinalIgnoreCase),
                    // Per-step pipeline overrides (enabled / mode / model).
                    // Only the steps the operator has touched appear here;
                    // an absent step is on its built-in default.
                    pipelineSteps = PipelineTypeSettings.ForType(kv.Value, PipelineTypes.Task)?.PipelineSteps
                        ?? new Dictionary<string, PipelineStepSetting>(),
                    testExecution = kv.Value.TestExecution,
                    pipelineStepOrder = PipelineTypeSettings.ForType(kv.Value, PipelineTypes.Task)?.PipelineStepOrder
                        ?? Array.Empty<string>(),
                    pipelineStepsByType = kv.Value.PipelineStepsByType
                        ?? new Dictionary<string, Dictionary<string, PipelineStepSetting>>(),
                    pipelineStepOrderByType = kv.Value.PipelineStepOrderByType
                        ?? new Dictionary<string, IReadOnlyList<string>>(),
                    // Resolved per-CLI permission mode + source for all four CLIs
                    // (project override → detected global config → YOLO default).
                    // The project-settings UI uses this to render the effective
                    // mode without a per-CLI round-trip.
                    cliModes = CliTypes.All.ToDictionary(
                        cli => cli,
                        cli =>
                        {
                            var r = settings.ResolveCliMode(kv.Key, cli);
                            return new { mode = r.Mode, source = r.Source, args = r.Args };
                        },
                        StringComparer.OrdinalIgnoreCase),
                    // Resolved per-CLI context mode + source + clean-support for
                    // all four CLIs (project override → CLEAN default). T1b /
                    // ASS-1742: the settings UI renders the effective mode and
                    // greys clean out for shared-only CLIs from this without a
                    // per-CLI round-trip.
                    cliContextModes = CliTypes.All.ToDictionary(
                        cli => cli,
                        cli =>
                        {
                            var r = settings.ResolveContextMode(kv.Key, cli);
                            return new { mode = r.Mode, source = r.Source, supported = r.Supported };
                        },
                        StringComparer.OrdinalIgnoreCase),
                },
                StringComparer.OrdinalIgnoreCase);
            return Results.Ok(projected);
        });

        // The configurable pipeline-step catalogue: the code-defined steps a
        // project can enable/disable, set a model on, or set a gate mode on.
        // The Settings panel reads this to render one control per step
        // without hardcoding the step list on the frontend.
        app.MapGet("/api/projects/pipeline-catalogue", (
            string? projectName,
            string? pipelineType,
            ProjectSettingsService settings,
            TaskScannerService scanner,
            AgentStudio.ModelMigrations.ModelMigrationCoordinator modelMigrations) =>
        {
            if (!string.IsNullOrWhiteSpace(pipelineType) && !PipelineTypes.IsValid(pipelineType))
                return Results.BadRequest(new { error = $"Unknown pipeline type '{pipelineType}'" });
            var type = PipelineTypes.Normalize(pipelineType);
            ProjectSettings? projectSettings = null;
            WatchPathEntry? project = null;
            var repositoryPath = "";
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                project = scanner.GetWatchPaths().FirstOrDefault(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
                if (project is null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                projectSettings = PipelineTypeSettings.ForType(settings.Get(projectName.Trim()), type);
                repositoryPath = string.IsNullOrWhiteSpace(project.RepositoryPath) ? project.RootPath : project.RepositoryPath;
            }
            var detectedStacks = ProjectStackDetector.Detect(repositoryPath);

            var pipeline = PipelineCatalogue.ForType(type);
            var cataloguePipelines = type == PipelineTypes.Planning
                ? new[] { pipeline }
                : PipelineCatalogue.All.Where(candidate =>
                    !string.Equals(candidate.Id, PipelineCatalogue.ReadOnlyPipelineId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(candidate.Id, PipelineCatalogue.ConceptPipelineId, StringComparison.OrdinalIgnoreCase));
            var catalogueSteps = cataloguePipelines
                .SelectMany(p => p.Pre.Select(s => (Step: s, Phase: "pre", PipelineId: p.Id))
                    .Concat(p.Core.Select(s => (Step: s, Phase: "core", PipelineId: p.Id)))
                    .Concat(p.Post.Select(s => (Step: s, Phase: PhaseForPostStep(s), PipelineId: p.Id))))
                .GroupBy(x => x.Step.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var steps = catalogueSteps
                .Select(x => ProjectPipelineStepDto(x.Step, x.Phase, x.PipelineId))
                .ToList();

            static string PhaseForPostStep(PipelineStep step)
            {
                if (step.Kind == StepKind.Aspect) return "aspect";
                if (step.Kind == StepKind.Tool) return "tool";
                if (step.Kind == StepKind.Analysis) return "analysis";
                if (step.Kind == StepKind.Drift) return "drift";
                if (string.Equals(step.Id, PipelineCatalogue.OrchestratorDecisionStepId, StringComparison.OrdinalIgnoreCase))
                    return "decision";
                return "post";
            }

            object ProjectPipelineStepDto(PipelineStep s, string phase, string? pipelineId = null)
            {
                var resolved = PipelineStepModelDefaults.Resolve(projectSettings, s);
                var configured = PipelineStepConfigResolver.Lookup(projectSettings, s.Id);
                var cliType = configured?.CliType ?? s.CliType ?? PipelineStepModelDefaults.RuntimeDefaultCliFor(s);
                var thinking = resolved is null
                    ? null
                    : PipelineStepConfigResolver.ResolveThinkingLevelWithSource(
                        projectSettings,
                        s,
                        cliType,
                        resolved.Model,
                        PipelineStepModelDefaults.RuntimeDefaultThinkingLevelFor(s));
                var execution = PipelineStepExecutionResolver.Resolve(s, repositoryPath, projectSettings);
                return new
                {
                    id = s.Id,
                    pipelineId,
                    displayName = s.DisplayName,
                    kind = s.Kind.ToString(),
                    appliesTo = s.AppliesTo,
                    applicable = ProjectStackDetector.Applies(s.AppliesTo, detectedStacks),
                    effectiveExecution = execution,
                    phase,
                    runMode = s.RunMode.ToString(),
                    dependsOn = s.DependsOn,
                    idempotent = s.Idempotent,
                    stub = s.Stub,
                    deferred = s.Deferred,
                    model = s.Model,
                    resolvedModel = resolved?.Model,
                    modelSource = resolved?.Source,
                    modelMigration = string.IsNullOrWhiteSpace(configured?.Model)
                        ? null
                        : modelMigrations.GetProposal(configured.Model),
                    resolvedThinkingLevel = thinking?.ThinkingLevel,
                    thinkingLevelSource = thinking?.Source,
                    // The core agent run cannot be disabled or model-overridden
                    // here (it uses the task's own CLI + model). Only steps that
                    // the runtime actually resolves through PipelineStepConfigResolver
                    // expose the shared CLI/model/thinking controls.
                    usesModel = PipelineStepModelDefaults.UsesModel(s),
                    supportsEconomyModel = s.Kind == StepKind.Aspect,
                    usesPrompt = PipelineStepModelDefaults.UsesModel(s),
                    supportsMode = s.Kind is StepKind.Tool or StepKind.Orchestrator,
                    cliType,
                    framework = s.Framework ?? s.AppliesTo,
                    promptTemplate = s.PromptTemplate,
                    // The loop guard is a safety net that always runs (the
                    // StuckLoopGuard circuit-breaker fires regardless of this row);
                    // the pipeline step only mirrors its state, so it is not an
                    // opt-out toggle - making it disable-able would let a project
                    // hide a loop the breaker still acts on.
                    canDisable = PipelineStepConfigResolver.CanDisable(s),
                    // The drift post-steps default off (opt-in); every other step
                    // defaults on. The Settings UI uses this to render the toggle's
                    // initial state when the project has no explicit override.
                    defaultEnabled = s.DefaultEnabled,
                    supportsCondition = s.Kind is not (StepKind.Core or StepKind.Analysis),
                    supportsMaxIterations = string.Equals(s.Id, PipelineCatalogue.UiPipelineRoutingStepId, StringComparison.OrdinalIgnoreCase),
                    defaultMaxIterations = string.Equals(s.Id, PipelineCatalogue.UiPipelineRoutingStepId, StringComparison.OrdinalIgnoreCase)
                        ? UiIterationGate.DefaultMaxIterations
                        : (int?)null,
                };
            }

            // The abort-triggered review step lives off the linear AllSteps list
            // (it only fires after a non-clean run end) but is configurable
            // through the same per-project override mechanism.
            var abort = PipelineCatalogue.AbortReviewStep;
            steps.Add(ProjectPipelineStepDto(abort, "abort"));

            return Results.Ok(new { pipelineId = pipeline.Id, pipelineType = type, detectedStacks, steps });
        });

        app.MapPost("/api/projects/{projectName}/pipeline-steps/{stepId}/probe", async (
            string projectName,
            string stepId,
            TaskScannerService scanner,
            PipelineStepProbeService probes,
            HttpContext context) =>
        {
            var project = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var step = PipelineCatalogue.FindStep(stepId);
            if (step is null) return Results.NotFound(new { error = $"Unknown pipeline step '{stepId}'" });
            var repositoryPath = string.IsNullOrWhiteSpace(project.RepositoryPath)
                ? project.RootPath
                : project.RepositoryPath;
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                return Results.BadRequest(new { error = "Project repository path is unavailable." });

            var result = await probes.RunAsync(projectName, repositoryPath, step, context.RequestAborted);
            return Results.Ok(result);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        // Per-project pipeline-step override. Sets enabled / mode / model for
        // one step; an all-null body clears the override (revert to default).
        app.MapPut("/api/projects/{projectName}/pipeline-step", (string projectName, SetPipelineStepRequest req, ProjectSettingsService settings, TaskScannerService scanner, RuntimePromptService prompts) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (string.IsNullOrWhiteSpace(req.StepId))
                return Results.BadRequest(new { error = "stepId is required" });
            if (!PipelineTypes.IsValid(req.PipelineType))
                return Results.BadRequest(new { error = $"Unknown pipeline type '{req.PipelineType}'" });
            var pipelineType = PipelineTypes.Normalize(req.PipelineType);

            // Reject step ids the catalogue does not know so a typo fails loud
            // instead of writing dead config that never reaches a real step. The
            // abort-review step lives off the linear AllSteps list but is a valid
            // configurable target, so accept it explicitly.
            if (!IsKnownPipelineStep(req.StepId, pipelineType))
                return Results.BadRequest(new { error = $"Unknown pipeline step '{req.StepId}'" });
            if (PipelineStepConfigResolver.IsRepositoryOwnedAnalysisStep(req.StepId))
                return Results.BadRequest(new
                {
                    error = $"Analysis step '{req.StepId}' is configured only by .quality/agent-studio.json in the project repository.",
                });

            if (!string.IsNullOrWhiteSpace(req.Mode) && PostStepConfigResolver.ParseMode(req.Mode) is null)
                return Results.BadRequest(new { error = $"Unsupported mode '{req.Mode}' (expected off / warn / fail)" });

            if (req.MaxIterations is < UiIterationGate.MinimumIterations or > UiIterationGate.MaximumIterations)
                return Results.BadRequest(new { error = $"maxIterations must be between {UiIterationGate.MinimumIterations} and {UiIterationGate.MaximumIterations}" });

            // Validate any run condition: the token must be known and
            // value-bearing tokens need a value. An "always" / blank condition
            // is a no-op and is left to normalize away in the service.
            if (req.Condition is { } condition && !string.IsNullOrWhiteSpace(condition.When))
            {
                var when = PipelineStepConditions.Normalize(condition.When);
                if (when is null)
                    return Results.BadRequest(new { error = $"Unsupported condition '{condition.When}'" });

                if (when != PipelineStepConditions.Always
                    && PipelineStepConditions.RequiresValue(when)
                    && string.IsNullOrWhiteSpace(condition.Value))
                    return Results.BadRequest(new { error = $"Condition '{when}' requires a value" });
            }

            var existing = PipelineTypeSettings.ForType(settings.Get(projectName), pipelineType)?.PipelineSteps?
                .GetValueOrDefault(req.StepId);
            var normalizedPrompt = string.IsNullOrWhiteSpace(req.Prompt)
                ? null
                : req.Prompt.Trim();
            string? promptBaseDefaultSha = null;
            string? promptBaseDefaultContent = null;
            if (normalizedPrompt is not null)
            {
                var promptUnchanged = string.Equals(
                    existing?.Prompt?.Trim(),
                    normalizedPrompt,
                    StringComparison.Ordinal);
                if (promptUnchanged && existing?.PromptBaseDefaultSha is not null)
                {
                    promptBaseDefaultSha = existing.PromptBaseDefaultSha;
                    promptBaseDefaultContent = existing.PromptBaseDefaultContent;
                }
                else
                {
                    var promptName = PromptPipelineBindings.ForStep(req.StepId);
                    promptBaseDefaultContent = promptName is null
                        ? null
                        : prompts.TryReadDefault(promptName);
                    promptBaseDefaultSha = promptBaseDefaultContent is null
                        ? null
                        : RuntimePromptService.ContentSha(promptBaseDefaultContent);
                }
            }

            settings.SetPipelineStep(projectName, pipelineType, req.StepId, new PipelineStepSetting
            {
                Enabled = req.Enabled,
                EconomyModel = req.EconomyModel,
                MaxIterations = req.MaxIterations,
                Mode = req.Mode,
                CliType = req.CliType,
                Model = req.Model,
                ThinkingLevel = req.ThinkingLevel,
                Prompt = normalizedPrompt,
                PromptBaseDefaultSha = promptBaseDefaultSha,
                PromptBaseDefaultContent = promptBaseDefaultContent,
                Condition = req.Condition,
            });
            return Results.Ok(new
            {
                stepId = req.StepId,
                pipelineType,
                pipelineSteps = PipelineTypeSettings.ForType(settings.Get(projectName), pipelineType)?.PipelineSteps
                    ?? new Dictionary<string, PipelineStepSetting>(),
                pipelineStepsByType = settings.Get(projectName).PipelineStepsByType
                    ?? new Dictionary<string, Dictionary<string, PipelineStepSetting>>(),
            });
        });

        // Project-specific pre/post step order. The pipeline remains catalogue-
        // bounded: callers can reorder known step ids, while missing ids append
        // in catalogue order so a newly introduced step is never hidden.
        app.MapPut("/api/projects/{projectName}/pipeline-step-order", (string projectName, SetPipelineStepOrderRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            if (!PipelineTypes.IsValid(req.PipelineType))
                return Results.BadRequest(new { error = $"Unknown pipeline type '{req.PipelineType}'" });
            var pipelineType = PipelineTypes.Normalize(req.PipelineType);

            var unknown = (req.StepIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id) && !IsKnownPipelineStep(id, pipelineType))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unknown.Length > 0)
                return Results.BadRequest(new { error = $"Unknown pipeline step '{unknown[0]}'" });

            settings.SetPipelineStepOrder(projectName, pipelineType, req.StepIds);
            return Results.Ok(new
            {
                pipelineType,
                pipelineStepOrder = PipelineTypeSettings.ForType(settings.Get(projectName), pipelineType)?.PipelineStepOrder
                    ?? Array.Empty<string>(),
                pipelineStepOrderByType = settings.Get(projectName).PipelineStepOrderByType
                    ?? new Dictionary<string, IReadOnlyList<string>>(),
            });
        });

        app.MapPut("/api/projects/{projectName}/auto-commit", (string projectName, SetAutoCommitRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            // Reject unknown project names so a typo in the URL fails loud rather than silently
            // adding orphan settings entries that never reach a board column.
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetAutoCommit(projectName, req.Enabled);
            return Results.Ok(settings.Get(projectName));
        });

        app.MapPut("/api/projects/{projectName}/crash-recovery", (string projectName, SetCrashRecoveryRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetCrashRecoveryEnabled(projectName, req.Enabled);
            return Results.Ok(settings.Get(projectName));
        });

        app.MapPut("/api/projects/{projectName}/auto-push-strategy", (string projectName, SetAutoPushStrategyRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (!AutoPushStrategies.All.Contains(req.Strategy, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unsupported auto-push strategy '{req.Strategy}'" });

            var normalized = AutoPushStrategies.Normalize(req.Strategy);
            settings.SetAutoPushStrategy(projectName, normalized);
            return Results.Ok(settings.Get(projectName));
        });

        // Flag-gated local CLI execution engine. The effective value resolves
        // process environment -> project override -> workspace default -> CAR
        // platform default.
        app.MapGet("/api/projects/{projectName}/cli-execution-engine", (
            string projectName,
            OrchestratorDefaultsProvider defaults,
            TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var r = defaults.ResolveCliExecutionEngine(projectName);
            return Results.Ok(new
            {
                executionEngine = r.ExecutionEngine,
                source = r.Source,
                projectOverride = r.ProjectOverride,
                workspaceDefault = r.WorkspaceDefault,
                platformDefault = r.PlatformDefault,
                available = CliExecutionEngines.All,
            });
        });

        app.MapPut("/api/projects/{projectName}/cli-execution-engine", (
            string projectName,
            SetCliExecutionEngineRequest req,
            ProjectSettingsService settings,
            OrchestratorDefaultsProvider defaults,
            TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            if (!string.IsNullOrWhiteSpace(req.ExecutionEngine)
                && !CliExecutionEngines.IsValid(req.ExecutionEngine))
            {
                return Results.BadRequest(new
                {
                    error = $"Unsupported CLI execution engine '{req.ExecutionEngine}'",
                });
            }

            settings.SetCliExecutionEngine(projectName, req.ExecutionEngine);
            var r = defaults.ResolveCliExecutionEngine(projectName);
            return Results.Ok(new
            {
                executionEngine = r.ExecutionEngine,
                source = r.Source,
                projectOverride = r.ProjectOverride,
                workspaceDefault = r.WorkspaceDefault,
                platformDefault = r.PlatformDefault,
                available = CliExecutionEngines.All,
            });
        });

        // Per-project CLI permission modes. GET returns the resolved mode +
        // source + rendered flags for every CLI (defaults filled in) so the
        // settings UI can render the effective state and dropdown in one shot.
        app.MapGet("/api/projects/{projectName}/cli-modes", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            var resolved = CliTypes.All.ToDictionary(
                cli => cli,
                cli =>
                {
                    var r = settings.ResolveCliMode(projectName, cli);
                    return new { mode = r.Mode, source = r.Source, args = r.Args };
                },
                StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                resolved,
                overrides = s.CliModes ?? new Dictionary<string, string>(),
                available = CliPermissionModes.UserVisible,
            });
        });

        // PUT one CLI's permission mode. An empty/null mode clears the override
        // (revert to the platform default / global config). Takes effect on the
        // next spawn without a backend restart (ProjectRunner resolves live).
        app.MapPut("/api/projects/{projectName}/cli-mode", (string projectName, SetCliModeRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (!CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"Unknown CLI '{req.CliType}'" });
            // Empty clears; a non-empty value must be a known mode.
            if (!string.IsNullOrWhiteSpace(req.Mode) && !CliPermissionModes.IsValid(req.Mode))
                return Results.BadRequest(new { error = $"Unsupported permission mode '{req.Mode}'" });

            settings.SetCliMode(projectName, req.CliType, req.Mode);
            var r = settings.ResolveCliMode(projectName, req.CliType);
            return Results.Ok(new { cli = r.CliType, mode = r.Mode, source = r.Source, args = r.Args });
        });

        // Effective-mode probe (ticket test path). Returns the mode a spawn of
        // <name> in <project> would use right now, its source, and the exact
        // flags the driver would inject — the reload-able check the E2E uses to
        // confirm a UI toggle reaches the resolver.
        app.MapGet("/api/cli/{name}/effective-mode", (string name, string? project, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            if (!CliTypes.IsValid(name))
                return Results.BadRequest(new { error = $"Unknown CLI '{name}'" });
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "query parameter 'project' is required" });

            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, project, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{project}'" });

            var r = settings.ResolveCliMode(project, name);
            return Results.Ok(new
            {
                cli = r.CliType,
                project,
                mode = r.Mode,
                source = r.Source,
                args = r.Args,
            });
        });

        // T1b / ASS-1742: per-project CLI context modes. GET returns the
        // resolved mode + source + clean-support for every CLI (CLEAN default
        // filled in) so the settings UI renders the effective state, the
        // dropdown, and the shared-only greying in one shot.
        app.MapGet("/api/projects/{projectName}/cli-context-modes", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            var resolved = CliTypes.All.ToDictionary(
                cli => cli,
                cli =>
                {
                    var r = settings.ResolveContextMode(projectName, cli);
                    return new { mode = r.Mode, source = r.Source, supported = r.Supported };
                },
                StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                resolved,
                overrides = s.CliContextModes ?? new Dictionary<string, string>(),
                available = CliContextModes.UserVisible,
            });
        });

        // PUT one CLI's context mode. An empty/null mode clears the override
        // (revert to the platform default CLEAN). Takes effect on the next spawn
        // without a backend restart (ProjectRunner resolves live).
        app.MapPut("/api/projects/{projectName}/cli-context-mode", (string projectName, SetCliContextModeRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (!CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"Unknown CLI '{req.CliType}'" });
            // Empty clears; a non-empty value must be a known mode.
            if (!string.IsNullOrWhiteSpace(req.Mode) && !CliContextModes.IsValid(req.Mode))
                return Results.BadRequest(new { error = $"Unsupported context mode '{req.Mode}'" });

            settings.SetCliContextMode(projectName, req.CliType, req.Mode);
            var r = settings.ResolveContextMode(projectName, req.CliType);
            return Results.Ok(new { cli = r.CliType, mode = r.Mode, source = r.Source, supported = r.Supported });
        });

        // ADR-0052: max number of tasks the runner runs concurrently for this
        // project. 1 (default) keeps it sequential; the value is clamped to
        // >= 1 server-side so a 0/negative body cannot stall the runner.
        //
        // DEPRECATED for remote execution (AGT-2302 / AGT-2376): a host ceiling
        // is the one source of truth there (PUT /api/clients/{id}/runner-capacity),
        // and this value only seeds it during migration. It is NOT dead: the
        // local ProjectRunner still limits itself by it through SlotMax() /
        // ParallelSlotPolicy, so removing the route left a live setting with no
        // way to change it. Remove after 2026-10-01, when the CAR rework of local
        // execution replaces it.
        app.MapPut("/api/projects/{projectName}/max-parallelism", (string projectName, SetMaxParallelismRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetMaxParallelism(projectName, req.MaxParallelism);
            return Results.Ok(settings.Get(projectName));
        });

        // Compatibility route for both the old composite executionRunner field
        // and the canonical pickupMode + executionLocation pair.
        app.MapPut("/api/projects/{projectName}/execution-runner", (string projectName, SetExecutionRunnerRequest req,
            ProjectSettingsService settings, TaskScannerService scanner, ClientIdentityStore clients,
            TaskRunnerService runners) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            try
            {
                string? pickupMode = req.PickupMode;
                string? executionLocation = req.ExecutionLocation;

                if (pickupMode is not null && !PickupModes.IsValid(pickupMode))
                    return Results.BadRequest(new { error = $"Unsupported pickup mode '{pickupMode}'. Allowed: auto, manual, paused." });

                if (req.ExecutionLocation is null
                    && req.ExecutionRunner is null
                    && pickupMode is not null)
                {
                    executionLocation = null;
                }
                else if (req.ExecutionLocation is not null)
                {
                    executionLocation = ExecutionRunnerAssignment.NormalizeAndValidate(req.ExecutionLocation, clients)
                                        ?? ExecutionLocations.Local;
                }
                else if (ProjectExecutionPolicy.IsLegacyComposite(req.ExecutionRunner))
                {
                    pickupMode = string.Equals(req.ExecutionRunner, "auto-continuous", StringComparison.OrdinalIgnoreCase)
                        ? PickupModes.Auto
                        : PickupModes.Normalize(req.ExecutionRunner);
                    executionLocation = string.Equals(req.ExecutionRunner, "auto-continuous", StringComparison.OrdinalIgnoreCase)
                        ? ExecutionLocations.Local
                        : null;
                }
                else
                {
                    executionLocation = ExecutionRunnerAssignment.NormalizeAndValidate(req.ExecutionRunner, clients)
                                        ?? ExecutionLocations.Local;
                    if (executionLocation != ExecutionLocations.Local
                        && req.RemoteExecutionEnabled != false)
                        pickupMode ??= PickupModes.Auto;
                }

                settings.SetExecutionSettings(
                    projectName,
                    pickupMode,
                    executionLocation,
                    req.RemoteExecutionEnabled);

                if (pickupMode is not null)
                    runners.RequestModeChange(
                        projectName,
                        PickupModes.ToRunnerMode(pickupMode),
                        "api: PUT /api/projects/{projectName}/execution-runner");
                return Results.Ok(settings.Get(projectName));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ProjectPersistenceException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // ADR-0052: integration branch parallel task worktrees branch off and
        // merge back into. Blank reverts to repository origin/HEAD.
        app.MapPut("/api/projects/{projectName}/integration-branch", (string projectName, SetIntegrationBranchRequest req,
            ProjectSettingsService settings, TaskScannerService scanner, ProjectRegistry projects,
            ClientIdentityStore clients) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetIntegrationBranch(projectName, req.Branch);
            var project = projects.FindByIdOrDisplayName(projectName);
            if (project is not null)
                clients.InvalidateRunnerProjectPreflights(project.Id);
            return Results.Ok(settings.Get(projectName));
        });

        // ADR-0052: how a finished task branch folds back into the integration
        // branch (direct-merge default, or pull-request).
        app.MapPut("/api/projects/{projectName}/integration-strategy", (string projectName, SetIntegrationStrategyRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (!IntegrationStrategies.All.Contains(req.Strategy, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unsupported integration strategy '{req.Strategy}'" });

            settings.SetIntegrationStrategy(projectName, req.Strategy);
            return Results.Ok(settings.Get(projectName));
        });

        // Slice P (ASS-1663): per-project build profile + onboarding.
        // GET returns the declared profile (or null), the resolved onboarding
        // status, whether the runner would auto-pick the project right now, and
        // the same derived verify plan the build/test gate uses. The Settings UI
        // can therefore disclose an empty gate without duplicating discovery.
        app.MapGet("/api/projects/{projectName}/build-profile", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var project = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var profile = settings.Get(projectName).BuildProfile;
            var gate = BuildProfileGate.Evaluate(profile);
            var repositoryPath = string.IsNullOrWhiteSpace(project.RepositoryPath)
                ? project.RootPath
                : project.RepositoryPath;
            var verifyPlan = VerifyCommandPlanner.Plan(repositoryPath, profile);
            return Results.Ok(new
            {
                profile,
                status = profile is null ? null : BuildProfileStatuses.Normalize(profile.Status),
                pickupAllowed = gate.AllowsPickup,
                gateReason = gate.Reason,
                plannedDryRun = BuildProfileDryRunPlanner.Plan(profile),
                gateApplicable = !verifyPlan.IsEmpty,
                verifyPlan = new
                {
                    source = verifyPlan.Source,
                    commands = verifyPlan.Commands,
                },
            });
        });

        // PUT declares or edits the build profile. A first declaration blocks;
        // an edit to a validated profile receives the bounded revalidation grace
        // defined by ProjectSettingsService.
        app.MapPut("/api/projects/{projectName}/build-profile", (string projectName, SetBuildProfileRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetBuildProfile(projectName, new BuildProfile
            {
                Stack = req.Stack,
                InstallCmd = req.InstallCmd,
                BuildCmds = req.BuildCmds,
                TestCmds = req.TestCmds,
                Lockfiles = req.Lockfiles,
                PreserveGlobs = req.PreserveGlobs,
                PoolSize = req.PoolSize,
            });
            var profile = settings.Get(projectName).BuildProfile;
            return Results.Ok(new { profile, pickupAllowed = BuildProfileGate.AllowsAutoPickup(profile) });
        });

        app.MapPut("/api/projects/{projectName}/test-execution", (string projectName, TestExecutionPolicy req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(entry =>
                string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            settings.SetTestExecution(projectName, req);
            return Results.Ok(settings.Get(projectName).TestExecution);
        });

        app.MapDelete("/api/projects/{projectName}/test-execution", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(entry =>
                string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            settings.SetTestExecution(projectName, null);
            return Results.Ok(new { cleared = true });
        });

        // DELETE clears the build profile entirely, reverting the project to the
        // legacy "no onboarding gate" behaviour.
        app.MapDelete("/api/projects/{projectName}/build-profile", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetBuildProfile(projectName, null);
            return Results.Ok(new { cleared = true });
        });

        // POST runs the validation dry-run (install + build in the project's
        // checkout). On green the profile flips to pipeline-ready and the runner
        // may auto-pick the project; on red it lands in validation-failed with a
        // recorded reason. Synchronous: the caller waits for the verdict.
        app.MapPost("/api/projects/{projectName}/build-profile/validate", async (string projectName, ProjectSettingsService settings, TaskScannerService scanner, BuildProfileValidationService validator, CancellationToken ct) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (settings.Get(projectName).BuildProfile is null)
                return Results.BadRequest(new { error = "no build profile declared for this project" });

            var validationWorkspace = BuildProfileValidationWorkspace.Resolve(entry);
            if (validationWorkspace is null)
                return Results.Conflict(new
                {
                    error = "build profile validation requires the project's real RepositoryPath or RootPath; the task workspace is not a source checkout",
                });

            var result = await validator.ValidateAsync(projectName, validationWorkspace, ct);
            var profile = settings.Get(projectName).BuildProfile;
            return Results.Ok(new
            {
                green = result.Green,
                status = result.Status,
                summary = result.Summary,
                failedCommand = result.FailedCommand,
                profile,
                pickupAllowed = BuildProfileGate.AllowsAutoPickup(profile),
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        // The orchestrator's model can be tuned per project. Defaults to
        // Opus when null. This is the only knob today; per-call overrides
        // are not exposed.
        app.MapPut("/api/projects/{projectName}/orchestrator-model", (string projectName, SetOrchestratorModelRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetOrchestratorModel(projectName, req.Model, req.ThinkingLevel);
            return Results.Ok(settings.Get(projectName));
        });

        // Epic decomposition (planning) run knobs (way 3): the model that
        // authors the sub-task list and whether generated sub-tasks land in
        // 2-ready instead of 0-backlog. Null fields leave that knob untouched.
        app.MapPut("/api/projects/{projectName}/epic-planning", (string projectName, SetEpicPlanningRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetEpicPlanning(projectName, req.Model, req.ThinkingLevel, req.SubTasksToReady);
            return Results.Ok(settings.Get(projectName));
        });

        // ADR-0026: per-project autonomy slider for the orchestrator-prep
        // loop. Returns the resolved level (default 2 when not set) so the
        // header can render the slider without a second round-trip.
        app.MapGet("/api/projects/{projectName}/autonomy", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            return Results.Ok(new { level = s.AutonomyLevel ?? 2 });
        });

        app.MapPut("/api/projects/{projectName}/autonomy", (string projectName, SetAutonomyLevelRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetAutonomyLevel(projectName, req.Level);
            return Results.Ok(new { level = settings.Get(projectName).AutonomyLevel ?? 2 });
        });

        app.MapGet("/api/projects/{projectName}/quota-wait-policy", (
            string projectName,
            ProjectSettingsService settings,
            CliQuotaWaitPolicyService waitPolicy,
            TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            return Results.Ok(waitPolicy.Resolve(settings.Get(projectName)));
        });

        app.MapPut("/api/projects/{projectName}/quota-wait-policy", (
            string projectName,
            SetProjectQuotaWaitPolicyRequest req,
            ProjectSettingsService settings,
            CliQuotaWaitPolicyService waitPolicy,
            TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            if (req.ThresholdMinutes is < CliQuotaWaitPolicyService.MinThresholdMinutes or > CliQuotaWaitPolicyService.MaxThresholdMinutes)
                return Results.BadRequest(new { error = $"thresholdMinutes must be between {CliQuotaWaitPolicyService.MinThresholdMinutes} and {CliQuotaWaitPolicyService.MaxThresholdMinutes}" });
            settings.SetQuotaWaitPolicy(projectName, req.Enabled, req.ThresholdMinutes);
            return Results.Ok(waitPolicy.Resolve(settings.Get(projectName)));
        });

        // F35: per-lane sort strategy. GET returns the resolved map (every
        // lane key present, defaults filled in) so the settings UI can render
        // a dropdown per lane without a second round-trip. PUT writes one
        // lane at a time; an empty/null strategy clears the override and the
        // lane reverts to its default.
        app.MapGet("/api/projects/{projectName}/lane-sort-strategies", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lane in TaskStates.All)
                resolved[lane] = LaneSortStrategies.Resolve(s, lane);
            return Results.Ok(new
            {
                resolved,
                overrides = s.LaneSortStrategyOverrides ?? new Dictionary<string, string>(),
                available = LaneSortStrategies.UserVisible,
            });
        });

        app.MapPut("/api/projects/{projectName}/lane-sort-strategy", (string projectName, SetLaneSortStrategyRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (string.IsNullOrWhiteSpace(req.Lane))
                return Results.BadRequest(new { error = "lane is required" });
            if (!TaskStates.All.Contains(req.Lane, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unknown lane '{req.Lane}'" });
            // Empty/null clears. Non-empty must be user-selectable; the
            // internal pickup-priority strategy is not exposed to the UI.
            if (!string.IsNullOrWhiteSpace(req.Strategy)
                && !LaneSortStrategies.IsUserSelectable(req.Strategy))
            {
                return Results.BadRequest(new { error = $"Unsupported sort strategy '{req.Strategy}'" });
            }

            settings.SetLaneSortStrategy(projectName, req.Lane, req.Strategy);
            var s = settings.Get(projectName);
            return Results.Ok(new
            {
                lane = req.Lane,
                strategy = LaneSortStrategies.Resolve(s, req.Lane),
                @override = s.LaneSortStrategyOverrides?.GetValueOrDefault(req.Lane)
            });
        });

        // Per-project orchestrator intake toggle (ready-orchestrator-intake-lane).
        // When enabled, the coding runner waits for intake to mark a 2-ready
        // card as intake-passed before picking it up. Default off so existing
        // projects keep their current behavior.
        app.MapPut("/api/projects/{projectName}/intake", (string projectName, SetIntakeEnabledRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetIntakeEnabled(projectName, req.Enabled);
            return Results.Ok(new { enabled = settings.Get(projectName).IntakeEnabled is true });
        });

        // Manual intake trigger: lets the user (or a test) re-run intake on a
        // single 2-ready job without waiting for the hosted-service tick. The
        // job stays in 2-ready; only the phase + lifecycle.json change. Reuses
        // IntakeRunner so the manual trigger and the background loop share one
        // verdict implementation.
        app.MapPost("/api/tasks/{jobId}/intake", (string jobId, IntakeRunRequestBody body, IntakeRunner intake) =>
        {
            try
            {
                var verdict = intake.RunForJob(jobId, string.IsNullOrWhiteSpace(body.WatchPath) ? null : body.WatchPath);
                return Results.Ok(new
                {
                    outcome = verdict.Outcome.ToString(),
                    reason = verdict.Reason,
                    details = verdict.Details
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static bool IsKnownPipelineStep(string? stepId, string pipelineType = PipelineTypes.Task)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return false;
        var pipelines = PipelineTypes.Normalize(pipelineType) == PipelineTypes.Planning
            ? new[] { PipelineCatalogue.ReadOnly }
            : PipelineCatalogue.All.Where(candidate =>
                !string.Equals(candidate.Id, PipelineCatalogue.ReadOnlyPipelineId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.Id, PipelineCatalogue.ConceptPipelineId, StringComparison.OrdinalIgnoreCase));
        return pipelines.SelectMany(p => p.AllSteps)
                .Any(s => string.Equals(s.Id, stepId, StringComparison.OrdinalIgnoreCase))
            || string.Equals(PipelineCatalogue.AbortReviewStep.Id, stepId, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SetProjectQuotaWaitPolicyRequest
{
    public bool? Enabled { get; init; }
    public int? ThresholdMinutes { get; init; }
}
