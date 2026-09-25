/**
 * View models the Overview pane's pipeline block renders, split out of
 * `overview-pane.component.ts` in AGT-2819 so the controller, its extracted
 * child components, and the pure builders can all name the same shapes without
 * importing each other.
 */
import type { CliType } from '../../../../../models/task.model';
import type {
  PipelinePricingGap,
  PipelineAspectEvidence,
  PipelineStepConfig,
  PipelineStepStatus,
  StepKind,
  StepRunMode,
} from '../../../../task-pipeline';
import type { StructuredTooltip } from 'coding-agent-chat/shared';

export interface PipelineRowVm {
  id: string;
  label: string;
  kind: StepKind;
  phaseKey: PipelinePhaseKey;
  phaseLabel: string;
  phaseDescription: string;
  startsPhase: boolean;
  /**
   * 'parallel' for the read-only aspect reviews that run concurrently in the
   * orchestrator pool; 'sequential' for the core run and the single final
   * verdict. Drives the "Parallel" badge so the two phases read as distinct.
   */
  runMode: StepRunMode;
  /** True only for `post-orchestrator-decision`. */
  isFinalVerdict: boolean;
  /** Historical rows are read-only evidence and never use live state colour. */
  historical: boolean;
  enabled: boolean;
  canDisable: boolean;
  hasExecution: boolean;
  config: PipelineStepConfig | null;
  /** Effective display status: 'disabled' for project-disabled steps. */
  status: PipelineStepStatus | 'disabled' | 'not-run';
  /** Failure/skip detail, plus honest coverage scope for a passed staged test gate. */
  statusTooltip: StructuredTooltip | null;
  /** Small causal note for the designed skip cascade after an early escalate. */
  skipHint: string | null;
  /** This local step is structurally absent from the remote execution route. */
  remoteNotApplicable: boolean;
  /** A required build/test gate was skipped instead of reaching a verdict. */
  attentionRequired: boolean;
  /** Remote Review Plane explanation when this is its projected decision row. */
  remoteReviewDetail: string | null;
  model: string | null;
  thinkingLevel: string | null;
  cliType: CliType | null;
  /**
   * Whether {@link model} is the pre-run resolved effective model (no run has
   * recorded one yet) vs the model an actual execution used. Drives a subtler
   * "will run on" presentation before the run.
   */
  modelIsResolved: boolean;
  /** Tooltip explaining where {@link model} comes from (the resolution chain). */
  modelTooltip: StructuredTooltip | null;
  /**
   * Whether this row exposes an inline per-step agent selector. The Overview
   * rows now only display the resolved model; per-step model changes live in
   * project/global configuration instead of individual aspect rows.
   */
  modelEditable: boolean;
  /**
   * The raw per-step model override stored for this step (`''` = inherit), as
   * opposed to {@link model} which is the resolved effective model. Bound to
   * the inline selector so it reflects the persisted knob, not the inherited
   * value.
   */
  modelOverride: string;
  thinkingLevelOverride: string | null;
  verdict: string | null;
  /**
   * Structured tooltip for the verdict pill, built from the per-aspect
   * concern summary. Null unless the step flagged a concern, so a pass
   * verdict never grows a misleading tooltip.
   */
  concernTooltip: StructuredTooltip | null;
  /** One-line conclusion for every completed aspect, including clean passes. */
  aspectSummary: string | null;
  /** Latest-first report, raw-log, and grade links for Remote Review attempts. */
  aspectEvidence: PipelineAspectEvidence[];
  /**
   * Always-present "what does this step do" tooltip shown on hovering the
   * step name. Keyed by step id with a per-kind fallback so a future
   * catalogue step still explains itself rather than rendering bare.
   */
  explanation: StructuredTooltip;
  /** Recorded wall-clock duration of the step in ms; 0 when not yet run. */
  durationMs: number;
  /** ISO start stamp from the execution record; null until the step starts. */
  startedAt: string | null;
  /** ISO end stamp; null while running or before the step is reached. */
  completedAt: string | null;
  tokenUsageSource: string | null;
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
  /** False when the historical price resolver could not price all usage. */
  costKnown: boolean;
  unpricedRuns: number;
  pricingGaps: PipelinePricingGap[];
  tokenTooltip: StructuredTooltip | null;
  costTooltip: StructuredTooltip | null;
}

export type PipelinePhaseKey = 'pre' | 'core' | 'aspect' | 'tool' | 'analysis' | 'decision' | 'drift';

export interface PipelinePhaseVm {
  key: PipelinePhaseKey;
  label: string;
  description: string;
}

export interface PipelineTotalVm {
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
  unpricedRuns: number;
  pricingGaps: PipelinePricingGap[];
  tokenTooltip: StructuredTooltip | null;
  costTooltip: StructuredTooltip | null;
}

export interface TokenBreakdownRowVm {
  label: string;
  tokens: number;
  costUsd: number;
}

/**
 * One run in the Runs chip strip. The current run is written out (number,
 * status dot, OK/fail counter, duration); prior runs render as compact
 * clickable mini chips, newest first. Per-run tokens / cost live in the
 * dedicated tokens-by-model section, so the strip only needs the at-a-glance
 * outcome and timing.
 */
export interface PipelineRunOptionVm {
  attempt: number;
  current: boolean;
  startedAt: string | null;
  durationMs: number;
  passed: number;
  failed: number;
  /** Text result glyph for the mini chip: '✓' clean, '✗' had failures, '·' nothing ran. */
  glyph: string;
  /** Outcome class driving the chip / status-dot colour. */
  kind: 'pass' | 'fail' | 'pending';
  /** Honest label when no step reached a pass/fail terminal state. */
  emptyOutcomeLabel: 'pending' | 'not run';
  /** Compact hover summary: "N OK M fail · 3m34s · 6h ago". */
  tooltip: StructuredTooltip;
}
