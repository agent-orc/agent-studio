namespace AgentStudio.Shared;

public record ProjectSettings
{
    /// <summary>
    /// Per publish-target automation ladder. Keys are derived target ids
    /// (<c>package:npm</c>, <c>package:nuget</c>, <c>website</c>); values are
    /// <c>manual</c>, <c>suggest</c>, or <c>auto</c>. Missing entries resolve to
    /// manual. Package targets never resolve above suggest.
    /// </summary>
    public Dictionary<string, string>? PublishAutomation { get; init; }

    /// <summary>When true, transition <c>3-progress → 4-auto-review</c> auto-commits and stamps the SHA on the job.</summary>
    public bool AutoCommit { get; init; } = true;

    /// <summary>
    /// When true, the boot-time crash recovery sweep runs for this project.
    /// Orphan working-tree commits still require operator confirmation.
    /// </summary>
    public bool CrashRecoveryEnabled { get; init; } = true;

    /// <summary>
    /// Controls when the platform pushes runner-owned commits. Default is
    /// <see cref="AutoPushStrategies.AlwaysImmediate"/> so every platform-owned
    /// commit is made durable on origin without waiting for lane transitions.
    /// </summary>
    public string AutoPushStrategy { get; init; } = AutoPushStrategies.AlwaysImmediate;

    /// <summary>
    /// Last <i>live</i> runner mode for this project ("manual", "auto-single",
    /// "auto-continuous", "paused"), updated on every mode change regardless of
    /// who caused it (operator, circuit-breaker, supervisor, update-quiesce).
    /// The supervisor meta-cycle reads this to detect runner-mode drift, so it
    /// must keep mirroring the actual live mode. Null means "use the default
    /// (manual)". Boot restore prefers <see cref="DesiredRunnerMode"/> over this
    /// so a transient system flip does not become the restored mode (ASS-1753).
    /// </summary>
    public string? RunnerMode { get; init; }

    /// <summary>
    /// The operator's durable auto-pickup intent - the last mode set by a
    /// <i>user</i>-sourced change (the API toggle), never overwritten by a
    /// system-driven flip such as the update-service quiesce or a
    /// circuit-breaker pause. This is what the backend restores at startup so
    /// "auto-continuous" survives a self-rebuild / restart even when the runner
    /// was sitting at a system-imposed manual when the process went down
    /// (ASS-1753). Null falls back to <see cref="RunnerMode"/> for legacy
    /// records written before this field existed.
    /// </summary>
    public string? DesiredRunnerMode { get; init; }

    /// <summary>
    /// Canonical automatic pickup intent, independent from execution placement.
    /// One of <see cref="PickupModes.Auto"/>, <see cref="PickupModes.Manual"/>,
    /// or <see cref="PickupModes.Paused"/>. Null is accepted only for legacy
    /// records and resolves through <see cref="ProjectExecutionPolicy"/>.
    /// </summary>
    public string? PickupMode { get; init; }

    /// <summary>
    /// Canonical execution placement, independent from pickup intent.
    /// <see cref="ExecutionLocations.Local"/> selects the in-process runner;
    /// any other value is the registered remote runner id. Null is accepted
    /// only for legacy records and resolves through
    /// <see cref="ProjectExecutionPolicy"/>.
    /// </summary>
    public string? ExecutionLocation { get; init; }

    /// <summary>
    /// Legacy compatibility mirror for <see cref="ExecutionLocation"/>.
    /// New code must resolve placement through <see cref="ProjectExecutionPolicy"/>.
    /// </summary>
    public string? ExecutionRunner { get; init; }

    /// <summary>
    /// Whether this project's tasks may execute on a remote host. Defaults to
    /// true; set false only for machine-bound suites such as UpdateService
    /// Windows machinery or live-checkout drift scans. Headless UI and
    /// screenshot work remains remote-capable.
    /// </summary>
    public bool RemoteExecutionEnabled { get; init; } = true;

    /// <summary>
    /// Model the orchestrator uses when it makes decisions on behalf of the
    /// user in auto mode (Phase E and later). Null means use the default.
    /// </summary>
    public string? OrchestratorModel { get; init; }

    /// <summary>
    /// Thinking / reasoning level for the orchestrator model. Null means use
    /// the selected model's default capability level.
    /// </summary>
    public string? OrchestratorThinkingLevel { get; init; }

    /// <summary>
    /// Per-project override for the local CLI execution engine. One of
    /// <see cref="CliExecutionEngines.Car"/> or
    /// <see cref="CliExecutionEngines.Legacy"/>. Null inherits the owning
    /// workspace default, then <see cref="CliExecutionEngines.Default"/>. The
    /// process-wide rollback selector takes precedence when present.
    /// </summary>
    public string? CliExecutionEngine { get; init; }

    /// <summary>
    /// Per-topic cadence for scheduled analysis reports (project-level
    /// "Analysis Reports" surface). Map of topic slug
    /// (e.g. <c>roadmapAlignment</c>, <c>queueHealth</c>, <c>docsDrift</c>,
    /// <c>staleJobs</c>, <c>tokenSpend</c>, <c>qaStatus</c>) to one of
    /// <c>disabled</c>, <c>fewHours</c>, <c>daily</c>, <c>manualOnly</c>.
    /// Default null = "disabled" for every topic; reports never auto-run
    /// without an explicit opt-in. The contract for execution is documented
    /// in <c>docs/system/reports/analysis-reports.md</c>; this struct stores the user's
    /// cadence choice only.
    /// </summary>
    public Dictionary<string, string>? AnalysisSchedules { get; init; }

    /// <summary>
    /// ADR-0026 orchestrator-prep autonomy scale, <c>0..4</c>:
    /// <c>0</c> manual, <c>1</c> cautious, <c>2</c> balanced (default),
    /// <c>3</c> confident, <c>4</c> fully-auto. Governs whether the
    /// orchestrator-prep loop accepts borderline tasks, iterates, or
    /// escalates them to <c>5e-escalated</c> (the retired
    /// <c>1b-needs-human-review</c> lane is gone). Null means "use the
    /// default (balanced, level 2)". The setting is consulted on each
    /// pickup tick; mid-iteration policy switches do not happen.
    /// </summary>
    public int? AutonomyLevel { get; init; }

    /// <summary>
    /// Per-project override for the global wait-on-quota policy. Null inherits
    /// the global CLI/quota setting; true/false explicitly enables/disables it
    /// for this project.
    /// </summary>
    public bool? WaitOnQuotaEnabled { get; init; }

    /// <summary>
    /// Per-project override for the longest nearby quota-reset delay worth
    /// waiting for. Null inherits the global threshold.
    /// </summary>
    public int? WaitOnQuotaThresholdMinutes { get; init; }

    /// <summary>
    /// Per-project switch for the orchestrator intake loop. When true, the
    /// coding runner waits for orchestrator intake to finish before picking
    /// up a 2-ready card (gates pickup on <c>phase == intake-passed</c>).
    /// When false / null (default), the gate is open: cards are picked up
    /// regardless of phase, and the intake hosted service does not act on
    /// the project. Intake is opt-in per project so the broader migration
    /// risk stays bounded; see the <c>ready-orchestrator-intake-lane</c>
    /// task in the expanded-lifecycle-lanes plan.
    /// </summary>
    public bool? IntakeEnabled { get; init; }

    /// <summary>
    /// F35: per-lane sort strategy override. Map of lane key
    /// (<see cref="TaskStates.Backlog"/> .. <see cref="TaskStates.Archive"/>)
    /// to a strategy id from <see cref="LaneSortStrategies"/>. Null or a
    /// missing lane key falls back to <see cref="LaneSortStrategies.GetDefaultForLane"/>.
    /// Used by the kanban grouped endpoint when ordering jobs inside a lane;
    /// the runner's pickup loop keeps its own deterministic order and is
    /// unaffected.
    /// </summary>
    public Dictionary<string, string>? LaneSortStrategyOverrides { get; init; }

    /// <summary>
    /// Legacy per-project pipeline-step configuration. Map of pipeline step id
    /// (e.g. <c>aspect-code-quality</c>, <c>post-lint-scss</c>) to a
    /// per-step override of <c>enabled</c> / <c>mode</c> / <c>model</c>.
    /// A missing step id, or a null field inside an entry, falls through
    /// to the built-in pipeline default. The known step ids come from
    /// <c>PipelineCatalogue.All</c>; this map only overrides code-defined steps
    /// because the runtime maps each step id to a concrete service. The
    /// catalogue includes standard, read-only, and UI iteration
    /// step sets. The UI routing entry also accepts <c>maxIterations</c>.
    /// Resolution order for <c>model</c> is step -&gt;
    /// <see cref="OrchestratorModel"/> -&gt; global default -&gt; runtime default;
    /// for <c>mode</c> it is step -&gt; built-in default.
    /// Persisted in <c>project-settings.json</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, PipelineStepSetting>? PipelineSteps { get; init; }

    /// <summary>
    /// Per-project pipeline-step overrides keyed first by
    /// <see cref="PipelineTypes"/> and then by pipeline step id. Each type owns
    /// an independent chain configuration. Existing flat
    /// <see cref="PipelineSteps"/> values migrate to task, bug, and feature;
    /// planning intentionally starts from its lightweight catalogue defaults.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, PipelineStepSetting>>? PipelineStepsByType { get; init; }

    /// <summary>
    /// Optional per-project display/execution order for configurable pipeline
    /// pre/post steps. The list stores step ids in the operator's preferred
    /// order; ids not present in the list append in catalogue order so newly
    /// added steps stay visible after upgrades. Core remains fixed and always
    /// runs as the single agent step.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? PipelineStepOrder { get; init; }

    /// <summary>
    /// Optional per-type pre/post step order. Core remains fixed. Legacy flat
    /// order migrates to task, bug, and feature alongside the step settings.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, IReadOnlyList<string>>? PipelineStepOrderByType { get; init; }

    /// <summary>
    /// DEPRECATED (AGT-2302 / AGT-2376, remove after 2026-10-01). Capacity is a
    /// host fact now: the execution host carries one ceiling, one target load,
    /// and one ramp strategy for every project it claims for
    /// (<see cref="HostCapacityPolicy"/>, <c>PUT /api/clients/{id}/runner-capacity</c>).
    ///
    /// <para>
    /// The value stays readable so existing <c>project-settings.json</c> files
    /// keep meaning something during the migration: it seeds a host ceiling once
    /// (see <c>LeaseEndpoints.DeprecatedProjectCompatCeiling</c>) and the legacy
    /// in-process runner still reads it. No HTTP route and no UI control writes
    /// it any more.
    /// </para>
    /// </summary>
    public int MaxParallelism { get; init; } = 1;

    /// <summary>
    /// ADR-0052: branch that parallel task worktrees branch off and merge back
    /// into (the project's integration line). Blank means the repository's
    /// <c>origin/HEAD</c> branch. When parallelism is off
    /// (<see cref="MaxParallelism"/> == 1) the sequential runner keeps pushing
    /// to its configured target and this value is unused.
    /// </summary>
    public string IntegrationBranch { get; init; } = string.Empty;

    /// <summary>
    /// ADR-0052: how a finished task branch is folded back into
    /// <see cref="IntegrationBranch"/>. One of <see cref="IntegrationStrategies"/>
    /// (<c>direct-merge</c> default, or <c>pull-request</c>). Only consulted
    /// when <see cref="MaxParallelism"/> &gt; 1.
    /// </summary>
    public string IntegrationStrategy { get; init; } = IntegrationStrategies.DirectMerge;

    /// <summary>
    /// Slice P (ASS-1663): per-project build profile - the declared stack plus
    /// the install / build / test commands, lockfile paths, clean preserve-globs,
    /// and recycled-worktree pool size that drive the worktree pre/post steps.
    /// Null means "no profile declared" (legacy behaviour: the runner picks the
    /// project up with no onboarding gate). Once declared, the runner refuses
    /// auto-pickup until a green validation dry-run flips
    /// <see cref="BuildProfile.Status"/> to
    /// <see cref="BuildProfileStatuses.PipelineReady"/>. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public BuildProfile? BuildProfile { get; init; }

    /// <summary>
    /// Staged test policy. Unlike the pipeline step's global warn/fail switch,
    /// this selects the amount of test coverage per lane. The default for
    /// post-processing is <c>work-package</c>; a pre-main caller always overrides
    /// the lane policy with <c>full</c>.
    /// </summary>
    public TestExecutionPolicy? TestExecution { get; init; }

    /// <summary>
    /// Per-CLI permission / sandbox mode override. Map of <see cref="CliTypes"/>
    /// id (<c>claude</c> / <c>codex</c> / <c>gemini</c> / <c>copilot</c>) to a
    /// mode id from <see cref="CliPermissionModes"/>. A missing CLI key means
    /// "no project override" and resolves to the platform default
    /// (<see cref="CliPermissionModes.Yolo"/>) or, where detectable, the CLI's
    /// global config. The resolved mode is rendered to concrete flags by
    /// <see cref="CliPermissionFlags"/> on every spawn, so changes take effect
    /// on the next run without a backend restart. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public Dictionary<string, string>? CliModes { get; init; }

    /// <summary>
    /// Per-CLI context mode override (T1b / ASS-1742). Map of <see cref="CliTypes"/>
    /// id to a mode from <see cref="CliContextModes"/> (<c>clean</c> /
    /// <c>shared</c>). A missing CLI key means "no project override" and resolves
    /// to the platform default (<see cref="CliContextModes.Clean"/>). A task can
    /// further override this per-run via <see cref="TaskInfo.ContextMode"/>. The
    /// resolved mode decides whether the driver seeds an isolated per-run config
    /// home on spawn, so changes take effect on the next run without a backend
    /// restart. Persisted in <c>project-settings.json</c>.
    /// </summary>
    public Dictionary<string, string>? CliContextModes { get; init; }

    /// <summary>
    /// Model the epic planning/decomposition run uses (way 3): when a
    /// <see cref="TaskKinds.Epic"/> card is picked up, the runner runs a
    /// planning step that authors the sub-task list instead of a coding run.
    /// Null means "use the epic card's own <see cref="TaskInfo.Model"/>"; set
    /// it to bias decomposition toward a stronger (or cheaper) model than the
    /// sub-tasks themselves will run on. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public string? EpicPlanningModel { get; init; }

    /// <summary>
    /// Thinking / reasoning level for epic decomposition runs. Null means use
    /// the selected planning model's default capability level.
    /// </summary>
    public string? EpicPlanningThinkingLevel { get; init; }

    /// <summary>
    /// Where an epic decomposition run's generated sub-tasks land. False /
    /// null (default) lands them in <c>0-backlog</c> for human triage, exactly
    /// like the deterministic <c>POST /api/epics/{id}/sub-tasks</c> path. True
    /// lands them straight in <c>2-ready</c> so an auto-pickup project starts
    /// executing the plan without a manual triage pass. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public bool? EpicSubTasksToReady { get; init; }

    /// <summary>
    /// AGT-2028: per-project configuration for the opt-in <c>post-task-spawner</c>
    /// pipeline step. When set (and the step is enabled via
    /// <see cref="PipelineSteps"/>), a completed task whose change set the best
    /// available model judges relevant spawns a follow-up card in
    /// <see cref="TaskSpawnerConfig.TargetProject"/>. Null means "no spawn target
    /// configured" - the step records a skipped row and never fires. Kept a
    /// dedicated typed object (mirroring <see cref="BuildProfile"/> /
    /// <see cref="EpicPlanningModel"/>) rather than overloading the shared
    /// <see cref="PipelineStepSetting"/>, because the spawn target + lane + policy
    /// are specific to this step. The step's enablement, model, and CLI still flow
    /// through the standard <see cref="PipelineSteps"/> resolver. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public TaskSpawnerConfig? TaskSpawner { get; init; }

    /// <summary>
    /// AGT-2749: per-project override of the build/test gate-run budget, in
    /// seconds. Null falls back to <see cref="AgentStudio.Pipeline.GateRunBudgetPolicy"/>
    /// (a measured run-history p95 when enough history exists, otherwise
    /// <see cref="AgentStudio.Pipeline.GateRunBudgetDefaults.DefaultSeconds"/>).
    /// Set this when a project's suite legitimately runs longer than the
    /// platform default, so the gate stops misclassifying a slow-but-healthy
    /// suite as an infrastructure timeout.
    /// </summary>
    public int? BuildTestGateTimeoutSeconds { get; init; }
}

/// <summary>
/// Per-project spawn target + policy for the <c>post-task-spawner</c> pipeline
/// step (AGT-2028). Deliberately generic (not website-hardwired): any project's
/// pipeline can point at any other project and phrase its own relevance
/// question. The best available model evaluates relevance and generates the
/// follow-up prompt; the spawned card is worked by the target project's default
/// model.
/// </summary>
public record TaskSpawnerConfig
{
    /// <summary>
    /// Where a relevant change spawns a follow-up card. A filesystem watch path
    /// or a stable <c>PROJ-NNN</c> id (the id survives a folder move; both are
    /// accepted by the create path). Null/blank disables the step for the
    /// project even when the pipeline step itself is enabled.
    /// </summary>
    public string? TargetProject { get; init; }

    /// <summary>
    /// The operator's relevance question, injected into the evaluation prompt,
    /// e.g. "Is this change relevant to the public website (new feature,
    /// removed capability, changed behaviour)?". Null falls back to a generic
    /// relevance framing in the template.
    /// </summary>
    public string? RelevanceQuestion { get; init; }

    /// <summary>
    /// Lane the spawned card lands in: <see cref="TaskStates.Backlog"/>
    /// (default, triage - auto-pickup never reaches it) or
    /// <see cref="TaskStates.Ready"/> (queued to auto-run). Any other value is
    /// clamped to backlog (a freshly minted card must never land in a review
    /// lane).
    /// </summary>
    public string? SpawnLane { get; init; }

    /// <summary>
    /// Dedup budget: the maximum number of cards this step spawns per source
    /// task, across re-runs (default 1). The append-only spawn ledger in the
    /// source job's <c>.metadata/spawned-tasks.jsonl</c> enforces it, so a task
    /// re-processed by the reissue loop never double-spawns.
    /// </summary>
    public int? MaxPerSourceTask { get; init; }
}

/// <summary>
/// Run-condition vocabulary for <see cref="PipelineStepCondition.When"/>. A
/// step's condition decides whether it executes for a given task run, on top
/// of the enabled flag. <see cref="Always"/> is the default (run whenever the
/// step is enabled); <see cref="Never"/> keeps the override around without
/// firing. The remaining tokens gate on the run outcome or the task's own
/// classification.
/// </summary>
public static class PipelineStepConditions
{
    /// <summary>Run whenever the step is enabled (default).</summary>
    public const string Always = "always";

    /// <summary>Keep the override but never run the step.</summary>
    public const string Never = "never";

    /// <summary>Run only when the run ended in an abort/stop outcome.</summary>
    public const string OnAbort = "on-abort";

    /// <summary>Run only when the CLI process exited with a non-zero code.</summary>
    public const string OnNonzeroExit = "on-nonzero-exit";

    /// <summary>Run only when at least one review aspect failed.</summary>
    public const string OnAspectFail = "on-aspect-fail";

    /// <summary>
    /// Run only for a matching <see cref="TaskInfo.TaskType"/> (the condition
    /// value names the task type, e.g. <c>bug</c>).
    /// </summary>
    public const string TaskType = "task-type";

    /// <summary>
    /// Run only when the task carries a matching tag (the condition value names
    /// the tag).
    /// </summary>
    public const string Tag = "tag";

    /// <summary>Every known condition token, in display order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Always, Never, OnAbort, OnNonzeroExit, OnAspectFail, TaskType, Tag,
    ];

    /// <summary>Tokens whose semantics require a non-empty <see cref="PipelineStepCondition.Value"/>.</summary>
    public static readonly IReadOnlyList<string> ValueBearing = [TaskType, Tag];

    public static bool IsKnown(string? when) =>
        when != null && All.Contains(when, StringComparer.OrdinalIgnoreCase);

    public static bool RequiresValue(string? when) =>
        when != null && ValueBearing.Contains(when, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lower-cases and trims a token to its canonical form. Returns null for a
    /// null/blank/unknown token so callers can treat it as "no condition".
    /// </summary>
    public static string? Normalize(string? when)
    {
        if (string.IsNullOrWhiteSpace(when)) return null;
        var trimmed = when.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase)) return known;
        }
        return null;
    }
}

/// <summary>
/// Per-step run condition: a <see cref="When"/> token from
/// <see cref="PipelineStepConditions"/> plus an optional <see cref="Value"/>
/// used by the value-bearing tokens (<c>task-type</c>, <c>tag</c>). A null or
/// <see cref="PipelineStepConditions.Always"/> condition means "run whenever
/// the step is enabled".
/// </summary>
public record PipelineStepCondition
{
    public string When { get; init; } = PipelineStepConditions.Always;
    public string? Value { get; init; }
}

/// <summary>
/// Per-step project override stored in <see cref="ProjectSettings.PipelineSteps"/>.
/// Every field is nullable: null means "no override, use the pipeline /
    /// runtime default" so a partial entry (e.g. only a model choice) leaves
/// the other dimensions on their defaults.
/// </summary>
public record PipelineStepSetting
{
    /// <summary>
    /// Optional bounded iteration count for steps that own an iterative loop.
    /// Today this is consumed by the UI-pipeline routing step. Null preserves
    /// the catalogue default; unsupported steps ignore it.
    /// </summary>
    public int? MaxIterations { get; init; }

    /// <summary>
    /// Opts this LLM-backed step into the TokenEconomy recommendation path.
    /// An explicit per-step <see cref="Model"/> still wins. The runtime falls
    /// back to its normal model when no qualified economy model is available.
    /// </summary>
    public bool? EconomyModel { get; init; }

    /// <summary>
    /// When <c>false</c>, the step is skipped for this project. Null or
    /// <c>true</c> leaves the step enabled. Only honoured for steps the
    /// runtime can actually skip (today: the aspect post-steps and the
    /// lint-scss gate); the core agent run cannot be disabled.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Gate mode for steps that support it (<c>off</c> / <c>warn</c> /
    /// <c>fail</c>, see <c>PostStepMode</c>). Null falls through to the
    /// built-in default. Ignored for steps that have no gate semantics.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Model id that runs this step's LLM call (uses the shared CLI+model
    /// selector vocabulary). Null falls back to the project
    /// <see cref="ProjectSettings.OrchestratorModel"/>, then the global
    /// default model, then the runtime default. Only meaningful for steps that
    /// invoke an LLM (including aspects, abort review, grade, and drift);
    /// deterministic tool steps ignore it.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Optional CLI type for LLM-backed steps. Null means "use the step /
    /// runtime default". Deterministic tool steps ignore it.
    /// </summary>
    public string? CliType { get; init; }

    /// <summary>
    /// Optional thinking / reasoning level for this step's LLM call. Null
    /// falls through to the selected model's default level.
    /// </summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>
    /// Prompt override for LLM-backed steps. Null means "use the catalogue
    /// prompt template"; deterministic tool steps ignore it.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// SHA-256 of the shipped prompt default when <see cref="Prompt"/> was
    /// created. Kept beside the project override so the catalogue can warn
    /// when an application update changes the shipped default.
    /// </summary>
    public string? PromptBaseDefaultSha { get; init; }

    /// <summary>
    /// Shipped prompt content captured with <see cref="PromptBaseDefaultSha"/>.
    /// This is the project-level equivalent of the global override sidecar and
    /// supports the same detail comparison workflow.
    /// </summary>
    public string? PromptBaseDefaultContent { get; init; }

    /// <summary>
    /// Run condition gating whether this step executes for a given task run.
    /// Null (or an <see cref="PipelineStepConditions.Always"/> condition) means
    /// "run whenever the step is enabled".
    /// </summary>
    public PipelineStepCondition? Condition { get; init; }
}

public static class LaneSortStrategies
{
    /// <summary>
    /// User-managed order. Sort by <c>order</c> ASC + <c>key</c> desc as
    /// tiebreaker. Drag-and-drop on the kanban is only enabled on lanes set
    /// to this strategy.
    /// </summary>
    public const string Manual = "manual";

    /// <summary>Newest key on top. Sort by <c>key</c> desc + <c>createdAt</c> desc.</summary>
    public const string NewestFirst = "newest-first";

    /// <summary>Oldest key on top (FIFO triage). Sort by <c>key</c> asc + <c>createdAt</c> asc.</summary>
    public const string OldestFirst = "oldest-first";

    /// <summary>Most-recent activity on top. Sort by <c>lastActivity</c> desc + <c>order</c> asc.</summary>
    public const string LastActivity = "last-activity";

    /// <summary>
    /// Hybrid default: most-recently-entered-lane on top, with manually
    /// dragged cards pinned. Cards with an explicit <c>order</c> (i.e. not the
    /// 999 sentinel) cluster on top by <c>order</c> asc — these are the
    /// drag-pinned cards; the rest flow by <c>enteredLaneAt</c> desc so the
    /// newest arrival is on top. This is the default for every lane.
    /// </summary>
    public const string LaneEntry = "lane-entry";

    /// <summary>
    /// Internal auto-pickup priority. Sort by <c>order</c> asc + <c>lastActivity</c>
    /// asc. Reserved for the runner; not selectable in the project-settings UI.
    /// </summary>
    public const string PickupPriority = "pickup-priority";

    /// <summary>The sentinel <c>order</c> value meaning "not explicitly placed".</summary>
    public const int UnpinnedOrder = 999;

    /// <summary>All strategies including the internal pickup-priority.</summary>
    public static readonly string[] All =
        [Manual, NewestFirst, OldestFirst, LastActivity, LaneEntry, PickupPriority];

    /// <summary>Strategies surfaced in the project-settings UI dropdown.</summary>
    public static readonly string[] UserVisible =
        [LaneEntry, Manual, NewestFirst, OldestFirst, LastActivity];

    /// <summary>
    /// Default strategy used when a lane has no explicit override in
    /// <see cref="ProjectSettings.LaneSortStrategies"/>. Every lane now defaults
    /// to <see cref="LaneEntry"/>: the card that most recently entered the lane
    /// floats to the top, while a manual drag pins a card in place. A project
    /// can still override any lane via <c>LaneSortStrategyOverrides</c>.
    /// </summary>
    public static string GetDefaultForLane(string lane) => LaneEntry;

    /// <summary>
    /// Returns the configured strategy for a lane, falling back to
    /// <see cref="GetDefaultForLane"/> when the project has no override.
    /// Unknown strategy ids fall back to the default too.
    /// </summary>
    public static string Resolve(ProjectSettings settings, string lane)
    {
        if (settings.LaneSortStrategyOverrides != null
            && settings.LaneSortStrategyOverrides.TryGetValue(lane, out var configured)
            && IsValid(configured))
        {
            return configured;
        }
        return GetDefaultForLane(lane);
    }

    public static bool IsValid(string? strategy)
        => !string.IsNullOrWhiteSpace(strategy)
           && All.Contains(strategy, StringComparer.OrdinalIgnoreCase);

    public static bool IsUserSelectable(string? strategy)
        => !string.IsNullOrWhiteSpace(strategy)
           && UserVisible.Contains(strategy, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return Manual;
        var v = strategy.Trim();
        foreach (var s in All)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
                return s;
        return Manual;
    }

    /// <summary>
    /// Returns the comparer that implements <paramref name="strategy"/> for
    /// <see cref="TaskInfo"/>. Unknown strategy ids fall back to manual.
    /// </summary>
    public static IComparer<TaskInfo> GetComparer(string strategy)
    {
        return Normalize(strategy) switch
        {
            NewestFirst => Comparer<TaskInfo>.Create(CompareNewestFirst),
            OldestFirst => Comparer<TaskInfo>.Create(CompareOldestFirst),
            LastActivity => Comparer<TaskInfo>.Create(CompareLastActivityDesc),
            LaneEntry => Comparer<TaskInfo>.Create(CompareLaneEntry),
            PickupPriority => Comparer<TaskInfo>.Create(ComparePickupPriority),
            _ => Comparer<TaskInfo>.Create(CompareManual),
        };
    }

    private static int CompareManual(TaskInfo a, TaskInfo b)
    {
        var byOrder = a.Order.CompareTo(b.Order);
        if (byOrder != 0) return byOrder;
        // Stable tiebreaker: newer key on top so two cards at order 999
        // sort consistently. CompareKeyDesc handles null keys safely.
        return CompareKeyDesc(a, b);
    }

    /// <summary>
    /// Hybrid lane-entry order. A card is "pinned" when it carries an explicit
    /// <c>order</c> (anything other than the <see cref="UnpinnedOrder"/>
    /// sentinel) — those are the cards a user dragged into place. Pinned cards
    /// cluster on top sorted by <c>order</c> asc; everything else flows below
    /// them by <c>enteredLaneAt</c> desc (newest arrival on top), with key desc
    /// as a stable tiebreaker. This lets a manual drag override the time-based
    /// flow without disabling it for the rest of the lane.
    /// </summary>
    private static int CompareLaneEntry(TaskInfo a, TaskInfo b)
    {
        var aPinned = a.Order != UnpinnedOrder;
        var bPinned = b.Order != UnpinnedOrder;
        if (aPinned != bPinned) return aPinned ? -1 : 1;
        if (aPinned)
        {
            var byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0) return byOrder;
            return CompareKeyDesc(a, b);
        }
        var byEntry = b.EnteredLaneAt.CompareTo(a.EnteredLaneAt);
        if (byEntry != 0) return byEntry;
        return CompareKeyDesc(a, b);
    }

    private static int CompareNewestFirst(TaskInfo a, TaskInfo b)
    {
        var byKey = CompareKeyDesc(a, b);
        if (byKey != 0) return byKey;
        return b.CreatedAt.CompareTo(a.CreatedAt);
    }

    private static int CompareOldestFirst(TaskInfo a, TaskInfo b)
    {
        var byKey = CompareKeyAsc(a, b);
        if (byKey != 0) return byKey;
        return a.CreatedAt.CompareTo(b.CreatedAt);
    }

    private static int CompareLastActivityDesc(TaskInfo a, TaskInfo b)
    {
        var byActivity = b.LastActivity.CompareTo(a.LastActivity);
        if (byActivity != 0) return byActivity;
        return a.Order.CompareTo(b.Order);
    }

    private static int ComparePickupPriority(TaskInfo a, TaskInfo b)
    {
        var byOrder = a.Order.CompareTo(b.Order);
        if (byOrder != 0) return byOrder;
        return a.LastActivity.CompareTo(b.LastActivity);
    }

    /// <summary>
    /// Compare reference keys (e.g. <c>ATP-130</c>) in semantic order: split
    /// at the dash so the numeric suffix sorts numerically, not
    /// lexicographically. Jobs with a null key fall to the end of the lane.
    /// </summary>
    private static int CompareKeyAsc(TaskInfo a, TaskInfo b)
    {
        var ka = a.Key;
        var kb = b.Key;
        if (string.IsNullOrEmpty(ka) && string.IsNullOrEmpty(kb)) return 0;
        if (string.IsNullOrEmpty(ka)) return 1;
        if (string.IsNullOrEmpty(kb)) return -1;
        return KeyComparer.Compare(ka, kb);
    }

    private static int CompareKeyDesc(TaskInfo a, TaskInfo b)
    {
        // Keep "null keys to the bottom" regardless of direction; a naive
        // CompareKeyAsc(b, a) would float nulls to the top in desc order.
        var ka = a.Key;
        var kb = b.Key;
        if (string.IsNullOrEmpty(ka) && string.IsNullOrEmpty(kb)) return 0;
        if (string.IsNullOrEmpty(ka)) return 1;
        if (string.IsNullOrEmpty(kb)) return -1;
        return -KeyComparer.Compare(ka, kb);
    }

    private static class KeyComparer
    {
        public static int Compare(string a, string b)
        {
            var dashA = a.LastIndexOf('-');
            var dashB = b.LastIndexOf('-');
            if (dashA > 0 && dashB > 0)
            {
                var prefixA = a.AsSpan(0, dashA);
                var prefixB = b.AsSpan(0, dashB);
                var byPrefix = prefixA.CompareTo(prefixB, StringComparison.OrdinalIgnoreCase);
                if (byPrefix != 0) return byPrefix;
                if (int.TryParse(a.AsSpan(dashA + 1), out var nA)
                    && int.TryParse(b.AsSpan(dashB + 1), out var nB))
                {
                    return nA.CompareTo(nB);
                }
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public static class AutoPushStrategies
{
    public const string Never = "never";
    public const string OnCompleted = "on-completed";
    public const string AlwaysImmediate = "always-immediate";

    public static readonly string[] All = [Never, OnCompleted, AlwaysImmediate];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AlwaysImmediate;
        var v = value.Trim();
        foreach (var strategy in All)
            if (string.Equals(strategy, v, StringComparison.OrdinalIgnoreCase))
                return strategy;
        return AlwaysImmediate;
    }
}

public static class IntegrationStrategies
{
    public const string DirectMerge = "direct-merge";
    public const string PullRequest = "pull-request";

    public static readonly string[] All = [DirectMerge, PullRequest];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DirectMerge;
        var v = value.Trim();
        foreach (var strategy in All)
            if (string.Equals(strategy, v, StringComparison.OrdinalIgnoreCase))
                return strategy;
        return DirectMerge;
    }
}

/// <summary>
/// Onboarding lifecycle of a project's <see cref="BuildProfile"/> (Slice P /
/// ASS-1663). A project first becomes <see cref="PipelineReady"/> after a green
/// local validation or matching green remote Review. A validated profile edit
/// keeps a bounded revalidation grace window; otherwise the runner refuses
/// auto-pickup (<see cref="AgentStudio.Runner.BuildProfileGate"/>).
/// A project that has never declared a profile carries no profile at all (null)
/// and keeps the legacy "pickup allowed" behaviour, so existing projects are
/// untouched.
/// </summary>
public static class BuildProfileStatuses
{
    /// <summary>Stack + commands declared, but no green dry-run yet. Pickup is blocked.</summary>
    public const string Declared = "declared";

    /// <summary>A validation dry-run is currently running. Pickup is blocked.</summary>
    public const string Validating = "validating";

    /// <summary>The dry-run went green: install + build succeeded. Pickup is allowed.</summary>
    public const string PipelineReady = "pipeline-ready";

    /// <summary>The dry-run failed (install or build red). Pickup is blocked until re-validated.</summary>
    public const string ValidationFailed = "validation-failed";

    public static readonly string[] All = [Declared, Validating, PipelineReady, ValidationFailed];

    /// <summary>
    /// Canonicalizes a status token. A null/blank/unknown value collapses to
    /// <see cref="Declared"/> - the safe default, since an un-validated profile
    /// must never be treated as pipeline-ready.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Declared;
        var v = value.Trim();
        foreach (var s in All)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
                return s;
        return Declared;
    }
}

/// <summary>
/// Per-project build profile (Slice P / ASS-1663; builds on ASS-850/878 and the
/// ADR-0052 parallel-execution work). Declares how a project's stack is
/// installed, built, and tested so the worktree pre/post steps (deps-ensure,
/// clean, build gate) are driven by registered commands instead of hardcoded
/// npm/dotnet assumptions. Lives on <see cref="ProjectSettings"/> and is
/// persisted in <c>project-settings.json</c>.
///
/// <para>
/// A project with no profile (null) behaves exactly as today. Once a profile is
/// declared it gates auto-pickup: initial pickup requires green validation.
/// Later edits retain a bounded grace window while matching proof is pending.
/// </para>
/// </summary>
public record BuildProfile
{
    /// <summary>
    /// Maximum number of coding runs admitted after an edit to a previously
    /// validated profile while an exact-profile revalidation is still pending.
    /// </summary>
    public const int DefaultRevalidationGraceRuns = 3;

    /// <summary>
    /// Declared stack id (free-form, e.g. <c>node</c>, <c>dotnet</c>,
    /// <c>node+dotnet</c>). Informational - drives onboarding defaults and the
    /// timeline label; the runner keys behaviour off the concrete commands below.
    /// </summary>
    public string? Stack { get; init; }

    /// <summary>
    /// Dependency-install command run once per fresh/recycled worktree before the
    /// build (e.g. <c>npm ci</c>, <c>dotnet restore</c>). Null/blank = no install
    /// step.
    /// </summary>
    public string? InstallCmd { get; init; }

    /// <summary>
    /// Ordered build commands run after install (e.g. <c>["npm run build",
    /// "dotnet build"]</c>). The dry-run is green only when every one exits zero.
    /// </summary>
    public IReadOnlyList<string>? BuildCmds { get; init; }

    /// <summary>
    /// Ordered test commands (e.g. <c>["npm test", "dotnet test"]</c>). Not part
    /// of the install/build dry-run gate; registered here so a later verify/test
    /// post-step can drive them.
    /// </summary>
    public IReadOnlyList<string>? TestCmds { get; init; }

    /// <summary>
    /// Lockfile paths (worktree-relative, e.g. <c>["package-lock.json",
    /// "frontend/package-lock.json"]</c>) whose content hash decides whether a
    /// recycled worktree must re-install. Feeds the deps-ensure pre-step
    /// (<see cref="AgentStudio.TaskServer.Contracts.DependencyPreparationState"/>).
    /// </summary>
    public IReadOnlyList<string>? Lockfiles { get; init; }

    /// <summary>
    /// Glob patterns the worktree clean step must preserve (e.g.
    /// <c>["node_modules", ".angular", "bin", "obj"]</c>) so an expensive
    /// dependency/build cache survives recycling. Maps to the clean step's
    /// exclude list.
    /// </summary>
    public IReadOnlyList<string>? PreserveGlobs { get; init; }

    /// <summary>
    /// Size of the recycled-worktree pool kept warm for this project. Null =
    /// "no dedicated pool" (worktrees are created/torn down per task). When set,
    /// the runner keeps up to this many worktrees with preserved caches around.
    /// </summary>
    public int? PoolSize { get; init; }

    /// <summary>Onboarding status; one of <see cref="BuildProfileStatuses"/>.</summary>
    public string Status { get; init; } = BuildProfileStatuses.Declared;

    /// <summary>UTC instant of the last green validation dry-run, or null if never green.</summary>
    public DateTime? LastValidatedAt { get; init; }

    /// <summary>Short reason from the last failed validation dry-run, or null.</summary>
    public string? LastValidationError { get; init; }

    /// <summary>
    /// Stable fingerprint of the profile fields that affect preparation and
    /// verification. Remote proof is accepted only for this exact value.
    /// </summary>
    public string? ValidationFingerprint { get; init; }

    /// <summary>
    /// True when the current profile differs from the last validated profile.
    /// A bounded grace window keeps pickup open while fresh proof is produced.
    /// </summary>
    public bool RevalidationPending { get; init; }

    /// <summary>Number of coding-run admissions left in the revalidation grace window.</summary>
    public int RevalidationGraceRunsRemaining { get; init; }

    /// <summary>UTC instant of the last green verification executed by a remote Review Executor.</summary>
    public DateTime? LastRemoteValidationAt { get; init; }

    /// <summary>ReviewAttempt that last proved this exact profile on a remote executor.</summary>
    public string? LastRemoteValidationAttemptId { get; init; }

    /// <summary>Remote executor identity that produced the last green profile proof.</summary>
    public string? LastRemoteValidationRunnerId { get; init; }

    /// <summary>Immutable result SHA covered by the last green remote profile proof.</summary>
    public string? LastRemoteValidationResultSha { get; init; }
}

/// <summary>Stable test levels used in settings, evidence, and gate logs.</summary>
public static class TestExecutionLevels
{
    /// <summary>
    /// Compile evidence only: the derived build / lint commands run, every test
    /// command (including the continuous baseline) is deliberately left out.
    /// This is the cheap stage the pre-develop merge gate keeps for non-frontend
    /// deliveries. Frontend merge results use the bounded work-package level;
    /// the full suite remains a promotion-only boundary.
    /// </summary>
    public const string BuildOnly = "build-only";
    public const string Continuous = "continuous";
    public const string WorkPackage = "work-package";
    public const string Full = "full";

    public static string Normalize(string? value, string fallback = WorkPackage)
        => value?.Trim().ToLowerInvariant() switch
        {
            BuildOnly => BuildOnly,
            Continuous => Continuous,
            WorkPackage => WorkPackage,
            Full => Full,
            _ => fallback,
        };
}

/// <summary>
/// Per-project staged testing configuration. Commands in
/// <see cref="ContinuousCommands"/> form the small fixed baseline and are
/// reporting-only during a work-package run. The impacted suite is selected
/// from the diff, explicit impact rules, Test Hub history, and optionally an
/// LLM adviser. <see cref="LaneLevels"/> makes the policy lane-specific.
/// </summary>
public sealed record TestExecutionPolicy
{
    public Dictionary<string, string>? LaneLevels { get; init; }
    public IReadOnlyList<string>? ContinuousCommands { get; init; }
    public IReadOnlyList<TestImpactRule>? ImpactRules { get; init; }
    public string? TestHubHistoryPath { get; init; }
    public bool LlmSelectionEnabled { get; init; }
    public string? LlmCliType { get; init; }
    public string? LlmModel { get; init; }
    public string? LlmThinkingLevel { get; init; }
}

/// <summary>
/// Explicit project-specific impact mapping used when repository conventions
/// cannot infer a test project or component command.
/// </summary>
public sealed record TestImpactRule
{
    public IReadOnlyList<string> PathPrefixes { get; init; } = [];
    public IReadOnlyList<string> TestCommands { get; init; } = [];
    public string? Reason { get; init; }
}
