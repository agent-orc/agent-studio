import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskState, type CliType, type TaskInfo } from '../../../../../models/task.model';
import type { CliModelInfo } from '../../../../cli';
import type { RunRecord } from '../../../../run-timeline';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { CompletionLoopIndicatorComponent } from '../../../../task-timeline';
import { AgentWorkSummaryPollService } from '../../../../polling/services/agent-work-summary-poll.service';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { TaskTimelinePollService } from '../../../../polling/services/task-timeline-poll.service';
import type {
  PipelineExecutionRecord,
  PipelineStep,
  TaskPipelineResponse,
} from '../../../../task-pipeline';
import { ClientService } from '../../../../../services/client.service';
import { CliModelSelectorComponent } from '../../../../../components/cli-model-selector';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { DisclosureMarkerComponent } from '../../../../../components/disclosure-marker/disclosure-marker.component';
import { RegressionRadarComponent } from '../../../../regression-radar';
import type { PipelineStepResultHeader } from '../pipeline-step-result/pipeline-step-result.component';
import { ReferencesSectionComponent } from '../../references-section/references-section.component';
import { TooltipDirective, type StructuredTooltip, type TooltipSeverity } from 'coding-agent-chat/shared';
import { PipelineRunHistoryComponent } from '../pipeline-run-history/pipeline-run-history.component';
import { PipelineTokenUsageComponent } from '../pipeline-token-usage/pipeline-token-usage.component';
import { PipelineStepDetailsComponent } from '../pipeline-step-details/pipeline-step-details.component';
import { PipelineStepToggleComponent } from '../pipeline-step-toggle/pipeline-step-toggle.component';
import { PostStepControlsComponent } from '../post-step-controls/post-step-controls.component';
import {
  isSteeringKind,
  steeringInfoFromEvent,
  type SteeringInfo,
} from '../../../../../components/steering-detail';
import { cliTypeLabel } from '../../../../../services/format.util';
import { TaskService } from '../../../../../services/task.service';
import {
  CostBreakdownTriggerDirective,
  formatTokenCostDisplay,
  incompleteTokenCostLabel,
} from '../../../../tokens';
import { NotificationService } from '../../../../../services/notification.service';
import { ModalStackService } from '../../../../../services/modal-stack.service';
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
  formatAbsoluteTime,
  formatClock,
  formatDuration,
  formatTokens,
  formatRelativeTime,
  formatStepDuration,
  liveStepDurationMs,
  historicalStepStatusIcon,
  laneLabel,
  stepKindIcon,
  stepKindLabel,
  stepStatusIcon,
  stepStatusLabel,
} from './overview-pane-formatters';
import { laneTone } from '../../../../../models/lane-presentation';
import { PipelineHistoryNoticeComponent } from './pipeline-history-notice/pipeline-history-notice.component';
import { OverviewRunsComponent } from './overview-runs/overview-runs.component';
import { OverviewTitleBlockComponent } from './overview-title-block/overview-title-block.component';
import { OverviewStepTokenModalComponent } from './overview-step-token-modal/overview-step-token-modal.component';
import { OverviewAgentWorkComponent } from './overview-agent-work/overview-agent-work.component';
import { distinctStepVerdict } from './pipeline-status-verdict.util';
import type { ProtocolVerdict } from '../../protocol-pane/protocol-verdict';
import { outcomeDecisionBadge, type DecisionBadgeVm } from './outcome-decision-badge.util';
import { PipelineAspectResultComponent } from './pipeline-aspect-result/pipeline-aspect-result.component';
import {
  type PipelineRowVm, type PipelineRunOptionVm, type PipelineTotalVm,
} from './pipeline-row.vm';
import {
  FINAL_VERDICT_STEP_ID,
  buildStepExplanation,
  pipelinePhaseForKind,
} from './pipeline-step-explanations.util';
import {
  buildConcernTooltip,
  buildAspectStatusTooltip,
  buildDecisionTooltip,
  buildStepStatusTooltip,
  decisionTooltipSeverity,
  reconcileCoreVerdict,
} from './pipeline-step-tooltips.util';
@Component({
  selector: 'app-overview-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, CliModelSelectorComponent, RegressionRadarComponent, ReferencesSectionComponent, TooltipDirective, CompletionLoopIndicatorComponent, PipelineRunHistoryComponent, PipelineTokenUsageComponent, PipelineStepDetailsComponent, PipelineStepToggleComponent, PostStepControlsComponent, StudioIconComponent, DisclosureMarkerComponent, CostBreakdownTriggerDirective, PipelineHistoryNoticeComponent, OverviewRunsComponent, OverviewTitleBlockComponent, OverviewStepTokenModalComponent, OverviewAgentWorkComponent, PipelineAspectResultComponent],
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
  readonly laneTone = laneTone;
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
  readonly titleSaved = output<void>(); readonly documentRequested = output<string>();

  private readonly runTimelinePoll = inject(RunTimelinePollService);
  private readonly agentWorkPoll = inject(AgentWorkSummaryPollService);
  readonly pipelinePoll = inject(TaskPipelinePollService);
  private readonly timelinePoll = inject(TaskTimelinePollService);
  private readonly clients = inject(ClientService);
  private readonly jobService = inject(TaskService);
  private readonly notifs = inject(NotificationService);
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  readonly timeline = this.runTimelinePoll.timeline;
  readonly runs = this.runTimelinePoll.runs;

  readonly selectedTokenStepId = signal<string | null>(null);
  readonly selectedPipelineAttempt = signal<number | null>(null);
  private tokenModalStackDisposer: (() => void) | null = null;

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
  readonly agentWork = this.agentWorkPoll.summary;

  readonly hasAgentWork = computed(() => {
    const s = this.agentWork();
    return s != null && (s.toolCalls > 0 || s.recovered);
  });

  pipelineTotalScope(): string { return `Run #${this.pipelineAttempt()} incl. pre/post/review steps`; }
  pipelineTotalScopeTooltip(): string { return `This total includes the core agent run and every pre, post, and review step in Run #${this.pipelineAttempt()} only.`; }

  showAllRunsTaskTotal(): boolean {
    const pipeline = this.pipelineTotal();
    const task = this.pipelinePoll.pipeline()?.tokensByModel;
    if (pipeline == null || task == null || task.runs.length !== 1) return true;
    return pipeline.totalTokens !== task.totalTokens || Math.abs(pipeline.totalCostUsd - task.totalCostUsd) > 0.000000001;
  }

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
        statusTooltip: buildAspectStatusTooltip(label, status, verdict, statusDetail) ?? buildStepStatusTooltip(label, status, statusDetail),
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
        aspectSummary: step.kind === 'aspect' ? statusDetail : null, aspectEvidence: res.aspectEvidence?.[step.id] ?? [],
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

  costDisplay(costUsd: number, totalTokens: number, unpricedRuns: number): string {
    return formatTokenCostDisplay({ costUsd, totalTokens, unpricedRuns });
  }

  incompleteCostLabel(unpricedRuns: number): string {
    return incompleteTokenCostLabel(unpricedRuns);
  }

  isPartialCost(costUsd: number, unpricedRuns: number): boolean {
    return costUsd > 0 && unpricedRuns > 0;
  }

  /** Task-total tokens + cost across all recorded steps. */
  readonly pipelineTotal = computed<PipelineTotalVm | null>(() => {
    if (this.selectedPipelineExecution() == null || !this.selectedPipelineIsCurrent()) return null;
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
  /** Live tick, also passed to the title block so the phase chip's elapsed
   *  wording advances on the same clock as the step timings. */
  readonly now = signal(Date.now());
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
    return liveStepDurationMs(row, this.now());
  }

  readonly formatClock = formatClock;

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

  readonly formatRelativeTime = formatRelativeTime;
  readonly formatAbsoluteTime = formatAbsoluteTime;

  readonly formatStepDuration = formatStepDuration;

  cliTypeLabel(t: CliType): string {
    return cliTypeLabel(t);
  }

}
