using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Pipeline read surface for one job. Returns the static
/// <see cref="TaskPipeline"/> the runtime targets for this job, the
/// latest <see cref="PipelineExecutionRecord"/> on disk
/// (<c>pipeline-execution.json</c> in the job folder), the per-project
/// step configuration (enabled / model / mode resolved from
/// <c>project-settings.json</c>), and a derived per-step + task-total
/// cost breakdown.
///
/// <para>
/// The cost is computed on read from the already-recorded per-step
/// tokens via the single <c>TokenPricing</c> table - cheap because one
/// task has only a handful of steps. The config block lets the Overview
/// render "what ran, on which model, what did it cost" and the Settings
/// panel render the per-step enable / model / mode controls without a
/// second round-trip.
/// </para>
/// </summary>
public static class TaskPipelineEndpoints
{
    public static void MapTaskPipelineEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/pipeline", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            ProjectSettingsService projectSettings,
            PipelineExecutionLog pipelineLog,
            TaskSessionLog sessions,
            TimelineLog timeline,
            ITokenAggregator tokens,
            OnDemandPostStepService onDemand) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var rawSettings = string.IsNullOrWhiteSpace(info.ProjectName)
                ? null
                : projectSettings.Get(info.ProjectName);
            var settings = PipelineTypeSettings.ForTask(rawSettings, info);
            var pipeline = ProjectPipelineOrder.Apply(UiTaskPipelineRouter.Select(info, settings), settings);
            var taskTokens = FindTaskTokens(
                tokens.WorkspacePerJob(info.ProjectName, info.WatchPath),
                info);
            var sessionEvents = sessions.ReadSessionEvents(info.Id, info.WatchPath);
            var projected = RemotePipelineExecutionProjection.Project(
                pipelineLog.Read(info.FolderPath),
                pipeline,
                info,
                sessionEvents,
                timeline.ReadAll(info.FolderPath),
                taskTokens);
            var record = projected.Execution;
            // Remote rows use canonical ledger calls; unchanged local rows
            // retain their recorded step usage. Enrich only the response copy
            // with per-aspect concern detail for the Overview tooltip.
            var cost = PipelineCostCalculator.SummarizeWithLedger(record, projected.LedgerCalls);
            // The visible RUNS rows come from session events, so their token
            // total must use the same run boundaries. Canonical task-ledger
            // calls are attributed into those windows; a run without a call
            // stays explicitly missing instead of silently becoming zero.
            var tokensByModel = PipelineCostCalculator.SummarizeByModel(
                record, sessionEvents, taskTokens);
            var execution = AspectConcernReader.Enrich(record, info.FolderPath);

            var resultFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var aspectEvidence = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in pipeline.AllSteps)
            {
                var relativePath = step.Kind switch
                {
                    StepKind.Core => "status.md",
                    StepKind.Aspect => $"{step.Id}.md",
                    _ => null,
                };
                if (relativePath is not null && File.Exists(Path.Combine(info.FolderPath, relativePath)))
                    resultFiles[step.Id] = relativePath;
                if (step.Kind == StepKind.Aspect)
                {
                    var attempts = new[] { execution }
                        .Concat(execution?.PreviousAttempts ?? [])
                        .Where(item => item is not null)
                        .Select(item => item!.Steps.FirstOrDefault(candidate =>
                            string.Equals(candidate.StepId, step.Id, StringComparison.OrdinalIgnoreCase)))
                        .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.ExecutionAttemptId))
                        .Select(item => item!)
                        .DistinctBy(item => item.ExecutionAttemptId, StringComparer.OrdinalIgnoreCase)
                        .Select(item =>
                        {
                            var attemptId = item.ExecutionAttemptId!;
                            var safeAttempt = new string(attemptId.Select(ch =>
                                char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
                            var rawPrefix = $"remote-review-{safeAttempt}-";
                            var normalizedStep = step.Id;
                            var rawLog = Directory.EnumerateFiles(info.FolderPath, rawPrefix + "*")
                                .Select(Path.GetFileName)
                                .FirstOrDefault(file => file is not null
                                    && file.Contains(normalizedStep, StringComparison.OrdinalIgnoreCase)
                                    && file.Contains("stdout", StringComparison.OrdinalIgnoreCase));
                            var grade = $"remote-review-grade-{safeAttempt}.md";
                            return (object)new
                            {
                                attemptId,
                                reportFile = File.Exists(Path.Combine(info.FolderPath, $"{step.Id}.md"))
                                    ? $"{step.Id}.md" : null,
                                rawLogFile = rawLog,
                                reviewGradeFile = File.Exists(Path.Combine(info.FolderPath, grade)) ? grade : null,
                            };
                        })
                        .ToArray();
                    if (attempts.Length > 0) aspectEvidence[step.Id] = attempts;
                }
            }
            var config = pipeline.AllSteps.ToDictionary(
                step => step.Id,
                step =>
                {
                    // Effective model + source resolved the same way the runtime
                    // resolves it (step override -> project model -> global ->
                    // catalogue -> runtime default), so the Overview pipeline can
                    // show which model each LLM-backed step WILL run on before the
                    // run, not just after. Null for deterministic / core steps.
                    var resolved = PipelineStepModelDefaults.Resolve(settings, step);
                    var configured = PipelineStepConfigResolver.Lookup(settings, step.Id);
                    var stepExecution = execution?.Steps.FirstOrDefault(candidate =>
                        string.Equals(candidate.StepId, step.Id, StringComparison.OrdinalIgnoreCase));
                    return new
                    {
                        enabled = PipelineStepConfigResolver.IsEnabled(settings, step),
                        enabledSource = configured?.Enabled.HasValue == true ? "project" : "catalogue",
                        activation = PostStepActivationProjection.Build(
                            step, configured, stepExecution, execution, info),
                        canDisable = PipelineStepConfigResolver.CanDisable(step),
                        cliType = configured?.CliType ?? step.CliType,
                        model = configured?.Model,
                        thinkingLevel = configured?.ThinkingLevel,
                        mode = configured?.Mode,
                        prompt = configured?.Prompt,
                        condition = configured?.Condition,
                        resolvedModel = resolved?.Model,
                        modelSource = resolved?.Source,
                    };
                },
                StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new
            {
                pipeline,
                execution,
                cost,
                tokensByModel,
                config,
                resultFiles,
                aspectEvidence,
                onDemand = new
                {
                    plannedStepIds = onDemand.ReadPlan(info.FolderPath),
                    attempts = onDemand.ReadAttempts(info.FolderPath),
                },
            });
        });

        // Add a known idempotent post-step to this card and execute only that
        // step. CORE and the historical orchestrator verdict are untouched.
        group.MapPost("/{jobId}/pipeline/steps/{stepId}/run", async (
            string jobId,
            string stepId,
            string? project,
            string? watchPath,
            RunPostStepRequest? body,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            OnDemandPostStepService onDemand,
            CancellationToken ct) =>
        {
            watchPath = body?.WatchPath ?? watchPath;
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!OnDemandPostStepService.IsSupported(stepId))
                return Results.BadRequest(new { error = $"Post-step '{stepId}' cannot run on demand" });

            var context = ResolveProjectContext(projects, scanner, info);
            if (context == null)
                return Results.BadRequest(new { error = "Canonical project identity is not configured for this task" });

            var result = await onDemand.RunAsync(
                info, context.Entry, context.Project.Id, stepId, body?.AddToCard ?? true, ct);
            return Results.Ok(result);
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        // Read-model for the raw step-call prompts captured at central
        // dispatch into .metadata/prompts.jsonl. The UI 'Prompt ansehen'
        // action on a step / timeline entry parses this to show the exact
        // prompt the aspect / code-review-grade / ... step sent to the CLI.
        // Main-run prompts and follow-ups are intentionally absent here -
        // they already live in the task's prompt.md / chat.
        group.MapGet("/{jobId}/step-prompts", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Cli.StepPromptLog promptLog) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var prompts = promptLog.ReadForJob(info.FolderPath);
            return Results.Ok(new { prompts });
        });
    }

    private static TaskTokenSummary? FindTaskTokens(
        IReadOnlyDictionary<string, TaskTokenSummary> lookup,
        TaskInfo task)
    {
        foreach (var key in new[] { task.TaskKey, task.Id, task.Key })
        {
            if (!string.IsNullOrWhiteSpace(key) && lookup.TryGetValue(key, out var summary))
                return TokenSummaryService.WithModelFallback(summary, task.Model);
        }
        return null;
    }
}

public sealed record RunPostStepRequest
{
    public string? WatchPath { get; init; }
    public bool AddToCard { get; init; } = true;
}
