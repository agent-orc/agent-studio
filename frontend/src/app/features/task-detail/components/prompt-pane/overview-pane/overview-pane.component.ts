import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, ViewChild, computed, effect, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskState, type CliType, type PromoteToCodingResponse, type TaskInfo } from '../../../../../models/task.model';
import { CreateTaskFormService, type PendingAttachment } from '../../../../board';
import type { CliModelInfo } from '../../../../cli';
import type { RunRecord } from '../../../../run-timeline';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { CompletionLoopIndicatorComponent } from '../../../../task-timeline';
import { AgentWorkSummaryPollService } from '../../../../polling/services/agent-work-summary-poll.service';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { TaskTimelinePollService } from '../../../../polling/services/task-timeline-poll.service';
import type {
  PipelineExecutionRecord,
  PipelinePricingGap,
  PipelineStep,
  PipelineStepConfig,
  PipelineStepStatus,
  TaskPipelineResponse,
  StepKind,
  StepRunMode,
} from '../../../../task-pipeline';
import { ClientService } from '../../../../../services/client.service';
import { CliModelSelectorComponent } from '../../../../../components/cli-model-selector';
import { DialogComponent } from '../../../../../components/dialog/dialog.component';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { RegressionRadarComponent } from '../../../../regression-radar';
import { AgentWorkDetailComponent } from '../agent-work-detail/agent-work-detail.component';
import type { PipelineStepResultHeader } from '../pipeline-step-result/pipeline-step-result.component';
import { ReferencesSectionComponent } from '../../references-section/references-section.component';
import { PlanningSpawnPanelComponent } from '../../planning-spawn-panel/planning-spawn-panel.component';
import { ConceptDossierNoticeComponent } from '../../concept-dossier-notice/concept-dossier-notice.component';
import { TooltipDirective, type StructuredTooltip, type TooltipSeverity } from 'coding-agent-chat/shared';
import { TaskPromptPopoverComponent } from '../task-prompt-popover/task-prompt-popover.component';
import { PipelineRunHistoryComponent } from '../pipeline-run-history/pipeline-run-history.component';
import { PipelineTokenUsageComponent } from '../pipeline-token-usage/pipeline-token-usage.component';
import { PipelineStepDetailsComponent } from '../pipeline-step-details/pipeline-step-details.component';
import { PipelineStepToggleComponent } from '../pipeline-step-toggle/pipeline-step-toggle.component';
import { PostStepControlsComponent } from '../post-step-controls/post-step-controls.component';
import { lifecyclePhaseLabel } from './lifecycle-phase.util';
import {
  isSteeringKind,
  steeringInfoFromEvent,
  type SteeringInfo,
} from '../../../../../components/steering-detail';
import { cliTypeLabel } from '../../../../../services/format.util';
import { projectIdentity } from '../../../../../services/project-identity.util';
import { TaskService } from '../../../../../services/task.service';
import {
  CostBreakdownTriggerDirective,
  formatTokenCostDisplay,
  incompleteTokenCostLabel,
} from '../../../../tokens';
import { NotificationService } from '../../../../../services/notification.service';
import { ModalStackService } from '../../../../../services/modal-stack.service';
import { copyTextToClipboard } from '../../../../../services/clipboard.util';
import { ExecutionLocationBadgeComponent } from '../../../../../components/execution-location-badge/execution-location-badge.component';
import { CopyableTaskKeyComponent } from '../../../../../components/copyable-task-key/copyable-task-key.component';
import {
  buildPipelineGroups,
  groupAriaLabel,
  groupToneLabel,
  type PipelineGroupVm,
} from './pipeline-groups.util';
import {
  buildPipelineStepCostTooltip,
  buildPipelineStepTokenTooltip,
  buildPipelineTotalCostTooltip,
  buildPipelineTotalTokenTooltip,
  formatPipelineAggregateCost,
  formatPipelineCost,
} from './pipeline-cost-tooltip.util';
import {
  formatDuration,
  formatTokens,
  historicalStepStatusIcon,
  laneLabel,
  stepKindIcon,
  stepKindLabel,
  stepStatusIcon,
  stepStatusLabel,
} from './overview-pane-formatters';
import { PipelineHistoryNoticeComponent } from './pipeline-history-notice/pipeline-history-notice.component';
import { OverviewRunsComponent } from './overview-runs/overview-runs.component';
import { distinctStepVerdict } from './pipeline-status-verdict.util';
import type { ProtocolVerdict } from '../../protocol-pane/protocol-verdict';
import { outcomeDecisionBadge, type DecisionBadgeVm } from './outcome-decision-badge.util';
import { ModelMigrationHintComponent } from '../../../../cli';

interface PipelineRowVm {
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

type PipelinePhaseKey = 'pre' | 'core' | 'aspect' | 'tool' | 'analysis' | 'decision' | 'drift';

interface PipelinePhaseVm {
  key: PipelinePhaseKey;
  label: string;
  description: string;
}

interface PipelineTotalVm {
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

interface TokenBreakdownRowVm {
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
interface PipelineRunOptionVm {
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

/** Short unique id for a seeded create-modal attachment (mirrors the dialog's own). */
function makeAttachmentId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
  }
  return Math.random().toString(36).slice(2, 14);
}

/** Title-case label for execution detail that belongs behind a verdict pill. */
function verdictTitle(verdict: string | null): string | null {
  switch ((verdict ?? '').toLowerCase()) {
    case 'concern':
    case 'concerns':      return 'Concerns';
    case 'blocked':
    case 'block':         return 'Blocking concern';
    // Auto-mode Ralph-loop guard verdicts (pre-loop-guard step).
    case 'looping':       return 'Loop forming';
    case 'loop-detected': return 'Loop detected';
    case 'open-items':    return 'Open items';
    case 'escalated':
    case 'escalate':      return 'Escalation reason';
    case 'selected':      return 'Economy selection';
    case 'override':      return 'Card override';
    case 'fallback':      return 'Default fallback';
    default:              return null;
  }
}

/**
 * Reconcile the CORE step's self-reported verdict against its deterministic
 * status so the row can never show a red "Failed" icon next to a green
 * "SUCCESS" badge (bug ASS-2). The status (icon) is authoritative — it is the
 * classified run status, not a prompt-based self-report — so a non-passed CORE
 * step that still claims a success-class outcome ('success'/'noop') has its
 * verdict dropped. The backend now writes a reconciled record, but this also
 * guards legacy on-disk records persisted before the fix, which are only
 * rewritten when the task re-runs. Every consistent pairing passes through.
 */
function reconcileCoreVerdict(
  status: PipelineRowVm['status'],
  verdict: string | null,
): string | null {
  if (status === 'passed') return verdict;
  const claim = (verdict ?? '').toLowerCase();
  if (claim === 'success' || claim === 'noop') return null;
  return verdict;
}

/**
 * Build the structured tooltip for detail behind a step verdict. Aspect
 * concerns, loop-guard findings, and reissue open-item/escalation decisions
 * all use the same compact verdict pill and details-dialog concern section.
 * A pass verdict or a step with no recorded detail stays bare.
 */
function buildConcernTooltip(
  label: string,
  verdict: string | null,
  summary: string | null,
): StructuredTooltip | null {
  const text = summary?.trim();
  if (!text) return null;
  const kind = verdictTitle(verdict);
  if (!kind) return null;
  return { title: `${label} · ${kind}`, body: text };
}

/** Show failures, skips, and the honest coverage scope behind a passed test gate. */
function buildStepStatusTooltip(
  label: string,
  status: PipelineRowVm['status'],
  detail: string | null,
): StructuredTooltip | null {
  const body = detail?.trim();
  if (!body) return null;
  const passedTestCoverage = status === 'passed' && /(?:^|;\s*)test-level=/i.test(body);
  if (status !== 'failed' && status !== 'skipped' && status !== 'notApplicable' && status !== 'not-run' && !passedTestCoverage) return null;
  const title = status === 'failed'
    ? 'Failed'
    : status === 'skipped'
      ? 'Skipped'
      : status === 'notApplicable'
        ? 'Not applicable'
      : status === 'not-run' ? 'Not run' : 'Passed';
  return { title: `${label}: ${title}`, body };
}

/** Map a steering tone to the tooltip accent colour. */
function decisionTooltipSeverity(tone: SteeringInfo['tone']): TooltipSeverity {
  switch (tone) {
    case 'ok':     return 'success';
    case 'warn':   return 'warn';
    case 'danger': return 'error';
    default:       return 'info';
  }
}

/**
 * Build the decision badge tooltip: the orchestrator's reasoning headline, the
 * open items behind the ruling, and the run context, composed into the body so
 * the inline badge stays compact and the detail is available on hover / focus.
 */
function buildDecisionTooltip(info: SteeringInfo): StructuredTooltip {
  const lines: string[] = [];
  if (info.reason) lines.push(info.reason);
  if (info.openItems.length > 0) {
    if (lines.length > 0) lines.push('');
    lines.push('Open items:');
    for (const item of info.openItems) {
      const verdict = item.verdict ? ` [${item.verdict}]` : '';
      const reason = item.reason ? `: ${item.reason}` : '';
      lines.push(`• ${item.aspect}${verdict}${reason}`);
    }
  }
  if (info.context.length > 0) {
    if (lines.length > 0) lines.push('');
    for (const line of info.context) lines.push(`${line.key}: ${line.value}`);
  }
  const body = lines.join('\n').trim();
  return {
    title: `Decision · ${info.verdictLabel}`,
    body: body || info.verdictLabel,
  };
}

/**
 * Per-step "what happens here" copy, keyed by the stable catalogue step id
 * (see backend PipelineCatalogue). Surfaced as the hover tooltip on every
 * pipeline-step name so the operator can learn what each pre / core / aspect /
 * tool / decision / drift step actually does without leaving the Overview.
 */
const PIPELINE_STEP_EXPLANATIONS: Record<string, string> = {
  'pre-loop-guard':
    'Auto-mode loop guard. Before the agent runs, a deterministic check makes sure the same task is not being re-issued in circles: it flags a forming loop while still under budget and trips the circuit-breaker once the iteration or token limit is hit, pausing for the user.',
  'pre-orchestrator-prep':
    'Opt-in prompt-readiness pass. Scores the task prompt for clarity while it is still in Preparation and either admits it to Ready or bounces it back for refinement. Runs off the coding seat, so it never blocks throughput.',
  'pre-model-qualification':
    'Zero-token model qualification. Classifies task type, size, affected surface, and similar project history, then maps that profile onto the selected CLI\'s live model and reasoning ladders. A model or level pinned on the card always wins; the recommendation remains visible for comparison.',
  'pre-reissue-open-items':
    'Re-issue guard. On a re-issued run it detects open items left from the previous attempt (the auto-review follow-up reason, unchecked checklist boxes, aspect concerns) and foregrounds them into the run prompt so the agent finishes them instead of starting over.',
  'core-agent-run':
    'The actual CLI coding run. The agent works the task in the repository until it reports done, blocks, or asks for input. This is the single sequential coding seat; every pre- and post-step wraps around it.',
  'aspect-requirement-fit':
    'Parallel review aspect. Checks whether the work matches the prompt\'s acceptance criteria and whether anything landed that the prompt did not ask for.',
  'aspect-code-quality':
    'Parallel review aspect. Scans the diff and changed-file list for obvious regressions, dead code, missing tests, or type errors.',
  'aspect-documentation-impact':
    'Parallel review aspect. Checks whether the change needs documentation updates (AGENTS.md, ROADMAP, ADRs, cli-skills, docs) and whether they were made.',
  'aspect-tests-and-evidence':
    'Parallel review aspect. Checks whether the agent shipped tests that fail before and pass after the change, and whether screenshot or log evidence is present where the contract requires it.',
  'post-git-commit-attribution':
    'Determines which git commits belong to this task by matching commit author-dates against the run\'s wall-clock windows. The work runs on the lane transition ahead of this bracket, so the row shows as planned here.',
  'post-lint-scss':
    'Runs stylelint over the frontend SCSS tree after the run. Depending on the configured gate mode (off, warn, or fail) a failure can trigger a re-issue back to Ready.',
  'post-regression-radar':
    'Deterministic spec-change analysis. Reads the task\'s attributed commits and classifies each changed spec as intended, at-risk, or drift. Reporting only: it never triggers a re-issue.',
  'post-orchestrator-review':
    'Post-core completeness check. Right after the agent reports done, a deterministic scan reads the run\'s own close-out evidence (open items, notes, the result line, and the log tail) for unfinished-work signals such as open checklist boxes or self-reported build / test failures. A hit re-issues the task with those items foregrounded before any review pass runs, so a task is never accepted while its own evidence says it is unfinished.',
  'post-orchestrator-decision':
    'The orchestrator\'s single final ruling. Aggregates the parallel aspect verdicts and decides re-issue, accept-as-done, or escalate. This is the step that moves the task out of auto-review.',
  'post-drift-adr-code':
    'Opt-in drift check (off by default). An LLM pass that looks for drift between the code and the decisions recorded in the ADRs.',
  'post-drift-software-architecture':
    'Opt-in drift check (off by default). An LLM pass that compares the code against the documented software-architecture intent.',
  'post-drift-docs-marketing':
    'Opt-in drift check (off by default). An LLM pass that checks whether docs and marketing copy still match what the software does.',
  'post-drift-spec-task-job':
    'Opt-in drift check (off by default). An LLM pass that checks whether specs, tasks, and jobs still agree with the implementation.',
  'post-drift-code-pattern':
    'Opt-in drift check (off by default). A rule-based scan for code-pattern drift, optionally enriched by an LLM verdict.',
  'post-abort-review':
    'Abort-triggered review (off by default). Runs only after a non-clean run end such as a watchdog timeout, non-zero exit, or unexpected stop: it reads the abort evidence and recommends rerun, a stronger re-issue, accept, or human review.',
};

/** Per-kind fallback copy for a step id not in the explicit catalogue map. */
const PIPELINE_KIND_EXPLANATIONS: Record<StepKind, string> = {
  module:       'A deterministic pre-processing step that runs before the agent.',
  core:         'The core CLI agent run for this task.',
  aspect:       'A read-only review aspect that runs in parallel after the agent finishes.',
  orchestrator: 'An orchestrator decision step that aggregates verdicts and chooses the next move.',
  tool:         'A deterministic tooling step that runs after the agent finishes.',
  analysis:     'A named Quality Studio analysis that returns canonical findings and provenance in process.',
  drift:        'An opt-in drift-analysis pass that runs after auto-review.',
};

/**
 * Catalogue id of the single FINAL orchestrator ruling. Only this row earns
 * the "Final verdict" chip / divider; the post-core `post-orchestrator-review`
 * early gate shares the `orchestrator` kind but is NOT the final verdict.
 * Mirrors backend `PipelineCatalogue.OrchestratorDecisionStepId`.
 */
const FINAL_VERDICT_STEP_ID = 'post-orchestrator-decision';

const PIPELINE_PHASES: Record<PipelinePhaseKey, PipelinePhaseVm> = {
  pre: {
    key: 'pre',
    label: 'PRE STEPS',
    description: 'Preparation checks before the agent gets the task.',
  },
  core: {
    key: 'core',
    label: 'CORE AGENT WORK',
    description: 'The coding agent work.',
  },
  aspect: {
    key: 'aspect',
    label: 'ASPECT',
    description: 'Parallel review passes over the finished work.',
  },
  tool: {
    key: 'tool',
    label: 'TOOL',
    description: 'Deterministic post-run tooling and evidence steps.',
  },
  analysis: {
    key: 'analysis',
    label: 'ANALYSIS',
    description: 'Named Quality Studio quality analyses over the completed change.',
  },
  decision: {
    key: 'decision',
    label: 'DECISION',
    description: 'The orchestrator ruling that accepts, reissues, or escalates.',
  },
  drift: {
    key: 'drift',
    label: 'DRIFT',
    description: 'Optional drift-analysis passes.',
  },
};

function pipelinePhaseForKind(kind: StepKind): PipelinePhaseVm {
  switch (kind) {
    case 'module':       return PIPELINE_PHASES.pre;
    case 'core':         return PIPELINE_PHASES.core;
    case 'aspect':       return PIPELINE_PHASES.aspect;
    case 'tool':         return PIPELINE_PHASES.tool;
    case 'analysis':     return PIPELINE_PHASES.analysis;
    case 'orchestrator': return PIPELINE_PHASES.decision;
    case 'drift':        return PIPELINE_PHASES.drift;
    default:             return PIPELINE_PHASES.tool;
  }
}

/**
 * Build the always-present step-name explanation tooltip: the step's display
 * label as the title and the "what happens here" copy as the body, keyed by
 * step id with a per-kind fallback so a new catalogue step still explains
 * itself rather than rendering with no tooltip.
 */
function buildStepExplanation(stepId: string, label: string, kind: StepKind): StructuredTooltip {
  const body =
    PIPELINE_STEP_EXPLANATIONS[stepId.toLowerCase()] ??
    PIPELINE_KIND_EXPLANATIONS[kind] ??
    'A pipeline step.';
  return { title: label, body };
}
@Component({
  selector: 'app-overview-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DialogComponent, CliModelSelectorComponent, RegressionRadarComponent, AgentWorkDetailComponent, ReferencesSectionComponent, PlanningSpawnPanelComponent, ConceptDossierNoticeComponent, TooltipDirective, CompletionLoopIndicatorComponent, TaskPromptPopoverComponent, PipelineRunHistoryComponent, PipelineTokenUsageComponent, PipelineStepDetailsComponent, PipelineStepToggleComponent, PostStepControlsComponent, StudioIconComponent, CostBreakdownTriggerDirective, ExecutionLocationBadgeComponent, PipelineHistoryNoticeComponent, OverviewRunsComponent, CopyableTaskKeyComponent, ModelMigrationHintComponent],
  templateUrl: './overview-pane.component.html',
  styleUrl: './overview-pane.component.scss',
})
export class OverviewPaneComponent {
  readonly stepKindLabel = stepKindLabel;
  readonly stepKindIcon = stepKindIcon;
  readonly stepStatusIcon = stepStatusIcon;
  readonly historicalStepStatusIcon = historicalStepStatusIcon;
  readonly stepStatusLabel = stepStatusLabel;
  readonly visibleStepVerdict = distinctStepVerdict;
  readonly laneLabel = laneLabel;
  readonly formatTokens = formatTokens;
  readonly formatDuration = formatDuration;

  readonly job = input.required<TaskInfo>();
  /** Raw task prompt markdown (`promptMarkdown`), surfaced via the Prompt
   *  popover next to the title. Empty/absent hides the trigger. */
  readonly promptMarkdown = input<string | null | undefined>('');
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  readonly isRunning = input(false);
  readonly cliTypeOverride = input<CliType | null | undefined>(undefined);
  readonly modelOverride = input<string | null | undefined>(undefined);
  readonly thinkingLevelOverride = input<string | null | undefined>(undefined);
  readonly runOutcome = input<ProtocolVerdict | null>(null);

  readonly agentConfigCommit = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();
  readonly referencesChanged = output<void>();
  /** Fired after a successful title PUT so the parent can re-fetch the
   *  detail and let the optimistic override drop back to the canonical
   *  `job().title`. */
  readonly titleSaved = output<void>();

  private readonly runTimelinePoll = inject(RunTimelinePollService);
  private readonly agentWorkPoll = inject(AgentWorkSummaryPollService);
  readonly pipelinePoll = inject(TaskPipelinePollService);
  private readonly timelinePoll = inject(TaskTimelinePollService);
  private readonly clients = inject(ClientService);
  private readonly jobService = inject(TaskService);
  private readonly notifs = inject(NotificationService);
  private readonly modalStack = inject(ModalStackService);
  private readonly createForm = inject(CreateTaskFormService);
  private readonly destroyRef = inject(DestroyRef);

  readonly timeline = this.runTimelinePoll.timeline;
  readonly runs = this.runTimelinePoll.runs;

  /** Title inline-edit state — local to this pane (the detail-header's edit
   *  is parent-owned and separate). Optimistic: `optimisticTitle` overrides
   *  the displayed value the moment the user hits Enter / blurs, so there
   *  is no spinner between PUT and the parent's re-fetch landing. */
  readonly editingTitle = signal(false);
  readonly titleDraft = signal('');
  readonly savingTitle = signal(false);
  readonly selectedTokenStepId = signal<string | null>(null);
  readonly selectedPipelineAttempt = signal<number | null>(null);
  private readonly optimisticTitle = signal<string | null>(null);
  private modalStackDisposer: (() => void) | null = null;
  private tokenModalStackDisposer: (() => void) | null = null;

  /** Title the H1 renders. Falls back to the job id when no title is set. */
  readonly displayedTitle = computed<string>(() => {
    const opt = this.optimisticTitle();
    if (opt != null) return opt;
    return this.job().title || this.job().id;
  });

  /** Visual identity (initial + colour) of the project, for the sub-line. */
  readonly identity = computed(() => projectIdentity(this.job().projectName));

  /** In-flight guard for the promote-to-coding fetch (payload + images). */
  readonly promoting = signal(false);

  /**
   * Lanes a planning task counts as "finished successfully" for the
   * promote affordance — it has reached review or completion, not a
   * failure / still-running lane. See
   * docs/concepts/planning-research-task-kinds-2026-05.md.
   */
  private static readonly FINISHED_STATES = new Set<string>([
    TaskState.AutoReview,
    TaskState.HumanReview,
    TaskState.Escalated,
    TaskState.Completed,
  ]);

  /**
   * Pending project-level step switches are useful only while this task can
   * still reach another pipeline step. Human review and every lane after it
   * are read-only evidence: changing project configuration there cannot alter
   * the run being inspected and misleadingly looks like a task-local change.
   */
  private static readonly PIPELINE_CONFIGURABLE_STATES = new Set<string>([
    TaskState.Backlog,
    TaskState.Preparation,
    TaskState.OrchestratorPrep,
    TaskState.Ready,
    TaskState.Progress,
    TaskState.FailedPickup,
    TaskState.CodeNotComplete,
    TaskState.AutoReview,
  ]);

  /** Lanes where a non-running attempt cannot honestly still be "pending". */
  private static readonly PIPELINE_ATTEMPT_SETTLED_STATES = new Set<string>([
    TaskState.HumanReview,
    TaskState.Escalated,
    TaskState.Completed,
    TaskState.Archive,
  ]);

  readonly canConfigurePendingPipelineSteps = computed(() =>
    OverviewPaneComponent.PIPELINE_CONFIGURABLE_STATES.has(this.job().state),
  );

  /**
   * "Promote to coding task" is offered only on a planning task whose latest
   * run finished. Research tasks are read-only reports by design and never
   * show it; coding tasks have nothing to promote to.
   */
  readonly canPromote = computed(() =>
    this.job().mode === 'planning'
    && OverviewPaneComponent.FINISHED_STATES.has(this.job().state),
  );

  /**
   * Fetch the pre-fill draft for this planning task, pull each copyable image
   * down as a blob (so the create modal can re-upload it byte-for-byte), then
   * open the create-task modal seeded with that draft. The modal stays the
   * single source of truth for the create UX.
   */
  promote(): void {
    if (this.promoting() || !this.canPromote()) return;
    const job = this.job();
    this.promoting.set(true);
    this.jobService.getPromoteToCoding(job.id, job.watchPath).subscribe({
      next: (payload) => {
        void this.fetchPromoteAttachments(payload).then((attachments) => {
          this.createForm.openPromotePlanning(payload, attachments);
          this.promoting.set(false);
        });
      },
      error: () => {
        this.promoting.set(false);
        this.notifs.warning(
          'Could not prepare a coding task from this planning report. Try again in a moment.',
          'Promote failed',
        );
      },
    });
  }

  /**
   * Download each promote attachment as a File wrapped in a PendingAttachment.
   * A single failed image is skipped (the rest still come along) rather than
   * failing the whole promotion.
   */
  private async fetchPromoteAttachments(payload: PromoteToCodingResponse): Promise<PendingAttachment[]> {
    const pending: PendingAttachment[] = [];
    for (const ref of payload.attachments) {
      try {
        const res = await fetch(ref.url);
        if (!res.ok) continue;
        const blob = await res.blob();
        const file = new File([blob], ref.fileName, { type: blob.type || 'image/png' });
        pending.push({
          id: makeAttachmentId(),
          file,
          alt: ref.fileName,
          previewUrl: URL.createObjectURL(blob),
        });
      } catch {
        // Skip this image; keep the rest of the promotion intact.
      }
    }
    return pending;
  }

  /** Effective CLI + model the Agent block renders. The override wins when
   *  the parent provides one (optimistic state from the picker commit) so
   *  the badge updates without a network round-trip; otherwise we fall
   *  back to the canonical `job()` value. */
  readonly effectiveCliType = computed<CliType | null>(() => {
    const override = this.cliTypeOverride();
    return override !== undefined ? (override as CliType | null) : this.job().cliType;
  });
  readonly effectiveModel = computed<string | null>(() => {
    const override = this.modelOverride();
    return override !== undefined ? override : this.job().model;
  });
  readonly effectiveThinkingLevel = computed<string | null>(() => {
    const override = this.thinkingLevelOverride();
    return override !== undefined ? override : (this.job().thinkingLevel ?? null);
  });
  readonly agentConfigReadOnly = computed(() => this.job().state === TaskState.Completed || this.job().state === TaskState.Archive);

  /**
   * Accepting an offered update is an operator decision, so it goes through the
   * same commit the picker uses: the card becomes an explicit pin and keeps its
   * CLI and reasoning level.
   */
  applyModelMigration(model: string): void {
    const cliType = this.effectiveCliType();
    if (cliType === null) return;
    this.agentConfigCommit.emit({ cliType, model, thinkingLevel: this.effectiveThinkingLevel() });
  }
  /** Clear the optimistic override once the real `job().title` catches up
   *  to the saved value (parent re-fetched the detail after PUT). */
  private clearOptimisticOnSync = effect(() => {
    const opt = this.optimisticTitle();
    if (opt == null) return;
    const current = this.job().title || this.job().id;
    if (current === opt) {
      this.optimisticTitle.set(null);
    }
  });

  @ViewChild('titleInput') private titleInputEl?: ElementRef<HTMLInputElement>;

  private focusOnEdit = effect(() => {
    if (this.editingTitle()) {
      queueMicrotask(() => this.titleInputEl?.nativeElement.select());
    }
  });

  startTitleEdit(): void {
    if (this.editingTitle()) return;
    this.titleDraft.set(this.displayedTitle());
    this.editingTitle.set(true);
    // Push a modal-stack entry so Escape closes the edit, not the detail
    // panel. The parent's modal-stack entry only checks its own
    // editingTitle / editingPrompt signals (set by the detail-header), so
    // without this Escape would bubble past our local cancel and close
    // the whole detail view.
    this.modalStackDisposer = this.modalStack.push('overview-title-edit', () => {
      this.cancelTitleEdit();
      return true;
    });
    this.destroyRef.onDestroy(() => this.disposeModalStack());
  }

  cancelTitleEdit(): void {
    this.editingTitle.set(false);
    this.savingTitle.set(false);
    this.disposeModalStack();
  }

  saveTitle(): void {
    if (!this.editingTitle()) return;
    const trimmed = this.titleDraft().trim();
    if (!trimmed) {
      this.cancelTitleEdit();
      return;
    }
    const current = this.displayedTitle();
    if (trimmed === current) {
      this.cancelTitleEdit();
      return;
    }
    // Optimistic: paint the new title immediately, drop edit mode, fire
    // the PUT without a spinner. Revert on error.
    const job = this.job();
    this.optimisticTitle.set(trimmed);
    this.editingTitle.set(false);
    this.savingTitle.set(false);
    this.disposeModalStack();
    this.jobService.setJobTitle(job.id, trimmed, job.watchPath).subscribe({
      next: () => {
        this.titleSaved.emit();
      },
      error: () => {
        this.optimisticTitle.set(null);
        this.notifs.warning(
          'The new title could not be saved. The previous title was restored.',
          'Title save failed',
        );
      },
    });
  }

  copyTitle(): void {
    const text = this.displayedTitle();
    if (!text) return;
    copyTextToClipboard(text).then(ok => {
      if (ok) this.notifs.success('Task title copied to clipboard', 'Title copied');
    });
  }

  onTitleDraftInput(value: string): void {
    this.titleDraft.set(value);
  }

  private disposeModalStack(): void {
    if (this.modalStackDisposer) {
      this.modalStackDisposer();
      this.modalStackDisposer = null;
    }
  }

  /**
   * Derived from `logs/session-events.jsonl` + `logs/tool-calls.jsonl`.
   * Drives the Agent Work block that replaced the raw SESSION row.
   */
  readonly agentWork = this.agentWorkPoll.summary;

  readonly hasAgentWork = computed(() => {
    const s = this.agentWork();
    return s != null && (s.calls > 0 || s.toolCalls > 0);
  });

  /** Top N tool counts to render as compact chips. */
  readonly topToolCounts = computed(() => {
    const s = this.agentWork();
    if (s == null) return [];
    return s.toolCounts.slice(0, 6);
  });

  /** Comma-separated tool tooltip (full list) for the "Tools" row. */
  readonly toolCountsTooltip = computed(() => {
    const s = this.agentWork();
    if (s == null || s.toolCounts.length === 0) return '';
    return s.toolCounts.map(tc => `${tc.tool}: ${tc.count}`).join('\n');
  });

  /** Short rendering of the session id for the optional debug tooltip. */
  readonly sessionDebugTooltip = computed(() => {
    const id = this.job().sessionName;
    if (!id) return '';
    return `Session id (debug): ${id}`;
  });

  readonly owner = computed(() => {
    const ownerId = this.job().ownerClientId;
    return this.clients.resolve(ownerId);
  });

  readonly lastRunRecord = computed<RunRecord | null>(() => {
    const r = this.runs();
    return r.length > 0 ? r[r.length - 1] : null;
  });

  readonly totalDuration = computed(() => {
    let total = 0;
    for (const r of this.runs()) {
      if (r.durationSeconds != null) total += r.durationSeconds;
    }
    // Fall back to the persisted CORE agent-run step duration when no run row
    // carried one. A killed run whose exit marker never paired with a
    // session-event leaves runs() without a duration, but RecordCoreRunFinish
    // writes the CORE step duration unconditionally on every finish - so the
    // elapsed time is still shown even for aborted runs where tokens are
    // missing (ASS-665: "duration always show").
    if (total === 0) {
      const coreMs = this.agentExecutionRow()?.durationMs ?? 0;
      if (coreMs > 0) total = coreMs / 1000;
    }
    return total;
  });

  /** Recorded run count, from the run-timeline. 0 before the first run. */
  readonly runCount = computed<number>(() => this.timeline()?.runCount ?? 0);

  /**
   * Per-step pipeline rows for the Overview pipeline block. Joins the
   * static catalogue (ordered pre+core+post, gives label/kind) with the
   * recorded execution (status/model/verdict/tokens), the derived cost,
   * and the per-project config (enabled flag + model override). Steps the
   * project disabled still render — as a struck-through "disabled" row —
   * so the operator can see what was switched off, not just what ran.
   */
  readonly pipelineRows = computed<PipelineRowVm[]>(() => {
    const res = this.pipelinePoll.pipeline();
    if (res == null) return [];
    const steps: PipelineStep[] =
      res.pipeline.allSteps ??
      [...res.pipeline.pre, ...res.pipeline.core, ...res.pipeline.post];

    const selectedExecution = this.selectedPipelineExecution();
    const isCurrentRun = this.selectedPipelineIsCurrent();
    const attemptSettledOutsideFullPipeline =
      isCurrentRun
      && selectedExecution != null
      && !this.isRunning()
      && OverviewPaneComponent.PIPELINE_ATTEMPT_SETTLED_STATES.has(this.job().state)
      && (
        selectedExecution.completedAt != null
        || (selectedExecution.attempt ?? 1) > 1
        || (selectedExecution.previousAttempts?.length ?? 0) > 0
        || this.job().orchestratorVerdict === 'escalate'
      );
    const exec = new Map((selectedExecution?.steps ?? []).map(s => [s.stepId.toLowerCase(), s]));
    const chainEndedByEarlyEscalate = (selectedExecution?.steps ?? []).some(step =>
      step.stepId !== FINAL_VERDICT_STEP_ID
      && step.kind === 'orchestrator'
      && step.verdict?.toLowerCase() === 'escalate');
    const cost = new Map((isCurrentRun ? (res.cost?.steps ?? []) : []).map(c => [c.stepId.toLowerCase(), c]));
    const cardPlan = new Set((res.onDemand?.plannedStepIds ?? []).map(id => id.toLowerCase()));
    const latestOnDemand = new Map<string, NonNullable<TaskPipelineResponse['onDemand']>['attempts'][number]>(isCurrentRun
      ? (res.onDemand?.attempts ?? []).map(attempt => [attempt.stepId.toLowerCase(), attempt]) : []);

    const rows = steps.map(step => {
      const key = step.id.toLowerCase();
      const e = exec.get(key);
      const onDemand = latestOnDemand.get(key);
      const c = cost.get(key);
      const cfg = res.config?.[step.id];
      const enabled = cardPlan.has(key) || (cfg?.enabled ?? true);
      let status: PipelineRowVm['status'];
      if (!enabled) status = 'disabled';
      else if (onDemand) status = onDemand.status.toLowerCase() === 'failed' ? 'failed'
        : onDemand.status.toLowerCase() === 'skipped' ? 'skipped' : 'passed';
      else if (e?.status === 'pending' && attemptSettledOutsideFullPipeline && !step.deferred) status = 'not-run';
      else if (e) status = e.status;
      else if (step.stub) status = 'planned';
      else if (attemptSettledOutsideFullPipeline && !step.deferred) status = 'not-run';
      else status = 'pending';
      const label = step.displayName || step.id;
      // Model precedence: a recorded execution model (what actually ran) wins;
      // before any run, fall back to the backend-resolved effective model so the
      // step shows which model it WILL use, then to the raw override / catalogue.
      const recordedModel = e?.model ?? null;
      const resolvedModel = cfg?.resolvedModel ?? null;
      const model = recordedModel ?? resolvedModel ?? cfg?.model ?? step.model ?? null;
      const thinkingLevel = e?.thinkingLevel ?? null;
      const cliType = this.asCliType(cfg?.cliType ?? step.cliType ?? this.effectiveCliType());
      const modelIsResolved = recordedModel == null && model != null;
      const modelTooltip = this.buildModelTooltip(label, model, modelIsResolved, cfg?.modelSource ?? null);
      const modelEditable = false;
      const modelOverride = cfg?.model ?? '';
      const thinkingLevelOverride = cfg?.thinkingLevel ?? null;
      let verdict = e?.verdict ?? null;
      if (step.kind === 'core') verdict = reconcileCoreVerdict(status, verdict);
      const statusDetail = e?.verdictSummary
        ?? e?.reason
        ?? (status === 'not-run'
          ? 'This attempt used a lightweight pipeline or escalated before this step ran.'
          : null);
      const legacyNoVerifyCommands =
        status === 'skipped' && e?.reason?.trim().toLowerCase() === 'no verify commands derivable';
      const remoteNotApplicable =
        status === 'skipped' && e?.reason?.startsWith('Executed remotely;') === true;
      if (legacyNoVerifyCommands || remoteNotApplicable) status = 'notApplicable';
      const attentionRequired = step.id === 'post-build-test-gate' && status === 'skipped';
      const tokenTooltip = buildPipelineStepTokenTooltip(label, c ?? null);
      const costTooltip = buildPipelineStepCostTooltip(label, c ?? null);
      const phase = pipelinePhaseForKind(step.kind);
      const inputTokens = c?.inputTokens ?? e?.inputTokens ?? 0;
      const outputTokens = c?.outputTokens ?? e?.outputTokens ?? 0;
      const cacheReadTokens = c?.cacheReadTokens ?? e?.cacheReadTokens ?? 0;
      const cacheCreationTokens = c?.cacheCreationTokens ?? e?.cacheCreationTokens ?? 0;
      const totalTokens = c?.totalTokens ?? (inputTokens + outputTokens + cacheReadTokens + cacheCreationTokens);
      return {
        id: step.id,
        label,
        kind: step.kind,
        phaseKey: phase.key,
        phaseLabel: phase.label,
        phaseDescription: phase.description,
        startsPhase: false,
        runMode: step.runMode,
        isFinalVerdict: step.id === FINAL_VERDICT_STEP_ID,
        historical: !isCurrentRun,
        enabled,
        canDisable: cfg?.canDisable ?? false,
        hasExecution: e != null || onDemand != null,
        config: cfg ?? null,
        status,
        statusTooltip: buildStepStatusTooltip(label, status, statusDetail),
        skipHint: status === 'not-run'
          ? 'not run: lightweight pipeline or escalation'
          : status === 'notApplicable' && legacyNoVerifyCommands
            ? 'no build/test defined'
          : remoteNotApplicable
            ? 'executed remotely · not applicable'
          : status === 'skipped' && chainEndedByEarlyEscalate
            ? 'skipped: chain ended by early escalate'
            : null,
        remoteNotApplicable,
        attentionRequired,
        remoteReviewDetail: e?.reason?.startsWith('Remote Review Plane verdict')
          ? e.reason
          : null,
        model,
        thinkingLevel,
        cliType,
        modelIsResolved,
        modelTooltip,
        modelEditable,
        modelOverride,
        thinkingLevelOverride,
        verdict: onDemand ? `attempt ${onDemand.attempt}` : verdict,
        concernTooltip: buildConcernTooltip(label, verdict, statusDetail),
        explanation: buildStepExplanation(step.id, label, step.kind),
        durationMs: onDemand?.durationMs ?? e?.durationMs ?? 0,
        startedAt: onDemand?.startedAt ?? e?.startedAt ?? null,
        completedAt: onDemand?.finishedAt ?? e?.completedAt ?? null,
        tokenUsageSource: c?.tokenUsageSource ?? e?.tokenUsageSource ?? null,
        inputTokens,
        outputTokens,
        cacheReadTokens,
        cacheCreationTokens,
        totalTokens,
        inputCostUsd: c?.inputCostUsd ?? 0,
        outputCostUsd: c?.outputCostUsd ?? 0,
        cacheReadCostUsd: c?.cacheReadCostUsd ?? 0,
        cacheCreationCostUsd: c?.cacheCreationCostUsd ?? 0,
        costUsd: c?.costUsd ?? 0,
        costKnown: c ? c.modelKnown : isCurrentRun,
        unpricedRuns: c && c.totalTokens > 0 && !c.modelKnown ? 1 : 0,
        pricingGaps: c?.pricingGaps ?? [],
        tokenTooltip,
        costTooltip,
      };
    });
    return rows.map((row, index) => ({
      ...row,
      startsPhase: index === 0 || row.phaseKey !== rows[index - 1].phaseKey,
    }));
  });

  readonly hasPipeline = computed(() => this.pipelineRows().length > 0);
  readonly hideDisabledPipelineSteps = signal(false);
  readonly disabledPipelineStepCount = computed(() =>
    this.pipelineRows().filter(row => row.status === 'disabled').length,
  );
  readonly visiblePipelineRows = computed<PipelineRowVm[]>(() => {
    const rows = this.hideDisabledPipelineSteps()
      ? this.pipelineRows().filter(row => row.status !== 'disabled')
      : this.pipelineRows();
    return rows.map((row, index) => ({
      ...row,
      startsPhase: index === 0 || row.phaseKey !== rows[index - 1].phaseKey,
    }));
  });

  toggleDisabledPipelineSteps(): void {
    this.hideDisabledPipelineSteps.update(value => !value);
  }

  refreshPipeline(): void {
    this.pipelinePoll.refresh();
  }

  /**
   * The complete configured pipeline folded into collapsible sections — one per
   * contiguous run of the same phase (PRE STEPS, CORE AGENT WORK, ASPECT, TOOL,
   * DECISION, DRIFT, and the repeated TOOL/DECISION runs in the post-bracket).
   * Each carries the aggregate tone + honest counters ({@link PipelineGroupVm})
   * the header shows whether expanded or collapsed. Derivation lives in the
   * dependency-free `pipeline-groups.util` so the grouping/tone/collapse rules
   * are unit-tested in isolation from this 1800-line host.
   */
  readonly pipelineGroups = computed<PipelineGroupVm<PipelineRowVm>[]>(() =>
    buildPipelineGroups(this.visiblePipelineRows()),
  );

  /**
   * Explicit per-section collapse choices, keyed by group key
   * (`${phaseKey}#${occurrence}`). Absent -> the section follows its derived
   * {@link PipelineGroupVm.defaultCollapsed} (attention sections open, quiet
   * finished / not-yet-reached sections collapse), so the strip reacts to the
   * run as it progresses until the operator overrides a section by hand.
   */
  private readonly groupCollapseOverrides = signal<ReadonlyMap<string, boolean>>(new Map());

  /** Effective collapse state: an explicit operator choice wins over the default. */
  isGroupCollapsed(group: Pick<PipelineGroupVm, 'key' | 'defaultCollapsed'>): boolean {
    const override = this.groupCollapseOverrides().get(group.key);
    return override ?? group.defaultCollapsed;
  }

  /** Flip a section between collapsed and expanded, recording the operator's choice. */
  toggleGroup(group: Pick<PipelineGroupVm, 'key' | 'defaultCollapsed'>): void {
    const collapsed = this.isGroupCollapsed(group);
    this.groupCollapseOverrides.update(prev => {
      const next = new Map(prev);
      next.set(group.key, !collapsed);
      return next;
    });
  }

  /** Force every current section open (backs an expand-all affordance / tests). */
  expandAllPipelineGroups(): void {
    const groups = this.pipelineGroups();
    this.groupCollapseOverrides.update(prev => {
      const next = new Map(prev);
      for (const group of groups) next.set(group.key, false);
      return next;
    });
  }

  /**
   * Section-tone label + header accessible name are pure derivations kept in
   * `pipeline-groups.util` (unit-tested there); bound here so the template can
   * call them directly.
   */
  readonly groupToneLabel = groupToneLabel;
  readonly groupAriaLabel = groupAriaLabel;

  /**
   * Latest raw step-call prompt per pipeline step, keyed by lowercased step id.
   * Fed from `GET /step-prompts` (the `.metadata/prompts.jsonl` read-model) so
   * the Overview "Prompt" affordance on a step can show the exact prompt that
   * step dispatched to the CLI. Empty until the first fetch resolves.
   */
  private readonly stepPrompts = signal<ReadonlyMap<string, string>>(new Map());

  /**
   * Most recent recorded prompt markdown for a step, or `''` when none was
   * captured (deterministic steps, the main run, or before the step has
   * dispatched). The popover hides its own trigger on empty text, so a row
   * without a recorded prompt shows no affordance.
   */
  stepPromptMarkdown(stepId: string): string {
    return this.stepPrompts().get(stepId.toLowerCase()) ?? '';
  }

  /**
   * Pull the raw step prompts for this task. Re-runs when the task changes or
   * a new run completes ({@link runCount}) so freshly dispatched step prompts
   * surface without a manual refresh. Best-effort: an error leaves the prior
   * map in place and the triggers simply stay hidden.
   */
  private readonly loadStepPromptsEffect = effect(() => {
    const job = this.job();
    this.runCount();
    if (!job.id) return;
    this.jobService.getStepPrompts(job.id, job.watchPath).subscribe({
      next: (res) => {
        const map = new Map<string, string>();
        for (const p of res.prompts ?? []) {
          if (!p?.stepId) continue;
          // Last write wins so a re-run step shows its most recent prompt.
          map.set(p.stepId.toLowerCase(), p.prompt ?? '');
        }
        this.stepPrompts.set(map);
      },
      error: () => { /* keep prior map; trigger stays hidden */ },
    });
  });

  readonly selectedTokenRow = computed<PipelineRowVm | null>(() => {
    const id = this.selectedTokenStepId();
    if (!id) return null;
    return this.pipelineRows().find(r => r.id === id && r.totalTokens > 0) ?? null;
  });

  /**
   * The single core "Agent execution" row, used to surface the run count and
   * its details popover. The catalogue carries exactly one `core` step
   * (`core-agent-run`); null before the pipeline catalogue loads.
   */
  private readonly agentExecutionRow = computed<PipelineRowVm | null>(
    () => this.pipelineRows().find(r => r.kind === 'core') ?? null,
  );

  /**
   * Execution count for the core Agent-execution row. Read from the same
   * `RunTimeline.runCount` that drives the Overview "Runs" value, with the
   * Agent Work call count as a fallback, so the row can never drift from the
   * numbers shown elsewhere on the tab. 0 when no run has happened yet, which
   * keeps the row in its existing dash state.
   */
  readonly agentRunCount = computed<number>(() => {
    const tl = this.timeline();
    if (tl && tl.runCount > 0) return tl.runCount;
    return this.agentWork()?.calls ?? 0;
  });

  /** "1 run" / "N runs" label for the count badge. */
  readonly agentRunCountLabel = computed<string>(() => {
    const n = this.agentRunCount();
    return n === 1 ? '1 run' : `${n} runs`;
  });

  /**
   * Runs that had to reconstruct context after a failed session resume —
   * counted from the run-timeline intents, falling back to the Agent Work
   * `recovered` flag when the timeline is unavailable.
   */
  private readonly recoveredRunCount = computed<number>(() => {
    const recovery = this.runs().filter(r => r.intent === 'recovery').length;
    if (recovery > 0) return recovery;
    return this.agentWork()?.recovered ? 1 : 0;
  });

  /** First run start, preferring the run-timeline over the agent-work rollup. */
  private readonly agentRunFirstAt = computed<string | null>(() => {
    const tl = this.timeline();
    if (tl?.firstStartedAt) return tl.firstStartedAt;
    const runs = this.runs();
    if (runs.length > 0 && runs[0].startedAt) return runs[0].startedAt;
    return this.agentWork()?.startedAt ?? null;
  });

  /** Latest run activity, preferring the run-timeline over the agent-work rollup. */
  private readonly agentRunLastAt = computed<string | null>(() => {
    const tl = this.timeline();
    if (tl?.lastActivityAt) return tl.lastActivityAt;
    const runs = this.runs();
    if (runs.length > 0) {
      const latest = [...runs].reverse().find(r => r.endedAt || r.startedAt);
      if (latest) return latest.endedAt ?? latest.startedAt;
    }
    return this.agentWork()?.lastTouchAt ?? null;
  });

  /**
   * Structured popover for the Agent-execution run-count badge: run count,
   * recovered count, CLI / model / session summary, first-run and
   * last-activity stamps, plus a pointer to the Timeline tab for the full
   * story. Null when no run has happened so the badge — which is only
   * rendered for count > 0 — never carries an empty tooltip.
   */
  readonly agentRunTooltip = computed<StructuredTooltip | null>(() => {
    const count = this.agentRunCount();
    if (count <= 0) return null;
    const lines: string[] = [`Runs: ${count}`];
    const recovered = this.recoveredRunCount();
    if (recovered > 0) lines.push(`Recovered: ${recovered}`);
    const cli = this.effectiveCliType();
    if (cli) lines.push(`CLI: ${this.cliTypeLabel(cli)}`);
    const model = this.agentExecutionRow()?.model ?? this.effectiveModel();
    if (model) lines.push(`Model: ${model}`);
    const session = this.job().sessionName ?? this.agentWork()?.currentSessionId ?? null;
    if (session) lines.push(`Session: ${session}`);
    const first = this.agentRunFirstAt();
    if (first) lines.push(`First run: ${this.formatAbsoluteTime(first)}`);
    const last = this.agentRunLastAt();
    if (last) lines.push(`Last activity: ${this.formatAbsoluteTime(last)}`);
    lines.push('See the Timeline tab for the full run history.');
    return {
      title: count === 1 ? 'Agent execution · 1 run' : `Agent execution · ${count} runs`,
      body: lines.join('\n'),
    };
  });

  /**
   * True once the completion loop has produced at least one verdict. Read
   * from the shared timeline poll (same instance the consolidated
   * completion-loop strip binds to) so the Pipeline section can render even
   * when only loop activity — and no pipeline execution — exists yet.
   */
  readonly hasCompletionLoop = computed(() => this.timelinePoll.completionLoop().hasActivity);

  /** Render the Pipeline section when there are steps or completion-loop activity. */
  readonly hasPipelineSection = computed(() => this.hasPipeline() || this.hasCompletionLoop());

  /**
   * The latest orchestrator steering step, projected from the shared
   * task-timeline ledger (Epic ASS-776). The orchestrator review / decision
   * steps render this as a collapsible structured block (verdict + reason +
   * steer prompt + context) so the Steps surface shows the same steering
   * trace as the Timeline, not just the bare verdict token. Null until the
   * completion loop has emitted at least one steering event.
   */
  private readonly latestSteeringInfo = computed<SteeringInfo | null>(() => {
    const events = this.timelinePoll.events();
    for (let i = events.length - 1; i >= 0; i--) {
      if (isSteeringKind(events[i].kind)) {
        return steeringInfoFromEvent(events[i]);
      }
    }
    return null;
  });

  private readonly authoritativeDecisionBadge = computed(() => outcomeDecisionBadge(this.runOutcome()));

  decisionBadgeForRow(row: PipelineRowVm): DecisionBadgeVm | null {
    if (!row.isFinalVerdict) return null;
    const authoritative = this.authoritativeDecisionBadge();
    if (row.remoteReviewDetail && row.verdict) {
      const reviewPassed = row.verdict.toLowerCase() === 'pass';
      const reviewInfrastructure = row.verdict.toLowerCase() === 'review-infra';
      const tone: DecisionBadgeVm['tone'] = reviewPassed
        ? 'ok'
        : reviewInfrastructure ? 'warn' : 'danger';
      const severity: TooltipSeverity = reviewPassed
        ? 'success'
        : reviewInfrastructure ? 'warn' : 'error';
      const label = reviewPassed
        ? 'Review Pass'
        : reviewInfrastructure ? 'Review infrastructure' : 'Review product failure';
      const resultLine = authoritative ? `\n\nTask result: ${authoritative.label}.` : '';
      return {
        verdict: row.verdict,
        label,
        tone,
        severity,
        tooltip: {
          title: `Remote Review Plane · ${label.replace('Review ', '')}`,
          body: `${row.remoteReviewDetail}${resultLine}`,
        },
      };
    }
    if (authoritative) return authoritative;
    const info = this.latestSteeringInfo();
    if (info) {
      return {
        verdict: info.verdict,
        label: info.verdictLabel,
        tone: info.tone,
        severity: decisionTooltipSeverity(info.tone),
        tooltip: buildDecisionTooltip(info),
      };
    }
    if (!row.verdict) return null;
    return {
      verdict: row.verdict,
      label: row.verdict,
      tone: 'neutral',
      severity: row.verdict === 'block' || row.verdict === 'loop-detected' ? 'error' : 'warn',
      tooltip: row.concernTooltip ?? {
        title: 'Final verdict',
        body: 'Aggregated from the review evidence.',
      },
    };
  }

  /** Build result metadata only for a file the backend verified on disk. */
  resultForRow(row: PipelineRowVm): { fileName: string; header: PipelineStepResultHeader } | null {
    if (!this.selectedPipelineIsCurrent()) return null;
    const resultFiles = this.pipelinePoll.pipeline()?.resultFiles ?? {};
    const fileName = Object.entries(resultFiles).find(
      ([stepId]) => stepId.toLowerCase() === row.id.toLowerCase(),
    )?.[1] ?? null;
    if (!fileName) return null;
    if (row.status !== 'passed' && row.status !== 'failed' && row.status !== 'skipped' && row.status !== 'notApplicable') return null;

    return {
      fileName,
      header: {
        label: row.label,
        statusIcon: this.stepStatusIcon(row.status),
        statusLabel: this.stepStatusLabel(row.status),
        status: row.status,
        verdict: row.verdict,
        model: row.model,
        durationLabel: row.durationMs > 0 ? this.formatStepDuration(row.durationMs) : null,
        tokensLabel: row.totalTokens > 0 ? this.formatTokens(row.totalTokens) : null,
        costLabel: row.totalTokens > 0
          ? this.costDisplay(row.costUsd, row.totalTokens, row.unpricedRuns)
          : null,
      },
    };
  }

  openStepTokenModal(row: PipelineRowVm): void {
    if (row.totalTokens <= 0) return;
    this.selectedTokenStepId.set(row.id);
  }

  closeStepTokenModal(): void {
    this.selectedTokenStepId.set(null);
  }

  tokenBreakdownRows(row: PipelineRowVm): TokenBreakdownRowVm[] {
    return [
      { label: 'Input', tokens: row.inputTokens, costUsd: row.inputCostUsd },
      { label: 'Output', tokens: row.outputTokens, costUsd: row.outputCostUsd },
      { label: 'Cache read', tokens: row.cacheReadTokens, costUsd: row.cacheReadCostUsd },
      { label: 'Cache write', tokens: row.cacheCreationTokens, costUsd: row.cacheCreationCostUsd },
    ];
  }

  tokenComponentTotal(row: PipelineRowVm): number {
    return row.inputTokens + row.outputTokens + row.cacheReadTokens + row.cacheCreationTokens;
  }

  tokenComponentMatchesTotal(row: PipelineRowVm): boolean {
    return this.tokenComponentTotal(row) === row.totalTokens;
  }

  tokenStepCallsLabel(row: PipelineRowVm): string {
    if (row.kind === 'core' && this.selectedPipelineIsCurrent()) {
      const n = this.agentRunCount();
      if (n > 0) return n === 1 ? '1 agent run' : `${n} agent runs`;
    }
    if (row.status === 'passed' || row.status === 'failed' || row.status === 'skipped' || row.status === 'notApplicable') {
      return '1 step execution';
    }
    return 'Not reported';
  }

  tokenStepSourceLabel(row: PipelineRowVm): string {
    const source = row.tokenUsageSource?.trim();
    if (source) return source;
    if (row.kind === 'core') return 'CORE agent run';
    return 'Pipeline step usage';
  }

  costDisplay(costUsd: number, totalTokens: number, unpricedRuns: number): string {
    return formatTokenCostDisplay({ costUsd, totalTokens, unpricedRuns });
  }

  incompleteCostLabel(unpricedRuns: number): string {
    return incompleteTokenCostLabel(unpricedRuns);
  }

  isPartialCost(costUsd: number, unpricedRuns: number): boolean {
    return costUsd > 0 && unpricedRuns > 0;
  }

  tokenStepTimeLabel(row: PipelineRowVm): string {
    const parts: string[] = [];
    if (row.startedAt) parts.push(`Started ${this.formatAbsoluteTime(row.startedAt)}`);
    if (row.completedAt) parts.push(`Ended ${this.formatAbsoluteTime(row.completedAt)}`);
    const duration = this.liveStepDurationMs(row);
    if (duration > 0) parts.push(`Duration ${this.formatStepDuration(duration)}`);
    return parts.length > 0 ? parts.join(' · ') : 'No step time recorded';
  }

  /** Task-total tokens + cost across all recorded steps. */
  readonly pipelineTotal = computed<PipelineTotalVm | null>(() => {
    if (!this.selectedPipelineIsCurrent()) return null;
    const c = this.pipelinePoll.pipeline()?.cost ?? null;
    if (c == null) return null;
    return {
      totalInputTokens: c.totalInputTokens,
      totalOutputTokens: c.totalOutputTokens,
      totalCacheReadTokens: c.totalCacheReadTokens,
      totalCacheCreationTokens: c.totalCacheCreationTokens,
      totalTokens: c.totalTokens,
      totalInputCostUsd: c.totalInputCostUsd,
      totalOutputCostUsd: c.totalOutputCostUsd,
      totalCacheReadCostUsd: c.totalCacheReadCostUsd,
      totalCacheCreationCostUsd: c.totalCacheCreationCostUsd,
      totalCostUsd: c.totalCostUsd,
      anyModelUnknown: c.anyModelUnknown,
      unpricedRuns: c.unpricedRuns ?? (c.anyModelUnknown ? 1 : 0),
      pricingGaps: c.pricingGaps ?? [],
      tokenTooltip: buildPipelineTotalTokenTooltip(c),
      costTooltip: buildPipelineTotalCostTooltip(c),
    };
  });

  /** True once at least one step has a recorded execution. */
  readonly hasPipelineExecution = computed(() => this.pipelinePoll.hasExecution());

  /**
   * Tooltip for a step's model chip. Before a run, names the resolved effective
   * model and where in the hierarchy it came from (step / project / global /
   * catalogue default); after a run, states the model the execution used.
   */
  private buildModelTooltip(
    label: string,
    model: string | null,
    isResolved: boolean,
    source: string | null,
  ): StructuredTooltip | null {
    if (!model) return null;
    if (!isResolved) {
      return { title: `${label} model`, body: `Model used for this step: ${model}` };
    }
    const sourceLabel = this.modelSourceLabel(source);
    const body = sourceLabel
      ? `Will run on ${model}\nSource: ${sourceLabel}`
      : `Will run on ${model} (configured before the run)`;
    return { title: `${label} model`, body };
  }

  /** Human-readable label for a resolved model's source token. */
  private modelSourceLabel(source: string | null): string | null {
    switch ((source ?? '').toLowerCase()) {
      case 'step':      return 'per-step override';
      case 'project':   return 'project model';
      case 'global':    return 'global default';
      case 'catalogue': return 'step default';
      case 'runtime':   return 'built-in default';
      default:          return null;
    }
  }

  /** Step ids with a per-step agent write in flight (disable the selector). */
  private readonly savingStepModel = signal<ReadonlySet<string>>(new Set());

  stepModelBusy(stepId: string): boolean {
    return this.savingStepModel().has(stepId);
  }

  /**
   * Persist a per-step agent override for an aspect review and re-resolve the
   * pipeline so the row's effective-model chip + source update in place. The
   * override is project-scoped (mirrors the project-settings page), so this is
   * the in-context way to change the CLI/model a step WILL run on before the run.
   *
   * The backend replaces the whole step entry, so the unchanged facets are
   * resent: aspect steps carry no mode/condition and are enabled by default,
   * so `enabled` is preserved only when explicitly disabled and the model's
   * default thinking level rides along (matching the project-settings write).
   */
  onStepAgentCommit(stepId: string, selection: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    if (this.isRunning() || this.stepModelBusy(stepId)) return;
    const value = (selection.model ?? '').trim();
    const cfg = this.pipelinePoll.pipeline()?.config?.[stepId] ?? null;

    this.savingStepModel.update(set => new Set(set).add(stepId));
    this.jobService.setProjectPipelineStep(this.job().projectName, {
      stepId,
      // Aspect steps default to enabled; only re-send `enabled` when the
      // project explicitly disabled this one, otherwise null clears the facet
      // and lets it fall back to the built-in default.
      enabled: cfg?.enabled === false ? false : null,
      cliType: selection.cliType,
      model: value || null,
      thinkingLevel: selection.thinkingLevel,
      mode: cfg?.mode ?? null,
      condition: null,
    }).subscribe({
      next: () => {
        this.clearStepModelBusy(stepId);
        // Re-resolve so the chip flips to the new effective model + source.
        this.pipelinePoll.refresh();
      },
      error: () => {
        this.clearStepModelBusy(stepId);
        this.pipelinePoll.refresh();
        this.notifs.warning(
          'Could not change the model for this step. Try again in a moment.',
          'Model change failed',
        );
      },
    });
  }

  asCliType(value: string | null | undefined): CliType | null {
    return value && (['claude', 'codex', 'gemini'] as readonly string[]).includes(value)
      ? value as CliType
      : null;
  }

  private clearStepModelBusy(stepId: string): void {
    this.savingStepModel.update(set => {
      const next = new Set(set);
      next.delete(stepId);
      return next;
    });
  }

  /** The current run's execution record, or null before any run. */
  private readonly pipelineExecution = computed<PipelineExecutionRecord | null>(
    () => this.pipelinePoll.pipeline()?.execution ?? null,
  );

  /** 1-based run counter for the current pipeline run (1 when never restarted). */
  readonly pipelineAttempt = computed<number>(() => this.pipelineExecution()?.attempt ?? 1);

  private readonly selectedPipelineExecution = computed<PipelineExecutionRecord | null>(() => {
    const current = this.pipelineExecution();
    if (current == null) return null;
    const selected = this.selectedPipelineAttempt();
    const currentAttempt = current.attempt ?? 1;
    if (selected == null || selected === currentAttempt) return current;
    return current.previousAttempts?.find(rec => (rec.attempt ?? 1) === selected) ?? current;
  });

  readonly selectedPipelineIsCurrent = computed<boolean>(() => {
    const current = this.pipelineExecution();
    const selected = this.selectedPipelineExecution();
    if (current == null || selected == null) return true;
    return (selected.attempt ?? 1) === (current.attempt ?? 1);
  });

  readonly selectedPipelineAttemptNumber = computed<number>(
    () => this.selectedPipelineExecution()?.attempt ?? this.pipelineAttempt(),
  );

  selectPipelineRun(attempt: number): void {
    const currentAttempt = this.pipelineAttempt();
    this.selectedPipelineAttempt.set(attempt === currentAttempt ? null : attempt);
    this.selectedTokenStepId.set(null);
  }

  /**
   * True when this job's pipeline has been restarted at least once, so the
   * Overview can flag the current run as a fresh attempt and surface the
   * archived prior runs. A restart shows up as attempt > 1 or as a non-empty
   * archive (belt-and-suspenders in case only one of the two is populated).
   */
  readonly isPipelineRestart = computed<boolean>(() => {
    const exec = this.pipelineExecution();
    if (exec == null) return false;
    return (exec.attempt ?? 1) > 1 || (exec.previousAttempts?.length ?? 0) > 0;
  });

  /** ISO start stamp of the current run, for the restart badge tooltip. */
  readonly pipelineStartedAt = computed<string | null>(
    () => this.pipelineExecution()?.startedAt ?? null,
  );

  /**
   * Compact summaries for the Runs chip strip. The current run stays first,
   * then prior runs follow most-recent first so an operator can swap the step
   * table between attempts after a restart.
   */
  readonly pipelineRunOptions = computed<PipelineRunOptionVm[]>(() => {
    const current = this.pipelineExecution();
    if (current == null) return [];
    return [
      this.toPipelineRunOptionVm(current, true),
      ...(current.previousAttempts ?? []).map(rec => this.toPipelineRunOptionVm(rec, false)),
    ];
  });

  /** The current / latest run, written out at the head of the chip strip. */
  readonly currentRunOption = computed<PipelineRunOptionVm | null>(
    () => this.pipelineRunOptions()[0] ?? null,
  );

  /** Prior runs, newest first, rendered as compact clickable mini chips. */
  readonly historyRunOptions = computed<PipelineRunOptionVm[]>(
    () => this.pipelineRunOptions().slice(1),
  );

  /**
   * Default number of history chips the strip renders before older runs fold
   * behind a "+N more" toggle. Keeps the strip to a single line even on a
   * heavily re-issued task; expanding wraps the full chip row rather than
   * reverting to a card grid.
   */
  private static readonly RUN_HISTORY_COLLAPSED_LIMIT = 8;

  /** Whether the strip is showing every history chip vs the collapsed window. */
  readonly runSwitcherExpanded = signal(false);

  readonly runSwitcherLimit = computed<number>(
    () => OverviewPaneComponent.RUN_HISTORY_COLLAPSED_LIMIT,
  );

  /**
   * History chips to render. Collapsed by default to the most recent
   * {@link RUN_HISTORY_COLLAPSED_LIMIT}; the rest fold behind the "+N more"
   * toggle. The actively-inspected run is kept visible even past the window so
   * collapsing never hides the run whose steps populate the table below.
   */
  readonly visibleHistoryChips = computed<PipelineRunOptionVm[]>(() => {
    const all = this.historyRunOptions();
    const limit = this.runSwitcherLimit();
    if (this.runSwitcherExpanded() || all.length <= limit) return all;
    const head = all.slice(0, limit);
    const selected = this.selectedPipelineAttemptNumber();
    if (!head.some(r => r.attempt === selected)) {
      const sel = all.find(r => r.attempt === selected);
      if (sel) head.push(sel);
    }
    return head;
  });

  /** Count of history chips hidden by the collapse window (0 when expanded). */
  readonly hiddenRunCount = computed<number>(() => {
    if (this.runSwitcherExpanded()) return 0;
    return Math.max(0, this.historyRunOptions().length - this.runSwitcherLimit());
  });

  toggleRunSwitcher(): void {
    this.runSwitcherExpanded.update(v => !v);
  }

  private toPipelineRunOptionVm(rec: PipelineExecutionRecord, current: boolean): PipelineRunOptionVm {
    const steps = rec.steps ?? [];
    const passed = steps.filter(s => s.status === 'passed').length;
    const failed = steps.filter(s => s.status === 'failed').length;
    const durationMs = this.recordDurationMs(rec);
    const attempt = rec.attempt ?? 1;
    const startedAt = rec.startedAt ?? null;
    const kind: PipelineRunOptionVm['kind'] = failed > 0 ? 'fail' : passed > 0 ? 'pass' : 'pending';
    const settledWithoutExecutedSteps =
      passed === 0
      && failed === 0
      && (
        rec.completedAt != null
        || (
          current
          && OverviewPaneComponent.PIPELINE_ATTEMPT_SETTLED_STATES.has(this.job().state)
          && (
            attempt > 1
            || (rec.previousAttempts?.length ?? 0) > 0
            || this.job().orchestratorVerdict === 'escalate'
          )
        )
      );
    const emptyOutcomeLabel: PipelineRunOptionVm['emptyOutcomeLabel'] =
      settledWithoutExecutedSteps ? 'not run' : 'pending';
    const glyph = kind === 'fail' ? '✗' : kind === 'pass' ? '✓' : '·';
    return {
      attempt,
      current,
      startedAt,
      durationMs,
      passed,
      failed,
      glyph,
      kind,
      emptyOutcomeLabel,
      tooltip: this.buildRunChipTooltip(
        attempt,
        current,
        passed,
        failed,
        emptyOutcomeLabel,
        durationMs,
        startedAt,
      ),
    };
  }

  /** Wall-clock duration of an archived run from its start/complete stamps. */
  private recordDurationMs(rec: PipelineExecutionRecord): number {
    if (!rec.startedAt || !rec.completedAt) return 0;
    const start = new Date(rec.startedAt).getTime();
    const end = new Date(rec.completedAt).getTime();
    if (Number.isNaN(start) || Number.isNaN(end)) return 0;
    return Math.max(0, end - start);
  }

  /**
   * Terse hover summary for a run (chip or the written-out current run):
   * "N OK M fail · 3m34s · 6h ago". Per-run token / cost detail lives in the
   * tokens-by-model section, so this stays a single at-a-glance line.
   */
  private buildRunChipTooltip(
    attempt: number,
    current: boolean,
    passed: number,
    failed: number,
    emptyOutcomeLabel: PipelineRunOptionVm['emptyOutcomeLabel'],
    durationMs: number,
    startedAt: string | null,
  ): StructuredTooltip {
    const outcome: string[] = [];
    if (passed > 0) outcome.push(`${passed} OK`);
    if (failed > 0) outcome.push(`${failed} fail`);
    const parts: string[] = [outcome.length > 0 ? outcome.join(' ') : emptyOutcomeLabel];
    if (durationMs > 0) parts.push(this.formatStepDuration(durationMs));
    if (startedAt) parts.push(this.formatRelativeTime(startedAt));
    return {
      title: current ? `Attempt #${attempt} · Current` : `Attempt #${attempt} · superseded`,
      body: parts.join(' · '),
    };
  }

  /** True while any pipeline step is in flight. */
  private readonly anyStepRunning = computed(() =>
    this.pipelineRows().some(r => r.status === 'running'),
  );

  /**
   * Wall-clock "now", advanced once per second only while a step is running,
   * so the active step's duration counts up live between the 10 s pipeline
   * polls. Idle (no interval, no change detection) when nothing is running.
   * Deliberately not read by `pipelineRows` / `anyStepRunning` so ticking
   * the clock never re-triggers the interval-management effect below.
   */
  private readonly now = signal(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;

  private readonly manageLiveTick = effect(() => {
    if (this.anyStepRunning()) {
      if (this.tickHandle == null) {
        this.now.set(Date.now());
        this.tickHandle = setInterval(() => this.now.set(Date.now()), 1000);
      }
    } else if (this.tickHandle != null) {
      clearInterval(this.tickHandle);
      this.tickHandle = null;
    }
  });

  constructor() {
    effect(() => {
      const row = this.selectedTokenRow();
      if (row && this.tokenModalStackDisposer == null) {
        this.tokenModalStackDisposer = this.modalStack.push('overview-step-token-modal', () => {
          this.closeStepTokenModal();
        });
      } else if (!row && this.tokenModalStackDisposer != null) {
        this.tokenModalStackDisposer();
        this.tokenModalStackDisposer = null;
      }
    });
    this.destroyRef.onDestroy(() => {
      if (this.tokenModalStackDisposer != null) {
        this.tokenModalStackDisposer();
        this.tokenModalStackDisposer = null;
      }
      if (this.tickHandle != null) {
        clearInterval(this.tickHandle);
        this.tickHandle = null;
      }
    });
  }

  /**
   * Effective duration in ms for a step row: a live "now − startedAt" while
   * the step is running (so the cell ticks up), otherwise the recorded
   * `durationMs`. Reads the `now` signal so the running row re-renders each
   * second; a completed row is independent of the clock.
   */
  liveStepDurationMs(row: PipelineRowVm): number {
    if (row.status === 'running' && row.startedAt) {
      const start = new Date(row.startedAt).getTime();
      if (!Number.isNaN(start)) return Math.max(0, this.now() - start);
    }
    return row.durationMs;
  }

  /** Wall-clock "HH:MM" for a step timestamp; empty string when unset. */
  formatClock(iso: string | null): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  /**
   * Structured tooltip for a step's timing cell: absolute start (and end, or
   * a live "running for" line). Null before the step starts so a pending /
   * disabled row carries no misleading tooltip.
   */
  stepTimingTooltip(row: PipelineRowVm): StructuredTooltip | null {
    if (!row.startedAt) return null;
    const lines: string[] = [`Started: ${this.formatAbsoluteTime(row.startedAt)}`];
    if (row.status === 'running') {
      lines.push(`Running for ${this.formatStepDuration(this.liveStepDurationMs(row))}`);
    } else {
      if (row.completedAt) lines.push(`Ended: ${this.formatAbsoluteTime(row.completedAt)}`);
      if (row.durationMs > 0) lines.push(`Duration: ${this.formatStepDuration(row.durationMs)}`);
    }
    return { title: row.label, body: lines.join('\n') };
  }

  /** USD formatting: sub-cent costs need more than 2 dp to be non-zero. */
  formatCost(usd: number): string {
    return formatPipelineCost(usd);
  }

  formatAggregateCost(usd: number, anyModelUnknown: boolean): string {
    return formatPipelineAggregateCost(usd, anyModelUnknown);
  }

  formatRelativeTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const diffMs = Date.now() - d.getTime();
    const minutes = Math.round(diffMs / 60_000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.round(months / 12)}y ago`;
  }

  formatAbsoluteTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }

  /**
   * Per-step duration for the pipeline rows. Sub-second steps (most
   * deterministic Tool steps) show in ms; longer steps fall through to the
   * coarser m/s/h formatter. Returns an em-dash when nothing ran yet.
   */
  formatStepDuration(ms: number): string {
    if (ms <= 0) return '—';
    if (ms < 1000) return `${Math.round(ms)}ms`;
    return this.formatDuration(ms / 1000);
  }

  cliTypeLabel(t: CliType): string {
    return cliTypeLabel(t);
  }

  phaseLabel(phase: string | null | undefined, entered?: string | null, steerSince?: string | null): string | null {
    return lifecyclePhaseLabel(phase, entered, steerSince, this.now());
  }
}
