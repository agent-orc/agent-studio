using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// First-class description of how a task is processed end-to-end:
/// pre-processing steps, a single core run, and post-processing steps.
/// Pipelines are static metadata - the runtime instantiates a pipeline
/// per job and records per-step execution into a sibling
/// <see cref="PipelineExecutionRecord"/>. The first concrete pipeline
/// is <c>standard-task-pipeline</c> in
/// <see cref="AgentStudio.Pipeline.PipelineCatalogue"/>;
/// the four 4-auto-review aspect runs are explicit
/// <see cref="StepKind.Aspect"/> post-steps that today's
/// <c>AspectRunnerService</c> consumes.
/// </summary>
public sealed record TaskPipeline
{
    /// <summary>Stable id, e.g. <c>standard-task-pipeline</c>.</summary>
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Schema version. Bumped when the wire shape changes.</summary>
    public int Version { get; init; } = 2;
    public List<PipelineStep> Pre { get; init; } = [];
    public List<PipelineStep> Core { get; init; } = [];
    public List<PipelineStep> Post { get; init; } = [];

    public IEnumerable<PipelineStep> AllSteps => Pre.Concat(Core).Concat(Post);
}

/// <summary>
/// One step in a <see cref="TaskPipeline"/>. Steps are pure metadata;
/// the runtime maps a step's <see cref="Kind"/> to a concrete service
/// (e.g. <see cref="StepKind.Aspect"/> -> <c>AspectRunnerService</c>).
/// </summary>
public sealed record PipelineStep
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public StepKind Kind { get; init; } = StepKind.Module;
    public StepRunMode RunMode { get; init; } = StepRunMode.Sequential;
    /// <summary>
    /// Repository stack required by this step. The project value is derived
    /// from repository markers, never copied from project settings.
    /// </summary>
    public string AppliesTo { get; init; } = PipelineStepStacks.Any;
    /// <summary>
    /// Step ids that must complete before this step starts. Empty means
    /// "no intra-section dependency"; siblings with <see cref="RunMode"/>
    /// = <see cref="StepRunMode.Parallel"/> and no edges run together.
    /// </summary>
    public List<string> DependsOn { get; init; } = [];
    /// <summary>
    /// Optional per-step model override. Resolution order at runtime is
    /// step -> job -> project -> client default.
    /// </summary>
    public string? Model { get; init; }
    /// <summary>
    /// Prompt template or prompt source that shapes this step. For LLM-backed
    /// steps this is the runtime prompt file; for the core run this can point
    /// at the task prompt. Null means the step is deterministic or its prompt is
    /// generated inline by the runtime.
    /// </summary>
    public string? PromptTemplate { get; init; }
    public string? CliType { get; init; }
    /// <summary>
    /// Optional technology marker for a framework-specific step. Null means the
    /// step is stack-neutral. The settings UI renders this as metadata, never as
    /// an activation rule.
    /// </summary>
    public string? Framework { get; init; }
    public int? TimeoutMs { get; init; }
    /// <summary>
    /// When true, the step is safe to re-run after a partial failure.
    /// Aspect steps and the git-commit-attribution stub are idempotent;
    /// the core agent run is not.
    /// </summary>
    public bool Idempotent { get; init; }
    /// <summary>
    /// When true, the step is a placeholder slot reserved for a future
    /// implementation (e.g. the git-commit-attribution post-step that
    /// the follow-up task will fill). Surfaces as "planned" in the
    /// execution record but does not run.
    /// </summary>
    public bool Stub { get; init; }
    /// <summary>
    /// When true, the step is fully implemented but runs only on an external
    /// operator trigger rather than automatically in the post-bracket. It is
    /// distinct from <see cref="Stub"/>: a stub has no implementation and shows
    /// as "planned"; a deferred step has a real implementation and shows as
    /// "pending" until the operator triggers it. The
    /// <c>post-merge-into-develop</c> post-step is deferred - it performs the
    /// real <c>task/&lt;id&gt; -&gt; develop</c> merge only when the operator
    /// runs the "Merge into Develop" action, so it sits in the pipeline as a
    /// not-yet-run step until then. Implemented by
    /// <c>AgentStudio.Pipeline.MergeIntoDevelopRunner</c>.
    /// </summary>
    public bool Deferred { get; init; }
    /// <summary>
    /// Whether the step runs when a project has no explicit override for it.
    /// Most steps default on; the opt-in drift post-steps default off because
    /// a drift run is an expensive extra LLM pass the operator turns on per
    /// project. <see cref="AgentStudio.Pipeline.PipelineStepConfigResolver.IsEnabled(ProjectSettings?, PipelineStep)"/>
    /// resolves the project override against this default.
    /// </summary>
    public bool DefaultEnabled { get; init; } = true;
}

/// <summary>Stable stack ids used by pipeline catalogue applicability metadata.</summary>
public static class PipelineStepStacks
{
    public const string Any = "any";
    public const string Angular = "angular";
    public const string DotNet = "dotnet";
    public const string Node = "node";
}

public enum StepKind
{
    /// <summary>
    /// Pre-processing module that prepares context (e.g. requirement
    /// clarification, skill readiness). No first-class implementation
    /// in Phase 1; reserved for catalogue use.
    /// </summary>
    Module,
    /// <summary>
    /// The core agent run (CLI process driven by <c>TaskRunnerService</c>).
    /// </summary>
    Core,
    /// <summary>
    /// One aspect runner pass (today: <c>code-quality</c>,
    /// <c>requirement-fit</c>, <c>documentation-impact</c>,
    /// <c>tests-and-evidence</c>). Implemented by
    /// <c>AspectRunnerService</c>.
    /// </summary>
    Aspect,
    /// <summary>
    /// Orchestrator decision step that consumes aspect verdicts and
    /// chooses reissue / accept / escalate. Implemented by
    /// <c>ReviewDecisionOrchestrator</c>.
    /// </summary>
    Orchestrator,
    /// <summary>
    /// Tool-driven post-step (deterministic, no model). The
    /// git-commit-attribution slot lives here; the follow-up task fills
    /// the implementation.
    /// </summary>
    Tool,
    /// <summary>
    /// A named Quality Studio analysis executed against the checked-out
    /// repository through the in-process analysis-core package. Analysis steps
    /// emit canonical findings and provenance; they are not generic shell tools.
    /// </summary>
    Analysis,
    /// <summary>
    /// One drift-analysis dimension run as an opt-in post-step (ADR / Code,
    /// Software / Architecture, Docs / Marketing, Spec / Task / Job, and the
    /// rule-based Code-Pattern check). Reuses the existing
    /// <c>*DriftAnalysisService</c> + <c>DriftReportStore</c>; the post-step
    /// only adds the automatic trigger. Drift steps default off (opt-in) and
    /// accept a per-step model like an aspect because four of the five
    /// dimensions drive an LLM call. Implemented by
    /// <c>DriftPostStepRunner</c>.
    /// </summary>
    Drift,
}

public enum StepRunMode
{
    /// <summary>Run after every earlier sibling has finished.</summary>
    Sequential,
    /// <summary>
    /// Eligible to run alongside its siblings when <see cref="PipelineStep.DependsOn"/>
    /// allows. The runtime enforces a bounded fan-out so a misconfigured
    /// pipeline cannot exhaust the CLI quota in one tick.
    /// </summary>
    Parallel,
}

/// <summary>
/// One pipeline run for one job. Persisted as
/// <c>pipeline-execution.json</c> next to <c>aspect-*.md</c> in the job
/// folder. Append-mostly: a step's record lands when the step finishes
/// (success or failure); the overall <see cref="CompletedAt"/> stamp
/// lands when the pipeline run ends. Schema kept small so the frontend
/// can render the Overview pipeline view without further joins.
/// </summary>
public sealed record PipelineExecutionRecord
{
    public string PipelineId { get; init; } = string.Empty;
    public int PipelineVersion { get; init; } = 1;
    public string JobId { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public List<PipelineStepExecution> Steps { get; init; } = [];

    /// <summary>
    /// 1-based run counter for this job. A pipeline re-run / re-issue starts a
    /// fresh record (new <see cref="StartedAt"/>) and increments this so the
    /// Overview pipeline table can show "Run #N" and flag a restart. Attempt 1
    /// is the first run; anything above 1 means the pipeline was restarted.
    /// </summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// Prior completed runs for this job, most-recent first, so the operator can
    /// still tell old step runs apart from the current ones after a restart.
    /// Each archived entry keeps its own <see cref="Steps"/> but carries an empty
    /// <see cref="PreviousAttempts"/> (the chain is flattened on this list, not
    /// nested) and is bounded to the last few runs to keep the file small.
    /// </summary>
    public List<PipelineExecutionRecord> PreviousAttempts { get; init; } = [];

    [JsonIgnore]
    public bool IsComplete => CompletedAt.HasValue;
}

public sealed record PipelineStepExecution
{
    /// <summary>Executed gate or reused verdict; null for other steps and legacy rows.</summary>
    public AgentStudio.Pipeline.GateVerdictSource? GateVerdictSource { get; init; }
    /// <summary>Original gate evidence, even when this step used the cache.</summary>
    public string? GateOriginEvidencePath { get; init; }
    public DateTimeOffset? GateOriginCompletedAtUtc { get; init; }
    public string StepId { get; init; } = string.Empty;
    public StepKind Kind { get; init; }
    /// <summary>
    /// Pipeline attempt epoch that owns this step state. Writers stamp the
    /// current attempt when the update is accepted. A late update from an
    /// older attempt is fenced out before it can replace the fresh row.
    /// Null is accepted only for legacy records read from disk.
    /// </summary>
    public int? Attempt { get; init; }
    /// <summary>
    /// Model actually used. May differ from <see cref="PipelineStep.Model"/>
    /// when the runtime fell back to the project / client default.
    /// </summary>
    public string? Model { get; init; }
    /// <summary>Reasoning level selected for this step or recommendation.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>Model recommended by a qualification step before overrides.</summary>
    public string? RecommendedModel { get; init; }
    /// <summary>Reasoning level recommended before overrides.</summary>
    public string? RecommendedThinkingLevel { get; init; }
    /// <summary>Where the effective selection came from: policy, policy-economy, or task-override.</summary>
    public string? SelectionSource { get; init; }
    /// <summary>
    /// Which level of the model-resolution chain chose <see cref="Model"/> for
    /// this execution: <c>step</c>, <c>project</c>, <c>global</c>,
    /// <c>catalogue</c> or <c>runtime</c> from
    /// <c>PipelineStepConfigResolver</c>, <c>task</c> for a card-pinned core
    /// run, or <c>config</c> for a host-configured decision model. Null on
    /// legacy rows and on steps that ran no model.
    /// </summary>
    public string? ModelSource { get; init; }
    /// <summary>Heuristic saving versus the live catalogue's top rung.</summary>
    public int? EstimatedSavingsPercent { get; init; }
    public PipelineStepStatus Status { get; init; } = PipelineStepStatus.Pending;
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long DurationMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    /// <summary>Provider input-counter semantics captured at parse time. Null on legacy rows.</summary>
    public bool? InputIncludesCached { get; init; }
    /// <summary>Stable marker for a historical normalization applied after capture.</summary>
    public string? UsageNormalization { get; init; }
    /// <summary>
    /// How this execution's cost was measured, from
    /// <c>AgentStudio.Pipeline.StepCostBasis</c>: <c>model</c> (an LLM call
    /// with recorded tokens), <c>deterministic</c> (no model call, a measured
    /// zero) or <c>not-run</c>. Null on legacy rows and while running, and on
    /// a model-backed row whose executor reported no model, which is a
    /// measurement gap rather than a zero.
    /// </summary>
    public string? CostBasis { get; init; }
    /// <summary>
    /// Historical list-price estimate for this execution's tokens. Zero for a
    /// deterministic or not-run step. Null when the model has no catalogue
    /// price (see <see cref="ModelPriced"/>): an unpriced call is never zero.
    /// </summary>
    public decimal? EstimatedCostUsd { get; init; }
    /// <summary>
    /// True when the price catalogue resolved <see cref="Model"/> at the run
    /// date, false when the model is unpriced, null when no model ran.
    /// </summary>
    public bool? ModelPriced { get; init; }
    /// <summary>
    /// How many times this step executed inside this pipeline attempt,
    /// counting review rounds and repeated decisions. Earlier executions are
    /// kept in <see cref="EarlierRuns"/>; archived attempts carry their own
    /// count, so the per-card total is the sum across every attempt.
    /// </summary>
    public int Runs { get; init; }
    /// <summary>
    /// Earlier executions of this step inside the same attempt, oldest first,
    /// bounded. <c>PipelineExecutionLog.RecordStep</c> moves the
    /// previous terminal row here when a step starts again instead of
    /// overwriting it, so a repeated decision keeps its own model and cost.
    /// </summary>
    public List<PipelineStepRunSummary>? EarlierRuns { get; init; }
    /// <summary>Physical placement reported by the executor, for example local or remote.</summary>
    public string? ExecutionLocation { get; init; }
    /// <summary>Runner host that executed the step when placement is remote.</summary>
    public string? ExecutionHostId { get; init; }
    /// <summary>Leased executor identity that executed the step.</summary>
    public string? ExecutionExecutorId { get; init; }
    /// <summary>Fenced attempt that owns the remote step evidence.</summary>
    public string? ExecutionAttemptId { get; init; }
    /// <summary>
    /// Human-readable attribution for the token counts, e.g.
    /// <c>AGENT (CLI FOOTER) / reported</c>. Null when no token usage was
    /// recorded for the step.
    /// </summary>
    public string? TokenUsageSource { get; init; }
    /// <summary>Short reason on failure / skip; null on success.</summary>
    public string? Reason { get; init; }
    /// <summary>Task-folder-relative artifact containing the step's detailed evidence.</summary>
    public string? EvidenceRef { get; init; }
    /// <summary>
    /// Structured evidence for a delivery that exhausted every integration
    /// stage. Null for successful integration and non-conflict failures.
    /// </summary>
    public IntegrationConflictReport? ConflictReport { get; init; }
    /// <summary>
    /// Stable machine-readable classification for a failed step. The reason
    /// remains human evidence; consumers use this code for card state and
    /// recovery eligibility without parsing prose.
    /// </summary>
    public string? FailureCode { get; init; }
    /// <summary>
    /// Optional verdict token from the step (e.g. <c>pass</c>,
    /// <c>concerns</c>, <c>block</c> for aspect steps). Lets the UI
    /// render the right pill without re-reading the aspect MD.
    /// </summary>
    public string? Verdict { get; init; }
    /// <summary>
    /// Optional human-readable detail behind the verdict; for aspect
    /// steps with a non-pass verdict, the concern summary lifted from the
    /// <c>aspect-{id}.md</c> frontmatter at read time. Lets the Overview
    /// pipeline render the concrete concern as a tooltip on the CONCERNS
    /// pill without a second fetch. Null when the step has no concern
    /// detail (e.g. a pass verdict, or a non-aspect step).
    /// </summary>
    public string? VerdictSummary { get; init; }
    /// <summary>Prior review attempt supplying this unchanged aspect verdict.</summary>
    public string? CarriedOverFrom { get; init; }
    /// <summary>Coding run that addressed this aspect, when known.</summary>
    public int? FixedInRun { get; init; }
    /// <summary>True when the finding remained after the bounded fix round.</summary>
    public bool? StillOpen { get; init; }
}

/// <summary>
/// One earlier execution of a step inside a pipeline attempt. Carries what the
/// transition view and the cost rollup need: when it ran, what it decided, on
/// which model, and what it cost. Token counts are zero when the step
/// accumulates its own totals in place (the core run), so a repeated core run
/// is counted without being priced twice.
/// </summary>
public sealed record PipelineStepRunSummary
{
    public PipelineStepStatus Status { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long DurationMs { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? ModelSource { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public string? CostBasis { get; init; }
    public decimal? EstimatedCostUsd { get; init; }
    public bool? ModelPriced { get; init; }
    public string? Verdict { get; init; }
    public string? Reason { get; init; }
    public string? EvidenceRef { get; init; }
}

public enum PipelineStepStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Skipped,
    /// <summary>
    /// Step is a stub slot (e.g. git-commit-attribution before the
    /// follow-up task implements it) and was not executed.
    /// </summary>
    Planned,
    /// <summary>
    /// Step deliberately has no work because the project has no applicable
    /// verification commands. Unlike <see cref="Skipped"/>, this is not an
    /// interrupted or unexecuted required check.
    /// </summary>
    NotApplicable,
}
