using System.Text;
using AgentStudio.Prompts;

namespace AgentStudio.Pipeline;

/// <summary>Verdict of one task-spawner post-step run.</summary>
public enum TaskSpawnerVerdict
{
    /// <summary>The step did not run (disabled, no target configured, or gated off).</summary>
    Skipped,
    /// <summary>The model judged the change not relevant to the target project.</summary>
    NotRelevant,
    /// <summary>A prior spawn already covers this source task under the dedup budget.</summary>
    Deduped,
    /// <summary>A follow-up card was created in the target project.</summary>
    Spawned,
    /// <summary>An error prevented the evaluation or the create (recorded, non-fatal).</summary>
    Error,
}

/// <summary>Outcome of <see cref="TaskSpawnerPostStepRunner.RunAsync"/>.</summary>
public sealed record TaskSpawnerResult(
    TaskSpawnerVerdict Verdict,
    string Reason,
    string? TargetKey = null,
    string? TargetJobId = null,
    string? TargetProjectName = null,
    string? Model = null,
    // The evaluation call's receipt and effective level, so the step row
    // records what deciding cost. Null when no model was called.
    OrchestratorTokenUsage? Usage = null,
    string? ThinkingLevel = null);

/// <summary>
/// The evidence + resolved config the spawner needs for one source task. The
/// orchestrator assembles it (it already loads the diff / status / inventory for
/// the aspect review) and hands it in, keeping the runner decoupled and
/// unit-testable without standing up the review pipeline - the same shape the
/// <see cref="WikiLearningsPostStepRunner"/> uses.
/// </summary>
public sealed record TaskSpawnerRunContext
{
    public TaskInfo Source { get; init; } = null!;
    public string SourceProjectName { get; init; } = "";
    /// <summary>Watch path or PROJ-NNN id of the project a relevant change spawns into.</summary>
    public string TargetProject { get; init; } = "";
    public string? RelevanceQuestion { get; init; }
    /// <summary>Target lane for the spawned card; clamped to backlog / ready.</summary>
    public string SpawnLane { get; init; } = TaskStates.Backlog;
    public int MaxPerSourceTask { get; init; } = 1;
    public string TaskBody { get; init; } = "";
    public string StatusSummary { get; init; } = "";
    public string DiffSummary { get; init; } = "";
    public string ResultsInventory { get; init; } = "";
    public string Model { get; init; } = TaskSpawnerModelSelector.DefaultModel;
    public string Cli { get; init; } = TaskSpawnerModelSelector.DefaultCli;
    public string? ThinkingLevel { get; init; } = TaskSpawnerModelSelector.DefaultThinkingLevel;
}

/// <summary>
/// Runner for the opt-in <c>post-task-spawner</c> pipeline step (AGT-2028). After
/// a task settles it asks the best available model whether the change set is
/// relevant to another project and, on a conservative yes, creates a follow-up
/// card there with a generated prompt and a <c>relatedTo</c> reference back to the
/// source task. Generic - the target project, relevance question, and spawn lane
/// are configuration, not hard-coded website wiring.
///
/// <para>
/// Reporting-only and fully non-gating: it never changes the source task's lane
/// decision, dedupes via <see cref="SpawnedTaskLedger"/> so a reissue loop cannot
/// double-spawn, and treats every failure as a recorded skip/error rather than an
/// exception into the orchestrator tick. The relevance evaluation is a single
/// one-shot CLI call routed through <see cref="CliOneShotRegistry"/>; the card
/// creation goes through <see cref="TaskMutationService.CreateJob"/> (the bounded
/// write path the file-watcher expects), never a hand-written folder.
/// </para>
/// </summary>
public sealed class TaskSpawnerPostStepRunner
{
    public const string TemplateName = "task-spawner-relevance.md";

    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(180);

    private readonly TaskMutationService _mutations;
    private readonly TaskScannerService _scanner;
    private readonly RuntimePromptService _prompts;
    private readonly ILogger<TaskSpawnerPostStepRunner> _logger;
    private readonly CliOneShotRegistry? _oneShots;

    /// <summary>
    /// Evaluation-call seam. Production routes through
    /// <see cref="CliOneShotRegistry"/>; tests substitute a deterministic stub so
    /// the relevance yes/no path can be exercised without a CLI.
    /// </summary>
    public Func<CliOneShotRequest, CancellationToken, Task<CliOneShotResult>>? OneShotOverride { get; set; }

    public TaskSpawnerPostStepRunner(
        TaskMutationService mutations,
        TaskScannerService scanner,
        RuntimePromptService prompts,
        ILogger<TaskSpawnerPostStepRunner> logger,
        CliOneShotRegistry? oneShots = null)
    {
        _mutations = mutations;
        _scanner = scanner;
        _prompts = prompts;
        _logger = logger;
        _oneShots = oneShots;
    }

    public async Task<TaskSpawnerResult> RunAsync(TaskSpawnerRunContext ctx, CancellationToken ct)
    {
        if (ctx.Source == null || string.IsNullOrWhiteSpace(ctx.TargetProject))
            return new TaskSpawnerResult(TaskSpawnerVerdict.Skipped, "no source or target configured", Model: ctx.Model);

        // Dedup first so an already-covered source task never pays for a model
        // call. The ledger lives in the source folder and survives the reissue
        // loop, so this is the hard "max 1 per source task" guarantee.
        if (!SpawnedTaskLedger.CanSpawn(ctx.Source.FolderPath, ctx.TargetProject, ctx.MaxPerSourceTask, _logger))
            return new TaskSpawnerResult(TaskSpawnerVerdict.Deduped,
                "a follow-up was already spawned for this source task", Model: ctx.Model);

        var prompt = BuildPrompt(ctx);
        var request = new CliOneShotRequest(ctx.Cli, ctx.Model, prompt)
        {
            ThinkingLevel = ctx.ThinkingLevel,
            Timeout = EvaluationTimeout,
            Source = AdHocUsageSources.TaskSpawner,
            Project = ctx.SourceProjectName,
            JobId = ctx.Source.Id,
            RecordUsage = true,
            JobFolderPath = ctx.Source.FolderPath,
            StepId = PipelineCatalogue.TaskSpawnerStepId,
            TemplateRef = TemplateName,
        };

        CliOneShotResult result;
        try
        {
            result = await RunOneShotAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "task-spawner: evaluation call threw for {Project}/{JobId}",
                ctx.SourceProjectName, ctx.Source.Id);
            return new TaskSpawnerResult(TaskSpawnerVerdict.Error, "evaluation call failed: " + ex.Message, Model: ctx.Model);
        }

        var ranModel = result.EffectiveModel ?? result.Usage?.Model ?? ctx.Model;
        var ranLevel = result.EffectiveThinkingLevel ?? ctx.ThinkingLevel;
        if (result is { Ok: false })
        {
            return new TaskSpawnerResult(TaskSpawnerVerdict.Error,
                "evaluation CLI failed: " + (string.IsNullOrWhiteSpace(result.Error) ? "no output" : result.Error),
                Model: ranModel, Usage: result.Usage, ThinkingLevel: ranLevel);
        }

        var reply = !string.IsNullOrWhiteSpace(result.ParsedText) ? result.ParsedText : result.Stdout;
        var decision = TaskSpawnerDecisionParser.Parse(reply);
        if (!decision.Relevant)
            return new TaskSpawnerResult(TaskSpawnerVerdict.NotRelevant,
                decision.Reason ?? "model judged the change not relevant",
                Model: ranModel, Usage: result.Usage, ThinkingLevel: ranLevel);
        if (!decision.CanSpawn)
            return new TaskSpawnerResult(TaskSpawnerVerdict.NotRelevant,
                "relevant but the model produced no follow-up prompt",
                Model: ranModel, Usage: result.Usage, ThinkingLevel: ranLevel);

        return Spawn(ctx, decision) with { Model = ranModel, Usage = result.Usage, ThinkingLevel = ranLevel };
    }

    private TaskSpawnerResult Spawn(TaskSpawnerRunContext ctx, TaskSpawnerDecision decision)
    {
        try
        {
            var lane = EpicSubTaskFactory.ClampTargetState(ctx.SpawnLane);
            var title = string.IsNullOrWhiteSpace(decision.Title)
                ? DefaultTitle(ctx.Source)
                : decision.Title!.Trim();
            var body = ComposeSpawnedPrompt(ctx, decision);

            // Model / CliType are left unset so CreateJob materializes the target
            // project's default model for the spawned card (operator-direktive:
            // "bearbeitet vom Default-Modell des Zielprojekts").
            var jobId = _mutations.CreateJob(new CreateTaskRequest
            {
                Title = title,
                WatchPath = ctx.TargetProject,
                PromptMarkdown = body,
                TargetState = lane,
            });
            if (string.IsNullOrWhiteSpace(jobId))
            {
                _logger.LogWarning(
                    "task-spawner: create refused for target {Target} (source {Project}/{JobId}); project not found?",
                    ctx.TargetProject, ctx.SourceProjectName, ctx.Source.Id);
                return new TaskSpawnerResult(TaskSpawnerVerdict.Error,
                    "target project not found or create refused: " + ctx.TargetProject, Model: ctx.Model);
            }

            // Resolve the created card across all projects to get its minted
            // display key + real watch path (TargetProject may be a PROJ id).
            var created = _scanner.FindJob(jobId, null);
            var targetKey = created?.Key ?? jobId;
            var targetProjectName = created?.ProjectName ?? ctx.TargetProject;

            // Spawn creates a reference, not a dependency: relatedTo is the
            // non-blocking thematic link back to the source (AGT-2028). The
            // separate Task-Dependencies feature can later turn this into a
            // waits-on edge.
            if (!string.IsNullOrWhiteSpace(ctx.Source.Key) && created != null)
            {
                _mutations.SetTaskReferences(
                    jobId,
                    new TaskReferences { RelatedTo = new List<string> { ctx.Source.Key! } },
                    created.WatchPath);
            }

            SpawnedTaskLedger.Append(ctx.Source.FolderPath, new SpawnedTaskRecord
            {
                At = DateTime.UtcNow,
                SourceKey = ctx.Source.Key,
                TargetProject = ctx.TargetProject,
                TargetKey = targetKey,
                TargetJobId = jobId,
                Reason = decision.Reason,
            }, _logger);

            _logger.LogInformation(
                "task-spawner: spawned {TargetKey} in {TargetProject} from {SourceProject}/{SourceKey}",
                targetKey, targetProjectName, ctx.SourceProjectName, ctx.Source.Key ?? ctx.Source.Id);

            return new TaskSpawnerResult(
                TaskSpawnerVerdict.Spawned,
                decision.Reason ?? "change judged relevant",
                TargetKey: targetKey,
                TargetJobId: jobId,
                TargetProjectName: targetProjectName,
                Model: ctx.Model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "task-spawner: spawn failed for source {Project}/{JobId}",
                ctx.SourceProjectName, ctx.Source.Id);
            return new TaskSpawnerResult(TaskSpawnerVerdict.Error, "spawn failed: " + ex.Message, Model: ctx.Model);
        }
    }

    private async Task<CliOneShotResult> RunOneShotAsync(CliOneShotRequest request, CancellationToken ct)
    {
        if (OneShotOverride != null) return await OneShotOverride(request, ct);
        var oneShot = _oneShots?.Get(request.CliType);
        if (oneShot == null)
        {
            var now = DateTime.UtcNow;
            return CliOneShotResult.SpawnFailure("no one-shot CLI registered for " + request.CliType, now, now);
        }
        return await oneShot.RunAsync(request, ct);
    }

    private string BuildPrompt(TaskSpawnerRunContext ctx)
    {
        var values = new Dictionary<string, string?>
        {
            ["relevance_question"] = string.IsNullOrWhiteSpace(ctx.RelevanceQuestion)
                ? "Is this change relevant to the target project (a new capability, a removed capability, or changed behaviour a user of the target project would need to know about)?"
                : ctx.RelevanceQuestion!.Trim(),
            ["source_project"] = ctx.SourceProjectName,
            ["source_key"] = ctx.Source.Key ?? ctx.Source.Id,
            ["source_title"] = ctx.Source.Title ?? ctx.Source.Id,
            ["target_project"] = ctx.TargetProject,
            ["task_body"] = Trim(ctx.TaskBody, 6000),
            ["status_summary"] = Trim(ctx.StatusSummary, 4000),
            ["diff_summary"] = Trim(ctx.DiffSummary, 8000),
            ["results_inventory"] = string.IsNullOrWhiteSpace(ctx.ResultsInventory)
                ? "No results/ inventory available."
                : Trim(ctx.ResultsInventory, 3000),
            ["source_commits"] = RenderCommits(ctx.Source),
        };

        try
        {
            var rendered = _prompts.Render(
                TemplateName,
                values,
                new PromptCallContext(
                    ctx.SourceProjectName,
                    PipelineCatalogue.TaskSpawnerStepId,
                    ctx.Model));
            if (!string.IsNullOrWhiteSpace(rendered)) return rendered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "task-spawner: template {Template} render failed; using inline prompt", TemplateName);
        }
        return BuildInlinePrompt(values);
    }

    // Inline fallback so a missing / unreadable template never blocks the step.
    private static string BuildInlinePrompt(Dictionary<string, string?> v)
    {
        var sb = new StringBuilder();
        sb.AppendLf("You evaluate whether a just-completed change should spawn a follow-up task in another project.");
        sb.AppendLf();
        sb.AppendLf("Relevance question: " + (v["relevance_question"] ?? ""));
        sb.AppendLf($"Source: {v["source_project"]} {v["source_key"]} - {v["source_title"]}");
        sb.AppendLf("Target project: " + (v["target_project"] ?? ""));
        sb.AppendLf();
        sb.AppendLf("## Task prompt");
        sb.AppendLf(v["task_body"] ?? "");
        sb.AppendLf();
        sb.AppendLf("## Status summary");
        sb.AppendLf(v["status_summary"] ?? "");
        sb.AppendLf();
        sb.AppendLf("## Change summary");
        sb.AppendLf(v["diff_summary"] ?? "");
        sb.AppendLf();
        sb.AppendLf("## Results inventory");
        sb.AppendLf(v["results_inventory"] ?? "");
        sb.AppendLf();
        sb.AppendLf("Be conservative: only judge relevant when a follow-up in the target project is clearly warranted.");
        sb.AppendLf("Reply with, on its own line:");
        sb.AppendLf("[[TASK_SPAWN: relevant=<yes|no>; reason=<one short sentence>]]");
        sb.AppendLf("If relevant=yes, also add:");
        sb.AppendLf("### SPAWN_TITLE");
        sb.AppendLf("<one-line title for the follow-up task>");
        sb.AppendLf("### SPAWN_PROMPT");
        sb.AppendLf("<a complete, self-contained task prompt for the target project's agent>");
        return sb.ToString();
    }

    // Prepend a machine-written provenance header to the model's generated prompt
    // so the spawned card always carries its source reference (Task-Key +
    // commits) even if the model omits it.
    private static string ComposeSpawnedPrompt(TaskSpawnerRunContext ctx, TaskSpawnerDecision decision)
    {
        var sb = new StringBuilder();
        var sourceKey = ctx.Source.Key ?? ctx.Source.Id;
        sb.Append("> Auto-spawned from ").Append(ctx.SourceProjectName).Append(' ').Append(sourceKey);
        if (!string.IsNullOrWhiteSpace(ctx.Source.Title))
            sb.Append(": \"").Append(ctx.Source.Title!.Trim()).Append('"');
        sb.AppendLf();
        var commits = RenderCommits(ctx.Source);
        if (!string.IsNullOrWhiteSpace(commits) && commits != "(no commits recorded)")
            sb.Append("> Source commits: ").AppendLf(commits.Replace("\n", "; "));
        if (!string.IsNullOrWhiteSpace(decision.Reason))
            sb.Append("> Relevance: ").AppendLf(decision.Reason!.Trim());
        sb.AppendLf("> Created by the task-spawner pipeline step; this card references its source task.");
        sb.AppendLf();
        sb.Append(decision.Prompt!.Trim());
        return sb.ToString();
    }

    private static string RenderCommits(TaskInfo source)
    {
        if (source.Commits.Count == 0) return "(no commits recorded)";
        var sb = new StringBuilder();
        foreach (var c in source.Commits.Take(20))
        {
            var sha = !string.IsNullOrWhiteSpace(c.ShortSha) ? c.ShortSha : c.Sha;
            if (string.IsNullOrWhiteSpace(sha)) continue;
            sb.Append(sha);
            var subject = FirstLine(c.Message);
            if (!string.IsNullOrWhiteSpace(subject))
                sb.Append(' ').Append(subject);
            sb.Append('\n');
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "(no commits recorded)" : s;
    }

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var nl = value.IndexOf('\n');
        var line = (nl < 0 ? value : value[..nl]).Trim();
        return line;
    }

    private static string DefaultTitle(TaskInfo source)
    {
        var key = source.Key ?? source.Id;
        var title = string.IsNullOrWhiteSpace(source.Title) ? key : source.Title!.Trim();
        return Trim($"Follow-up from {key}: {title}", 160);
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "\n... (truncated)";
    }
}
