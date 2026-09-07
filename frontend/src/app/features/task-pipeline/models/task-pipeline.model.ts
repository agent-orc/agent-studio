/**
 * Wire types for `GET /api/tasks/{id}/pipeline`. Mirrors the backend
 * `TaskPipeline` / `PipelineExecutionRecord` / `PipelineCostSummary`
 * shapes (System.Text.Json camelCases property names and serialises the
 * enums as camelCase strings via the global `JsonStringEnumConverter`).
 */

export type StepKind = 'module' | 'core' | 'aspect' | 'orchestrator' | 'tool' | 'analysis' | 'drift';
export type StepRunMode = 'sequential' | 'parallel';
export type PipelineStepStatus =
  | 'pending'
  | 'running'
  | 'passed'
  | 'failed'
  | 'skipped'
  | 'planned'
  | 'notApplicable';

/** Static metadata for one step in the pipeline catalogue. */
export interface PipelineStep {
  id: string;
  displayName: string;
  kind: StepKind;
  runMode: StepRunMode;
  dependsOn: string[];
  model?: string | null;
  cliType?: string | null;
  /** Technology marker for stack-specific steps, e.g. Angular. */
  framework?: string | null;
  timeoutMs?: number | null;
  idempotent: boolean;
  stub: boolean;
  /** Implemented step that waits for an explicit operator trigger. */
  deferred?: boolean;
}

/** The full static pipeline definition this job targets. */
export interface TaskPipeline {
  id: string;
  displayName: string;
  version: number;
  pre: PipelineStep[];
  core: PipelineStep[];
  post: PipelineStep[];
  /** Flattened pre+core+post in order (backend computed property). */
  allSteps?: PipelineStep[];
}

/** One recorded step execution from `pipeline-execution.json`. */
export interface PipelineStepExecution {
  stepId: string;
  kind: StepKind;
  /** Pipeline attempt epoch that owns this row. Null only on legacy records. */
  attempt?: number | null;
  model?: string | null;
  thinkingLevel?: string | null;
  recommendedModel?: string | null;
  recommendedThinkingLevel?: string | null;
  /** Effective route source: policy, policy-economy, or task-override. */
  selectionSource?: string | null;
  estimatedSavingsPercent?: number | null;
  status: PipelineStepStatus;
  startedAt?: string | null;
  completedAt?: string | null;
  durationMs: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  tokenUsageSource?: string | null;
  reason?: string | null;
  /** Task-folder-relative detailed evidence artifact for this step. */
  evidenceRef?: string | null;
  verdict?: string | null;
  /**
   * Human-readable concern detail for aspect steps with a non-pass
   * verdict, lifted from `aspect-{id}.md` frontmatter by the backend.
   * Drives the tooltip on the CONCERNS pill in the Overview pipeline.
   */
  verdictSummary?: string | null;
}

/** The persisted execution record (null when the job never ran a pipeline). */
export interface PipelineExecutionRecord {
  pipelineId: string;
  pipelineVersion: number;
  jobId: string;
  project: string;
  startedAt: string;
  completedAt?: string | null;
  steps: PipelineStepExecution[];
  /**
   * 1-based run counter. A re-run / re-issue starts a fresh record and
   * increments this; anything above 1 means the pipeline was restarted, so
   * the Overview pipeline can flag it as a new run.
   */
  attempt?: number;
  /**
   * Prior completed runs for this job, most-recent first, so old step runs
   * stay distinguishable from the current ones after a restart. Each entry
   * keeps its own `steps` but carries an empty `previousAttempts` (the chain
   * is flattened, not nested) and is bounded to the last few runs.
   */
  previousAttempts?: PipelineExecutionRecord[];
}

/** One unresolved historical model-price reason and the runs it affects. */
export interface PipelinePricingGap {
  modelId: string;
  reason: string;
  affectedRuns: number;
}

/** Derived per-step cost (USD) for one recorded step. */
export interface PipelineStepCost {
  stepId: string;
  kind: StepKind;
  model?: string | null;
  tokenUsageSource?: string | null;
  /** False when the historical resolver has no price for this usage. */
  modelKnown: boolean;
  pricingGaps?: PipelinePricingGap[];
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  totalTokens: number;
  inputCostUsd: number;
  outputCostUsd: number;
  cacheReadCostUsd: number;
  cacheCreationCostUsd: number;
  costUsd: number;
}

/** Per-step rows plus the task total. */
export interface PipelineCostSummary {
  steps: PipelineStepCost[];
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheReadTokens: number;
  totalCacheCreationTokens: number;
  totalTokens: number;
  totalInputCostUsd: number;
  totalOutputCostUsd: number;
  totalCacheReadCostUsd: number;
  totalCacheCreationCostUsd: number;
  totalCostUsd: number;
  anyModelUnknown: boolean;
  unpricedRuns?: number;
  pricingGaps?: PipelinePricingGap[];
}

/**
 * Token + cost rollup for one model, summed across the steps that ran on
 * it within a run (or across all runs for the grand total). Mirrors backend
 * `PipelineModelTokenUsage`. `modelKnown` false means price data is unavailable.
 */
export interface PipelineModelTokenUsage {
  model: string;
  modelKnown: boolean;
  unpricedRuns?: number;
  pricingGaps?: PipelinePricingGap[];
  steps: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  totalTokens: number;
  costUsd: number;
}

/**
 * One pipeline run (attempt) with its tokens grouped per model. `current`
 * marks the live run; older runs come from previous attempts. Mirrors
 * backend `PipelineRunTokenUsage`.
 */
export interface PipelineRunTokenUsage {
  attempt: number;
  current: boolean;
  startedAt: string;
  completedAt?: string | null;
  models: PipelineModelTokenUsage[];
  totalTokens: number;
  totalCostUsd: number;
  anyModelUnknown: boolean;
  pricingGaps?: PipelinePricingGap[];
  /** False when this visible session run has no attributable token telemetry. */
  tokenUsageAvailable?: boolean;
}

/**
 * Per-model token usage for one task across every run: a per-run breakdown
 * plus a grand total summing each model over all runs. Mirrors backend
 * `PipelineModelUsageSummary`. Powers the Overview "RUNS - tokens by model"
 * surface.
 */
export interface PipelineModelUsageSummary {
  runs: PipelineRunTokenUsage[];
  totalByModel: PipelineModelTokenUsage[];
  totalTokens: number;
  totalCostUsd: number;
  anyModelUnknown: boolean;
  unpricedRuns?: number;
  pricingGaps?: PipelinePricingGap[];
  /** Visible session runs for which no token telemetry was recorded. */
  missingTokenRuns?: number;
}

/** Per-project override resolved for one step (from project-settings.json). */
export interface PostStepActivation {
  state: 'active' | 'inactive' | 'skipped';
  source: 'global' | 'project' | 'condition';
  /** Backend-owned explanation of the exact effective source / condition. */
  reason: string;
}

export interface PipelineStepConfig {
  enabled: boolean;
  /** Whether this catalogue step is optional and may be toggled by an operator. */
  canDisable?: boolean;
  cliType?: string | null;
  model?: string | null;
  thinkingLevel?: string | null;
  mode?: string | null;
  /** Raw project-level prompt override, preserved by inline enable changes. */
  prompt?: string | null;
  /** Raw project-level run condition, preserved by inline enable changes. */
  condition?: PipelineStepCondition | null;
  /**
   * Effective model the step WILL run on before any run, resolved the same way
   * the runtime resolves it (step override -> project model -> global ->
   * catalogue -> runtime default). Null for deterministic / core steps that do
   * not resolve a per-step LLM model. Lets the Overview pipeline show the model
   * pre-run, not just after a recorded execution.
   */
  resolvedModel?: string | null;
  /**
   * Where {@link resolvedModel} came from: `step` | `project` | `global` |
   * `catalogue` | `runtime`. Drives the "from project / default" hint so the
   * operator can see the hierarchy at a glance. Null when no model resolves.
   */
  modelSource?: string | null;
  /** Whether enabled state comes from an explicit project override or the catalogue default. */
  enabledSource?: 'project' | 'catalogue';
  /** Effective post-step state and provenance. The frontend renders this verbatim. */
  activation?: PostStepActivation | null;
}

export interface OnDemandPostStepAttempt {
  stepId: string;
  attempt: number;
  status: string;
  summary: string;
  startedAt: string;
  finishedAt: string;
  durationMs: number;
  artifactRef?: string | null;
}

/**
 * One row in `GET /api/projects/pipeline-catalogue`. The capability flags
 * tell the Settings UI which controls a step accepts: a model picker
 * (`usesModel`), a gate-mode select (`supportsMode`), an enable toggle
 * (`canDisable`).
 */
export interface PipelineCatalogueStep {
  id: string;
  pipelineId?: string | null;
  displayName: string;
  kind: StepKind;
  appliesTo?: 'angular' | 'dotnet' | 'node' | 'any';
  applicable?: boolean;
  effectiveExecution?: EffectivePipelineStepExecution;
  /** Live quota admission preview for the next launch of this model-backed step. */
  effectiveExecutionSpec?: EffectivePipelineAgentSpec | null;
  phase?: string | null;
  runMode?: StepRunMode | null;
  dependsOn?: string[] | null;
  idempotent?: boolean | null;
  stub?: boolean | null;
  deferred?: boolean | null;
  model?: string | null;
  resolvedModel?: string | null;
  modelSource?: string | null;
  resolvedThinkingLevel?: string | null;
  thinkingLevelSource?: string | null;
  usesModel: boolean;
  supportsEconomyModel?: boolean;
  usesPrompt: boolean;
  supportsMode: boolean;
  cliType?: string | null;
  /** Technology marker for stack-specific steps, e.g. Angular. */
  framework?: string | null;
  promptTemplate?: string | null;
  canDisable: boolean;
  /**
   * Initial toggle state when the project has no explicit override. Drift
   * post-steps ship `false` (opt-in, expensive); every other step is `true`.
   */
  defaultEnabled: boolean;
  /**
   * Whether the runtime evaluates a per-step run condition for this step.
   * Core cannot be conditionally skipped; configurable pre/post steps use this
   * to render the condition control in the project settings pipeline editor.
   */
  supportsCondition: boolean;
  supportsMaxIterations?: boolean;
  defaultMaxIterations?: number | null;
}

export interface EffectivePipelineAgentSpec {
  cliType: string;
  model?: string | null;
  thinkingLevel?: string | null;
  outcome: string;
  isFallback: boolean;
  primaryCliType: string;
  reason: string;
  activatedAt?: string | null;
  resetAt?: string | null;
}

/**
 * Per-step run condition: a `when` token plus an optional `value` used by the
 * value-bearing tokens (`task-type`, `tag`). An absent condition (or `always`)
 * means "run whenever the step is enabled".
 */
export interface PipelineStepCondition {
  when: PipelineStepConditionToken;
  value?: string | null;
}

/** Run-condition vocabulary, mirrors backend `PipelineStepConditions`. */
export type PipelineStepConditionToken =
  | 'always'
  | 'never'
  | 'on-abort'
  | 'on-nonzero-exit'
  | 'on-aspect-fail'
  | 'task-type'
  | 'tag';

/** Envelope of `GET /api/projects/pipeline-catalogue`. */
export interface PipelineCatalogue {
  pipelineId: string;
  pipelineType?: PipelineType;
  detectedStacks?: string[];
  steps: PipelineCatalogueStep[];
}

/** Editable pipeline configuration types. */
export type PipelineType = 'task' | 'bug' | 'feature' | 'planning';

export interface EffectivePipelineCommand {
  workingSubdir: string;
  command: string;
}

export interface EffectivePipelineStepExecution {
  executionKind: 'shell' | 'internal';
  source: string;
  commands: EffectivePipelineCommand[];
}

export interface PipelineStepProbeResult {
  stepId: string;
  status: 'passed' | 'failed' | 'skipped' | 'not-applicable' | 'unavailable';
  applicable: boolean;
  exitCode?: number | null;
  durationMs: number;
  output: string;
  queueWaitMs: number;
}

/**
 * Raw per-step override as persisted in project-settings.json (the value
 * type of the `pipelineSteps` map on the project-settings projection). All
 * fields optional; an absent field means "fall through to the built-in
 * default". `enabled` is nullable on the wire (null/true both mean on).
 */
export interface PipelineStepSetting {
  enabled?: boolean | null;
  economyModel?: boolean | null;
  maxIterations?: number | null;
  mode?: string | null;
  cliType?: string | null;
  model?: string | null;
  thinkingLevel?: string | null;
  prompt?: string | null;
  condition?: PipelineStepCondition | null;
}

/**
 * One raw step-call prompt recorded at central dispatch into
 * `.metadata/prompts.jsonl` (the "Rohdaten" side of the prompt-completeness
 * principle). Captures the final prompt text a one-shot step (aspect,
 * code-review-grade, ...) handed to the CLI plus the provenance needed to
 * attribute it to a pipeline step. Keyed to the matching Overview step row
 * via {@link stepId}.
 */
export interface StepPromptEntry {
  /** UTC dispatch time (ISO-8601). */
  at: string;
  /** Pipeline step id this prompt belongs to, e.g. `aspect-requirement-fit`. */
  stepId: string;
  /** Runtime template the prompt was rendered from; null when built inline. */
  templateRef?: string | null;
  /** Model the prompt was sent to. */
  model?: string | null;
  /** CLI the prompt was sent through (lowercase). */
  cli?: string | null;
  /** Usage-attribution source tag, when the call site supplied one. */
  source?: string | null;
  /** The final, raw prompt text exactly as piped to the CLI. */
  prompt: string;
}

/** Envelope of `GET /api/tasks/{id}/step-prompts`. */
export interface StepPromptsResponse {
  prompts: StepPromptEntry[];
}

/** Full response envelope of the pipeline read endpoint. */
export interface TaskPipelineResponse {
  pipeline: TaskPipeline;
  execution: PipelineExecutionRecord | null;
  cost: PipelineCostSummary;
  /**
   * Per-model tokens grouped per run plus a grand total over all runs.
   * Optional so older fixtures / responses without it still type-check;
   * the backend always emits it (possibly empty).
   */
  tokensByModel?: PipelineModelUsageSummary | null;
  config: Record<string, PipelineStepConfig>;
  /**
   * Step id to verified job-root result file. Entries only exist when the
   * backend found the file on disk, so the UI never guesses result presence
   * from a step kind or terminal status.
   */
  resultFiles?: Record<string, string>;
  /** Card-owned additions and append-only attempts from individual post-step runs. */
  onDemand?: {
    plannedStepIds: string[];
    attempts: OnDemandPostStepAttempt[];
  };
}
