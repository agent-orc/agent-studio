/**
 * Transition chain for the Overview pipeline block (AGT-3015).
 *
 * The step table shows one row per step; a decision that ran three times in
 * three review rounds collapses into its latest verdict there. This pure
 * helper unfolds every execution of every step (earlier runs included),
 * orders them in time, and turns each orchestrator-kind execution into one
 * transition: which step ended before it, what was decided, on which model,
 * how long it took, what it cost, and where its evidence lives.
 *
 * Dependency-free so the ordering and the cost wording are unit-tested
 * against fixtures, apart from the host component.
 */
import type {
  PipelineExecutionRecord,
  PipelineStepExecution,
  PipelineStepRunSummary,
  StepCostBasis,
  StepKind,
} from '../../../../../task-pipeline/models/task-pipeline.model';
import { formatTokenCostUsd } from '../../../../../tokens/token-cost-tooltip.util';

/** How a transition's cost reads: a priced amount, a measured zero, an unpriced model, or a gap. */
export type DecisionCostTone = 'priced' | 'zero' | 'unpriced' | 'unmeasured';

export interface DecisionTransitionVm {
  /** Stable identity: step id, start time and execution index. */
  key: string;
  stepId: string;
  /** Display name of the deciding step. */
  decisionLabel: string;
  /** Display name of the step that ended before this decision, or null at the start. */
  fromLabel: string | null;
  verdict: string | null;
  summary: string | null;
  /** Model that made the call; null for a rule or human decision. */
  model: string | null;
  thinkingLevel: string | null;
  modelSource: string | null;
  /** `model` when an LLM decided, `rule` when the decision ran without one. */
  decidedBy: 'model' | 'rule';
  durationMs: number;
  completedAt: string | null;
  tokens: number;
  costText: string;
  costTone: DecisionCostTone;
  evidenceRef: string | null;
  /** 1-based position among this step's executions in the attempt. */
  occurrence: number;
  occurrences: number;
}

/** Minimal step metadata the chain needs; keyed by lower-cased step id. */
export type StepLabelLookup = (stepId: string) => string;

interface Execution {
  stepId: string;
  kind: StepKind;
  run: PipelineStepRunSummary;
  index: number;
  total: number;
}

/**
 * Every execution of every step in one attempt, oldest first. A step whose
 * row is still pending or planned contributes nothing.
 */
function executions(record: PipelineExecutionRecord): Execution[] {
  const out: Execution[] = [];
  for (const step of record.steps) {
    const runs = runsOf(step);
    runs.forEach((run, index) => out.push({
      stepId: step.stepId,
      kind: step.kind,
      run,
      index,
      total: runs.length,
    }));
  }
  return out.sort((a, b) => timeOf(a.run) - timeOf(b.run)
    || a.stepId.localeCompare(b.stepId)
    || a.index - b.index);
}

function runsOf(step: PipelineStepExecution): PipelineStepRunSummary[] {
  const runs = [...(step.earlierRuns ?? [])];
  const tokens = step.inputTokens + step.outputTokens + step.cacheReadTokens + step.cacheCreationTokens;
  const executed = step.status === 'running' || step.status === 'passed' || step.status === 'failed' || tokens > 0;
  if (executed) {
    runs.push({
      status: step.status,
      startedAt: step.startedAt,
      completedAt: step.completedAt,
      durationMs: step.durationMs,
      model: step.model,
      thinkingLevel: step.thinkingLevel,
      modelSource: step.modelSource,
      inputTokens: step.inputTokens,
      outputTokens: step.outputTokens,
      cacheReadTokens: step.cacheReadTokens,
      cacheCreationTokens: step.cacheCreationTokens,
      costBasis: step.costBasis,
      estimatedCostUsd: step.estimatedCostUsd,
      modelPriced: step.modelPriced,
      verdict: step.verdict,
      reason: step.verdictSummary ?? step.reason,
      evidenceRef: step.evidenceRef,
    });
  }
  return runs;
}

function timeOf(run: PipelineStepRunSummary): number {
  const iso = run.completedAt ?? run.startedAt;
  const ms = iso ? Date.parse(iso) : Number.NaN;
  return Number.isNaN(ms) ? Number.MAX_SAFE_INTEGER : ms;
}

function startOf(run: PipelineStepRunSummary): number {
  const ms = run.startedAt ? Date.parse(run.startedAt) : Number.NaN;
  return Number.isNaN(ms) ? timeOf(run) : ms;
}

/** Visible cost wording. An unpriced model never reads as zero. */
export function decisionCost(run: Pick<PipelineStepRunSummary,
  'costBasis' | 'estimatedCostUsd' | 'modelPriced' | 'inputTokens' | 'outputTokens' | 'cacheReadTokens' | 'cacheCreationTokens' | 'model'>):
  { text: string; tone: DecisionCostTone } {
  const basis: StepCostBasis | null | undefined = run.costBasis;
  const tokens = run.inputTokens + run.outputTokens + run.cacheReadTokens + run.cacheCreationTokens;
  if (basis === 'deterministic') return { text: '$0 · rule', tone: 'zero' };
  if (basis === 'not-run') return { text: '$0 · not run', tone: 'zero' };
  if (run.modelPriced === false) return { text: 'no price data', tone: 'unpriced' };
  if (basis === 'model' && run.estimatedCostUsd != null) {
    if (tokens > 0 && run.estimatedCostUsd === 0) return { text: '<$0.0001', tone: 'priced' };
    return { text: formatTokenCostUsd(run.estimatedCostUsd), tone: 'priced' };
  }
  return { text: 'not measured', tone: 'unmeasured' };
}

/**
 * Build the transitions of one attempt. Each orchestrator-kind execution is
 * one transition; its `from` is the latest non-decision execution that ended
 * at or before the decision started.
 */
export function buildDecisionTransitions(
  record: PipelineExecutionRecord | null,
  labelOf: StepLabelLookup,
): DecisionTransitionVm[] {
  if (record == null) return [];
  const ordered = executions(record);
  const transitions: DecisionTransitionVm[] = [];
  for (const item of ordered) {
    if (item.kind !== 'orchestrator') continue;
    const start = startOf(item.run);
    let from: Execution | null = null;
    for (const candidate of ordered) {
      if (candidate.kind === 'orchestrator') continue;
      if (timeOf(candidate.run) > start) continue;
      if (from == null || timeOf(candidate.run) >= timeOf(from.run)) from = candidate;
    }
    const cost = decisionCost(item.run);
    const tokens = item.run.inputTokens + item.run.outputTokens
      + item.run.cacheReadTokens + item.run.cacheCreationTokens;
    const model = item.run.costBasis === 'model' || tokens > 0 ? item.run.model?.trim() || null : null;
    transitions.push({
      key: `${item.stepId}:${item.run.startedAt ?? ''}:${item.index}`,
      stepId: item.stepId,
      decisionLabel: labelOf(item.stepId),
      fromLabel: from ? labelOf(from.stepId) : null,
      verdict: item.run.verdict?.trim() || null,
      summary: item.run.reason?.trim() || null,
      model,
      thinkingLevel: model ? item.run.thinkingLevel?.trim() || null : null,
      modelSource: model ? item.run.modelSource?.trim() || null : null,
      decidedBy: model ? 'model' : 'rule',
      durationMs: Math.max(0, item.run.durationMs ?? 0),
      completedAt: item.run.completedAt ?? item.run.startedAt ?? null,
      tokens,
      costText: cost.text,
      costTone: cost.tone,
      evidenceRef: item.run.evidenceRef?.trim() || null,
      occurrence: item.index + 1,
      occurrences: item.total,
    });
  }
  return transitions;
}
