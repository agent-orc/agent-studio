import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import type { PipelineCatalogueStep, PipelineStepSetting, PipelineStepCondition,
  PipelineStepConditionToken, PipelineType } from '../../../task-pipeline';
import type { ProjectPipelineCostTimeline } from '../../../project-token-usage';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { TooltipDirective, type StructuredTooltip } from 'coding-agent-chat/shared';
import {
  PIPELINE_GATE_MODES,
  PIPELINE_CONDITIONS,
  PIPELINE_CONDITION_VALUE_TOKENS,
  PIPELINE_TOKEN_WINDOW_DAYS,
  type PipelineAdminRow,
  type PipelineGroup,
  phaseForStep,
  pipelineTypeOverrides,
  pipelinePhaseLabel,
  pipelineOrderSection,
  orderedPipelineCatalogue,
  canMovePipelineStep,
  formatTokens,
  stepTokenLabel,
  stepTokenTooltip,
  pipelineTokenCostByStep,
} from './pipeline-config.util';
import { PipelineStepFocusDirective } from './pipeline-step-focus.directive';
import { PipelineHealthBlockComponent } from '../pipeline-health-block/pipeline-health-block';
import { PipelineStepExecutionComponent } from './pipeline-step-execution/pipeline-step-execution.component';
import { PipelineTypePickerComponent } from './pipeline-type-picker/pipeline-type-picker.component';
import { PipelineStepRowStateComponent } from './pipeline-step-row-state/pipeline-step-row-state.component';
import { PipelineModelMigrationRowComponent } from './pipeline-model-migration-row/pipeline-model-migration-row.component';
/** Per-type project pipeline editor for ordering, activation, agents, prompts, gates, and usage. */
@Component({
  selector: 'app-project-pipeline-panel', standalone: true,
  imports: [FormsModule, CliModelSelectorComponent, PipelineModelMigrationRowComponent, TooltipDirective, PipelineHealthBlockComponent, PipelineStepExecutionComponent,
    PipelineTypePickerComponent, PipelineStepRowStateComponent],
  hostDirectives: [{ directive: PipelineStepFocusDirective, inputs: ['focusStepId'] }],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-pipeline-panel.component.html',
  styleUrl: './project-pipeline-panel.component.scss',
})
export class ProjectPipelinePanelComponent {
  readonly projectName = input.required<string>();
  /** Deep-link to the Prompts registry rail (content is managed there). */
  readonly openPrompts = output<void>();
  private readonly jobService = inject(TaskService);
  readonly catalogue = signal<readonly PipelineCatalogueStep[]>([]);
  readonly pipelineType = signal<PipelineType>('task');
  readonly overrides = signal<Record<string, PipelineStepSetting>>({});
  readonly order = signal<readonly string[]>([]);
  readonly pipelineCost = signal<ProjectPipelineCostTimeline | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly cliTypes = CLI_TYPES;
  readonly gateModes = PIPELINE_GATE_MODES;
  readonly conditions = PIPELINE_CONDITIONS;

  /** Window for the per-step token rollup shown on each row. */
  readonly tokenWindowDays = PIPELINE_TOKEN_WINDOW_DAYS;

  /** Per-step write in flight; disables that row's controls until the PUT resolves. */
  readonly stepBusy: Record<string, boolean> = {};
  readonly orderBusy = signal(false);
  readonly draggingStepId = signal<string | null>(null);
  readonly dragTargetStepId = signal<string | null>(null);

  /** Drafts let value-bearing conditions show their input before persistence. */
  readonly conditionDraft = signal<Record<string, { when: string; value: string }>>({});

  constructor() {
    effect(() => {
      const name = this.projectName();
      const type = this.pipelineType();
      if (name) this.load(name, type);
    });
  }

  /**
   * One row per configurable step: catalogue metadata joined with the
   * project's override. No override (or null `enabled`) falls back to the
   * step's `defaultEnabled`. Empty model/mode = inherit.
   */
  readonly rows = computed<PipelineAdminRow[]>(() => {
    const overrides = this.overrides();
    const drafts = this.conditionDraft();
    const catalogue = orderedPipelineCatalogue(this.catalogue(), this.order());
    const tokenByStep = pipelineTokenCostByStep(this.pipelineCost());
    return catalogue.map((step, index) => {
      const ov = overrides[step.id];
      const draft = drafts[step.id];
      const tok = tokenByStep.get(step.id);
      const conditionWhen = draft?.when ?? ov?.condition?.when ?? '';
      const conditionValue = draft?.value ?? ov?.condition?.value ?? '';
      return {
        id: step.id, displayName: step.displayName, kind: step.kind,
        appliesTo: step.appliesTo ?? 'any', applicable: step.applicable ?? true,
        effectiveExecution: step.effectiveExecution ?? { executionKind: 'internal', source: 'runtime', commands: [] }, runMode: step.runMode ?? '', dependsOn: step.dependsOn ?? [],
        idempotent: step.idempotent ?? false,
        stub: step.stub ?? false,
        deferred: step.deferred ?? false,
        usesModel: step.usesModel,
        supportsEconomyModel: step.supportsEconomyModel ?? false,
        usesPrompt: step.usesPrompt,
        supportsMode: step.supportsMode,
        canDisable: step.canDisable,
        supportsCondition: step.supportsCondition,
        framework: step.framework ?? '',
        phase: step.phase ?? phaseForStep(step),
        enabled: ov?.enabled ?? step.defaultEnabled,
        economyModel: ov?.economyModel ?? false,
        cliType: ov?.cliType ?? step.cliType ?? '',
        model: ov?.model ?? '',
        thinkingLevel: ov?.thinkingLevel ?? '',
        effectiveCliType: ov?.cliType ?? step.cliType ?? (step.usesModel ? 'claude' : ''),
        effectiveModel: ov?.model ?? step.resolvedModel ?? step.model ?? '',
        effectiveModelSource: ov?.model ? 'step' : (step.modelSource ?? ''),
        effectiveThinkingLevel: ov?.thinkingLevel ?? step.resolvedThinkingLevel ?? '',
        prompt: ov?.prompt ?? '',
        promptTemplate: step.promptTemplate ?? '',
        mode: ov?.mode ?? '',
        condition: conditionWhen,
        conditionValue,
        conditionNeedsValue: PIPELINE_CONDITION_VALUE_TOKENS.includes(conditionWhen),
        canMoveUp: step.kind !== 'analysis' && canMovePipelineStep(catalogue, index, -1),
        canMoveDown: step.kind !== 'analysis' && canMovePipelineStep(catalogue, index, 1),
        tokenSum: tok?.tokens ?? null,
        tokenUnknown: tok?.unknown ?? false,
        tokenCostUsd: tok?.costUsd ?? null,
        tokenUnpricedRuns: tok?.unpricedRuns ?? 0,
        tokenPricingGaps: tok?.pricingGaps ?? [],
      };
    });
  });

  readonly groups = computed<PipelineGroup[]>(() => {
    const groups: PipelineGroup[] = [];
    for (const row of this.rows()) {
      const phase = row.phase || 'post';
      let group = groups.find(g => g.phase === phase);
      if (!group) {
        group = { phase, label: pipelinePhaseLabel(phase), rows: [] };
        groups.push(group);
      }
      group.rows.push(row);
    }
    return groups;
  });

  private load(project: string, pipelineType: PipelineType): void {
    this.catalogue.set([]);
    this.conditionDraft.set({});
    this.jobService.getPipelineCatalogue(project, pipelineType).subscribe({
      next: (cat) => {
        if (this.pipelineType() !== pipelineType) return;
        this.catalogue.set(cat.steps ?? []);
        this.loadError.set(null);
      },
      error: () => { if (this.pipelineType() === pipelineType)
        this.loadError.set('Could not load the pipeline catalogue.'); },
    });
    this.refreshOverrides(project, pipelineType);
    this.jobService.getProjectPipelineCost(project, this.tokenWindowDays).subscribe({
      next: (t) => this.pipelineCost.set(t),
      error: () => { /* tokens are a secondary read; leave null -> chips just hide */ },
    });
  }

  refreshOverrides(project: string, pipelineType: PipelineType = this.pipelineType()): void {
    this.jobService.getAllProjectSettings().subscribe({
      next: (all) => {
        if (this.pipelineType() !== pipelineType) return;
        const selected = pipelineTypeOverrides(all[project], pipelineType);
        this.overrides.set(selected.steps);
        this.order.set(selected.order);
      },
      error: () => { /* keep last known overrides */ },
    });
  }

  onStepEnabledChange(stepId: string, enabled: boolean): void {
    this.writeStep(stepId, { enabled });
  }

  onStepMove(stepId: string, direction: -1 | 1): void {
    if (this.orderBusy()) return;
    const rows = orderedPipelineCatalogue(this.catalogue(), this.order());
    const index = rows.findIndex(step => step.id === stepId);
    if (index < 0) return;

    const section = pipelineOrderSection(rows[index]);
    if (section === 'core') return;

    let target = index + direction;
    while (target >= 0 && target < rows.length && pipelineOrderSection(rows[target]) !== section) {
      target += direction;
    }
    if (target < 0 || target >= rows.length) return;

    const next = [...rows];
    [next[index], next[target]] = [next[target], next[index]];
    const stepIds = next
      .filter(step => pipelineOrderSection(step) !== 'core')
      .map(step => step.id);
    const pipelineType = this.pipelineType();

    this.orderBusy.set(true);
    this.order.set(stepIds);
    this.jobService.setProjectPipelineStepOrder(this.projectName(), pipelineType, stepIds).subscribe({
      next: (res) => {
        this.orderBusy.set(false);
        if (this.pipelineType() === pipelineType)
          this.order.set(res.pipelineStepOrder ?? stepIds);
      },
      error: () => {
        this.orderBusy.set(false);
        this.refreshOverrides(this.projectName(), pipelineType);
      },
    });
  }

  canDragStep(step: PipelineAdminRow): boolean {
    return (step.canMoveUp || step.canMoveDown) && !this.orderBusy();
  }

  onStepDragStart(event: DragEvent, step: PipelineAdminRow): void {
    if (!this.canDragStep(step)) {
      event.preventDefault();
      return;
    }
    this.draggingStepId.set(step.id);
    this.dragTargetStepId.set(null);
    event.dataTransfer?.setData('text/plain', step.id);
    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
  }

  onStepDragOver(event: DragEvent, target: PipelineAdminRow): void {
    const sourceId = this.draggingStepId() ?? event.dataTransfer?.getData('text/plain') ?? null;
    if (!sourceId || sourceId === target.id || !this.canDropStep(sourceId, target.id)) return;
    event.preventDefault();
    this.dragTargetStepId.set(target.id);
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
  }

  onStepDrop(event: DragEvent, target: PipelineAdminRow): void {
    const sourceId = this.draggingStepId() ?? event.dataTransfer?.getData('text/plain') ?? null;
    this.draggingStepId.set(null);
    this.dragTargetStepId.set(null);
    if (!sourceId || sourceId === target.id || !this.canDropStep(sourceId, target.id)) return;

    event.preventDefault();
    const rect = (event.currentTarget as HTMLElement | null)?.getBoundingClientRect();
    const insertAfter = rect ? event.clientY > rect.top + rect.height / 2 : false;
    this.reorderStep(sourceId, target.id, insertAfter);
  }

  onStepDragEnd(): void {
    this.draggingStepId.set(null);
    this.dragTargetStepId.set(null);
  }

  private canDropStep(sourceId: string, targetId: string): boolean {
    const rows = orderedPipelineCatalogue(this.catalogue(), this.order());
    const source = rows.find(step => step.id === sourceId);
    const target = rows.find(step => step.id === targetId);
    return !!source
      && !!target
      && pipelineOrderSection(source) !== 'core'
      && pipelineOrderSection(source) === pipelineOrderSection(target);
  }

  private reorderStep(sourceId: string, targetId: string, insertAfter: boolean): void {
    if (this.orderBusy()) return;
    const rows = [...orderedPipelineCatalogue(this.catalogue(), this.order())];
    const sourceIndex = rows.findIndex(step => step.id === sourceId);
    const targetIndex = rows.findIndex(step => step.id === targetId);
    if (sourceIndex < 0 || targetIndex < 0) return;
    const section = pipelineOrderSection(rows[sourceIndex]);
    if (section === 'core' || section !== pipelineOrderSection(rows[targetIndex])) return;

    const [source] = rows.splice(sourceIndex, 1);
    const targetAfterRemoval = rows.findIndex(step => step.id === targetId);
    if (targetAfterRemoval < 0) return;
    rows.splice(targetAfterRemoval + (insertAfter ? 1 : 0), 0, source);

    const stepIds = rows
      .filter(step => pipelineOrderSection(step) !== 'core')
      .map(step => step.id);
    const pipelineType = this.pipelineType();

    this.orderBusy.set(true);
    this.order.set(stepIds);
    this.jobService.setProjectPipelineStepOrder(this.projectName(), pipelineType, stepIds).subscribe({
      next: (res) => {
        this.orderBusy.set(false);
        if (this.pipelineType() === pipelineType)
          this.order.set(res.pipelineStepOrder ?? stepIds);
      },
      error: () => {
        this.orderBusy.set(false);
        this.refreshOverrides(this.projectName(), pipelineType);
      },
    });
  }

  onStepModeChange(stepId: string, mode: string): void {
    this.writeStep(stepId, { mode });
  }

  /** Clear a legacy inline prompt override; content lives in the registry. */
  clearStepPrompt(stepId: string): void {
    this.writeStep(stepId, { prompt: '' });
  }

  resetStepAgent(stepId: string): void {
    this.writeStep(stepId, { cliType: '', model: '', thinkingLevel: null });
  }

  onStepAgentCommit(
    stepId: string,
    selection: { cliType: CliType; model: string; thinkingLevel: string | null },
  ): void {
    this.writeStep(stepId, { economyModel: false, cliType: selection.cliType,
      model: selection.model, thinkingLevel: selection.thinkingLevel });
  }

  /**
   * The condition <select> changed. A non-value token persists immediately;
   * a value-bearing token (task-type / tag) keeps a draft until a value
   * exists. Picking the empty option clears the condition.
   */
  onStepConditionChange(stepId: string, when: string): void {
    const existingValue = this.conditionDraft()[stepId]?.value
      ?? this.overrides()[stepId]?.condition?.value ?? '';

    if (!when) {
      this.setConditionDraft(stepId, { when: '', value: existingValue });
      this.writeStep(stepId, { condition: null });
      return;
    }

    if (PIPELINE_CONDITION_VALUE_TOKENS.includes(when)) {
      this.setConditionDraft(stepId, { when, value: existingValue });
      if (existingValue.trim()) {
        this.writeStep(stepId, { condition: { when: when as PipelineStepConditionToken, value: existingValue.trim() } });
      }
      return;
    }

    this.setConditionDraft(stepId, { when, value: '' });
    this.writeStep(stepId, { condition: { when: when as PipelineStepConditionToken } });
  }

  onStepConditionValueInput(stepId: string, value: string): void {
    const when = this.conditionDraft()[stepId]?.when
      ?? this.overrides()[stepId]?.condition?.when ?? '';
    this.setConditionDraft(stepId, { when, value });
  }

  onStepConditionValueCommit(stepId: string): void {
    const draft = this.conditionDraft()[stepId];
    const ov = this.overrides()[stepId];
    const when = draft?.when ?? ov?.condition?.when ?? '';
    const value = (draft?.value ?? ov?.condition?.value ?? '').trim();
    if (!when || !PIPELINE_CONDITION_VALUE_TOKENS.includes(when)) return;
    this.writeStep(stepId, {
      condition: value ? { when: when as PipelineStepConditionToken, value } : null,
    });
  }

  private setConditionDraft(stepId: string, draft: { when: string; value: string }): void {
    this.conditionDraft.update(m => ({ ...m, [stepId]: draft }));
  }

  private clearConditionDraft(stepId: string): void {
    this.conditionDraft.update(m => {
      if (!(stepId in m)) return m;
      const next = { ...m };
      delete next[stepId];
      return next;
    });
  }

  /**
   * Merge one changed facet onto the step's current override and PUT the
   * whole step (the backend replaces the entry, so unchanged facets are
   * resent). `enabled` is sent as null when it equals the built-in default
   * so an at-default step clears its entry instead of leaving a dead one.
   */
  writeStep(
    stepId: string,
    patch: {
      enabled?: boolean;
      economyModel?: boolean;
      cliType?: string;
      model?: string;
      thinkingLevel?: string | null;
      prompt?: string | null;
      mode?: string;
      condition?: PipelineStepCondition | null;
    },
  ): void {
    const cur = this.overrides()[stepId] ?? {};
    const defaultEnabled = this.catalogue().find(s => s.id === stepId)?.defaultEnabled ?? true;
    const enabled = patch.enabled ?? (cur.enabled ?? defaultEnabled);
    const economyModel = patch.economyModel ?? (cur.economyModel ?? false);
    const model = (patch.model ?? cur.model ?? '').trim();
    const cliType = (patch.cliType ?? cur.cliType ?? '').trim();
    const thinkingLevel = (patch.thinkingLevel !== undefined ? patch.thinkingLevel : (cur.thinkingLevel ?? ''))?.trim() ?? '';
    const prompt = (patch.prompt !== undefined ? patch.prompt : (cur.prompt ?? ''))?.trim() ?? '';
    const mode = (patch.mode ?? cur.mode ?? '').trim();
    const condition = patch.condition !== undefined ? patch.condition : (cur.condition ?? null);
    const pipelineType = this.pipelineType();

    this.stepBusy[stepId] = true;
    this.jobService.setProjectPipelineStep(this.projectName(), {
      pipelineType,
      stepId,
      enabled: enabled === defaultEnabled ? null : enabled,
      economyModel: economyModel || null,
      cliType: cliType || null,
      model: model || null,
      thinkingLevel: thinkingLevel || null,
      prompt: prompt || null,
      mode: mode || null,
      condition: condition ?? null,
    }).subscribe({
      next: (res) => {
        this.stepBusy[stepId] = false;
        if (this.pipelineType() !== pipelineType) return;
        this.overrides.set(res.pipelineSteps ?? {});
        this.clearConditionDraft(stepId);
      },
      error: () => {
        this.stepBusy[stepId] = false;
        this.refreshOverrides(this.projectName(), pipelineType);
        if (this.pipelineType() === pipelineType) this.clearConditionDraft(stepId);
      },
    });
  }

  asCliType(value: string | null | undefined): CliType | null {
    return value && (CLI_TYPES as readonly string[]).includes(value) ? value as CliType : null;
  }

  groupSummary(group: PipelineGroup): string {
    const total = group.rows.length;
    const on = group.rows.filter(row => row.enabled).length;
    const llm = group.rows.filter(row => this.hasTokenUsage(row)).length;
    return `${total} steps · ${on} on${llm ? ` · ${llm} LLM` : ''}`;
  }

  /**
   * Tool steps are process-only and never own token usage. Core and
   * orchestrator steps can account for an LLM call even when their catalogue
   * model is runtime-owned rather than operator-configurable.
   */
  hasTokenUsage(step: PipelineAdminRow): boolean {
    const kind = this.kindKey(step.kind);
    return kind !== 'tool'
      && (step.usesModel || kind === 'core' || kind === 'orchestrator');
  }

  modelSummary(step: PipelineAdminRow): string {
    if (!step.usesModel) return 'no model';
    if (step.economyModel && !step.model) return 'Spark auto';
    return step.effectiveModel || 'runtime default';
  }

  modelSourceLabel(source: string | null | undefined): string {
    switch ((source ?? '').trim().toLowerCase()) {
      case 'step': return 'step override';
      case 'project': return 'project default';
      case 'global': return 'global default';
      case 'catalogue': return 'catalogue default';
      case 'runtime': return 'runtime default';
      default: return 'default';
    }
  }

  promptSummary(step: PipelineAdminRow): string {
    if (!step.usesPrompt) return 'no prompt';
    if (step.prompt) return 'inline override';
    return step.promptTemplate || 'catalogue default';
  }

  modeSummary(step: PipelineAdminRow): string {
    return step.mode || 'default';
  }

  conditionSummary(step: PipelineAdminRow): string {
    if (!step.condition) return 'always';
    if (step.conditionValue) return `${step.condition}: ${step.conditionValue}`;
    return step.condition;
  }

  runSetting(step: PipelineAdminRow): string {
    const mode = this.runModeLabel(step.runMode);
    return step.dependsOn.length ? `${mode} after ${step.dependsOn.join(', ')}` : mode;
  }

  runExplanation(step: PipelineAdminRow): string {
    if (step.runMode.toLowerCase() === 'parallel') {
      return 'May run together with sibling steps in this phase; dependencies still have to finish first.';
    }
    return 'Runs in sequence at this position in the pipeline.';
  }

  modelExplanation(step: PipelineAdminRow): string {
    const source = this.modelSourceLabel(step.effectiveModelSource);
    if (step.model || step.thinkingLevel || step.cliType) {
      return `Step override is set; clear it to inherit from the ${source}.`;
    }
    return `Resolved from ${source}. The selector pins CLI, model, and thinking level for this step.`;
  }

  promptExplanation(step: PipelineAdminRow): string {
    if (step.prompt) return 'Legacy inline override is active; clear it to use the registry or catalogue prompt.';
    if (step.promptTemplate) return 'Bound to a prompt registry template. Edit the content in Prompts.';
    return 'Uses the built-in catalogue prompt; no separate prompt content is stored here.';
  }

  gateExplanation(step: PipelineAdminRow): string {
    if (!step.mode) return 'Default follows the catalogue or project gate policy.';
    if (step.mode === 'off') return 'Off skips this gate even when the step is enabled.';
    if (step.mode === 'warn') return 'Warn records the result but does not block the lane transition.';
    if (step.mode === 'fail') return 'Fail can block or reissue when the gate reports a problem.';
    return 'Gate mode for this step.';
  }

  conditionExplanation(step: PipelineAdminRow): string {
    if (!step.condition) return 'Runs whenever the step is enabled.';
    if (step.condition === 'task-type') return 'Runs only when the task type matches the entered value.';
    if (step.condition === 'tag') return 'Runs only when the task carries the entered tag.';
    return 'Runs only when the selected runtime condition is true.';
  }

  retrySetting(step: PipelineAdminRow): string {
    return step.idempotent ? 'Safe to retry' : 'Side effects possible';
  }

  retryExplanation(step: PipelineAdminRow): string {
    return step.idempotent
      ? 'Repeated runs should produce the same outcome.'
      : 'Repeated runs can have side effects; rerun deliberately.';
  }

  stepTooltip(step: PipelineAdminRow): StructuredTooltip {
    const parts = [this.stepPurpose(step)];
    if (step.deferred) parts.push('Deferred: triggered by an operator action instead of automatic pickup.');
    if (step.stub) parts.push('Planned slot: recorded in the pipeline, implemented outside this executor bracket.');
    return { title: step.displayName, body: parts.join('\n') };
  }

  private runModeLabel(value: string): string {
    return value.toLowerCase() === 'parallel' ? 'parallel' : 'sequential';
  }

  stepPurpose(step: PipelineAdminRow): string {
    switch (step.id) {
      case 'pre-loop-guard':
        return 'Detects stuck auto-mode loops and surfaces the loop guard state before the agent run.';
      case 'pre-orchestrator-prep':
        return 'Optional prep pass that checks prompt clarity before work is admitted to Ready.';
      case 'pre-reissue-open-items':
        return 'On reissues, foregrounds unresolved open items so the next agent run does not restart blindly.';
      case 'core-agent-run':
        return 'Runs the task-owning CLI agent with the task prompt, branch/worktree context, and selected task model.';
      case 'post-orchestrator-review':
        return 'Early post-core completeness scan over close-out evidence before spending review tokens.';
      case 'post-orchestrator-decision':
        return 'Final orchestrator decision that accepts, reissues, or escalates after reviews and gates.';
      case 'post-code-review-grade':
        return 'LLM code-review pass that assigns the A/B/C/D quality grade visible on the task card.';
      case 'post-build-test-gate':
        return 'Runs the configured build/test gate and can reissue when the repository is red.';
      case 'post-worktree-containment':
        return 'Checks that work stayed inside the task worktree and did not leak into shared state.';
      case 'post-integrate-merge':
        return 'Keeps parallel task worktrees integrated with the project integration line.';
      case 'post-conflict-resolution':
        return 'Uses orchestrator reasoning when integration detects conflicts that need structured handling.';
      case 'post-git-commit-attribution':
        return 'Attributes commits to the task so review and merge screens know which changes belong together.';
      case 'post-merge-into-develop':
        return 'Operator-triggered delivery step that merges accepted task work into develop.';
      case 'post-merge-into-develop-push':
        return 'Pushes the integration branch to origin after the merge so integration is never only local.';
      case 'post-lint-scss':
        return 'Runs frontend stylelint for SCSS quality and can warn or fail depending on gate mode.';
      case 'post-regression-radar':
        return 'Classifies changed specs as intended, at-risk, or drift without gating the lane decision.';
      case 'post-wiki-maintenance':
        return 'Maintains common-problem wiki entries from run outcomes when enabled.';
      case 'post-wiki-learnings':
        return 'Writes per-task learnings into the project wiki from structured run evidence.';
      case 'post-agents-wiki-sync':
        return 'Keeps AGENTS/wiki pointers for designated topics consistent and collects each topic current state when enabled.';
      case 'post-abort-review':
        return 'Optional review pass after an aborted or stopped run to decide rerun, reissue, or escalation.';
      default:
        if (step.id.startsWith('aspect-')) return 'Runs an LLM aspect review and records a focused verdict for auto-review.';
        if (step.id.startsWith('post-drift-')) return 'Runs an opt-in drift analysis dimension after the main task decision.';
        return 'Pipeline step in the project processing flow.';
    }
  }

  kindKey(value: string | null | undefined): string {
    return (value ?? '').trim().toLowerCase();
  }

  kindAbbrev(value: string | null | undefined): string {
    switch (this.kindKey(value)) {
      case 'module': return 'MOD';
      case 'core': return 'COR';
      case 'orchestrator': return 'ORC';
      case 'tool': return 'TOO';
      case 'aspect': return 'ASP';
      case 'drift': return 'DRI';
      default: return (value ?? '').trim().slice(0, 3).toUpperCase();
    }
  }

  formatTokens = formatTokens;
  stepTokenLabel = stepTokenLabel;
  stepTokenTooltip = stepTokenTooltip;
}
