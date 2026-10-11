/**
 * Pipeline group derivation for the Overview pipeline block.
 *
 * The Overview must always represent the complete configured pipeline shape,
 * but a flat list of 20+ steps is hard to scan. These pure helpers fold the
 * ordered per-step rows into collapsible *sections* — one per contiguous run
 * of the same phase (PRE STEPS, CORE AGENT WORK, DECISION, TOOL, ASPECT, …) —
 * and derive the aggregate tone + collapsed-summary counters each section
 * shows whether it is expanded or collapsed.
 *
 * Kept dependency-free (no Angular, no signals, no `Date.now()`) so the
 * grouping/tone/collapse rules can be unit-tested against fixtures in
 * isolation from the 1800-line host component.
 */

/** Aggregate section state, mapped to a subtle border/header tint in the template. */
export type PipelineGroupTone =
  | 'ok'
  | 'danger'
  | 'warn'
  | 'concern'
  | 'muted'
  | 'not-applicable'
  | 'not-run'
  | 'neutral';

/** The subset of the effective display status a row can carry. */
export type PipelineRowStatusLike =
  | 'pending'
  | 'running'
  | 'passed'
  | 'failed'
  | 'skipped'
  | 'notApplicable'
  | 'needsDecision'
  | 'not-run'
  | 'planned'
  | 'disabled';

/**
 * Structural shape the grouping needs from a pipeline row. The full
 * `PipelineRowVm` in the host component is assignable to this, so the helper
 * stays decoupled from the component's presentation fields.
 */
export interface PipelineGroupRowLike {
  phaseKey: string;
  phaseLabel: string;
  phaseDescription: string;
  status: PipelineRowStatusLike;
  verdict: string | null;
  totalTokens: number;
  /** True for local pipeline work deliberately omitted by a remote lifecycle. */
  remoteNotApplicable?: boolean;
  /** True when an unexecuted step was required and needs operator attention. */
  attentionRequired?: boolean;
}

/** One collapsible pipeline section, carrying its rows and aggregate counters. */
export interface PipelineGroupVm<R extends PipelineGroupRowLike = PipelineGroupRowLike> {
  /** Stable key: phase + occurrence index, so repeated TOOL/DECISION runs stay distinct. */
  key: string;
  phaseKey: string;
  label: string;
  description: string;
  rows: R[];
  tone: PipelineGroupTone;
  /** Total rows in the section (including disabled). */
  stepCount: number;
  /** Rows that have actually executed (passed/failed/running). */
  ranCount: number;
  /** Rows needing attention: running, failed, or flagged with a concern. */
  riskCount: number;
  /** Rows switched off by config (disabled). */
  offCount: number;
  /** Rows carrying an unresolved concern (concern/block/escalate verdict or a failure). */
  concernCount: number;
  /** Honest sum of the token use present on the section's rows. */
  totalTokens: number;
  /** Quiet sections collapse by default; only active/problematic ones open. */
  defaultCollapsed: boolean;
}

/**
 * One-word status label for a section's aggregate tone. Backs the collapsed
 * summary and the header accessible name so section state never rides on colour
 * alone (WCAG 1.4.1).
 */
export function groupToneLabel(tone: PipelineGroupTone): string {
  switch (tone) {
    case 'ok':     return 'Passed';
    case 'danger': return 'Attention';
    case 'warn':   return 'Running';
    case 'concern': return 'Concerns';
    case 'muted':  return 'Disabled';
    case 'not-applicable': return 'Not applicable';
    case 'not-run': return 'Not run';
    default:       return 'Pending';
  }
}

/**
 * Accessible name for a collapsible section header. Folds the phase label, the
 * tone status word, the step count, and any concern count into one string;
 * expanded/collapsed state is conveyed separately through `aria-expanded`.
 */
export function groupAriaLabel(
  group: Pick<PipelineGroupVm, 'label' | 'tone' | 'stepCount' | 'concernCount' | 'description'>,
): string {
  const parts = [`${group.label} phase`, groupToneLabel(group.tone).toLowerCase()];
  parts.push(group.stepCount === 1 ? '1 step' : `${group.stepCount} steps`);
  if (group.concernCount > 0) {
    parts.push(group.concernCount === 1 ? '1 concern' : `${group.concernCount} concerns`);
  }
  return `${parts.join(', ')}. ${group.description}`;
}

const BLOCKING_VERDICTS = new Set([
  'block',
  'blocked',
  'escalate',
  'loop-detected',
]);

const CONCERN_VERDICTS = new Set([
  'concern',
  'concerns',
  'block',
  'blocked',
  'escalate',
  'looping',
  'loop-detected',
]);

function verdictKey(verdict: string | null): string {
  return (verdict ?? '').trim().toLowerCase();
}

/** A row that has run to some executed state (as opposed to pending/planned/off). */
function rowHasRun(status: PipelineRowStatusLike): boolean {
  return status === 'passed' || status === 'failed' || status === 'running' || status === 'needsDecision';
}

/** A row the operator should look at right now: active or problematic. */
export function rowIsRisk(row: PipelineGroupRowLike): boolean {
  if (row.status === 'running' || row.status === 'failed' || row.attentionRequired) return true;
  return CONCERN_VERDICTS.has(verdictKey(row.verdict));
}

/** A row carrying an unresolved concern (drives the concern rollup + row marker). */
export function rowHasConcern(row: PipelineGroupRowLike): boolean {
  if (row.status === 'failed' || row.status === 'needsDecision' || row.attentionRequired) return true;
  return CONCERN_VERDICTS.has(verdictKey(row.verdict));
}

/** A row that forces the whole section into the danger tone. */
function rowIsBlocking(row: PipelineGroupRowLike): boolean {
  if (row.status === 'failed' || row.attentionRequired) return true;
  return BLOCKING_VERDICTS.has(verdictKey(row.verdict));
}

/**
 * Aggregate tone for a section:
 * - danger  — any contained step failed / blocked / needs a human decision;
 * - warn    — any contained step is running;
 * - concern — a non-blocking concern needs review;
 * - ok      — there is executable work and every executable step passed;
 * - muted   — the section is entirely disabled/skipped (nothing executable);
 * - neutral — nothing has run yet (pending).
 * Executable excludes disabled/skipped/planned so an all-off section reads muted,
 * not falsely "ok".
 */
export function groupTone(rows: readonly PipelineGroupRowLike[]): PipelineGroupTone {
  if (rows.some(rowIsBlocking)) return 'danger';
  if (rows.some(r => r.status === 'running')) return 'warn';
  if (rows.some(rowHasConcern)) return 'concern';
  const executable = rows.filter(
    r => r.status !== 'disabled' && r.status !== 'skipped' && r.status !== 'notApplicable'
      && r.status !== 'not-run' && r.status !== 'planned',
  );
  if (executable.length > 0 && executable.every(r => r.status === 'passed')) return 'ok';
  if (executable.length === 0 && rows.some(r => r.status === 'not-run')) return 'not-run';
  if (executable.length === 0 && rows.some(r => r.status === 'notApplicable' || r.remoteNotApplicable)) return 'not-applicable';
  if (executable.length === 0) return 'muted';
  return 'neutral';
}

/** The pipeline has started once at least one row has executed. */
export function pipelineStarted(rows: readonly PipelineGroupRowLike[]): boolean {
  return rows.some(r => rowHasRun(r.status));
}

/**
 * The pipeline is complete once no enabled row is still waiting to run
 * (nothing pending / running / planned among the non-disabled rows).
 */
export function pipelineComplete(rows: readonly PipelineGroupRowLike[]): boolean {
  return !rows.some(
    r =>
      r.status !== 'disabled' &&
      (r.status === 'pending' || r.status === 'running' || r.status === 'planned'),
  );
}

/** Only active or problematic sections start open. */
export function groupDefaultCollapsed(group: Pick<PipelineGroupVm, 'tone'>): boolean {
  return group.tone !== 'danger' && group.tone !== 'warn' && group.tone !== 'concern';
}

/**
 * Fold ordered per-step rows into collapsible sections. Sections break on a
 * change of phase, so a non-contiguous phase (e.g. the several TOOL runs in the
 * post-bracket) yields several distinct sections in pipeline order rather than
 * one merged, reordered block.
 */
export function buildPipelineGroups<R extends PipelineGroupRowLike>(
  rows: readonly R[],
): PipelineGroupVm<R>[] {
  const groups: PipelineGroupVm<R>[] = [];
  const occurrences = new Map<string, number>();

  for (const row of rows) {
    const last = groups[groups.length - 1];
    if (!last || last.phaseKey !== row.phaseKey) {
      const occ = occurrences.get(row.phaseKey) ?? 0;
      occurrences.set(row.phaseKey, occ + 1);
      groups.push({
        key: `${row.phaseKey}#${occ}`,
        phaseKey: row.phaseKey,
        label: row.phaseLabel,
        description: row.phaseDescription,
        rows: [row],
        tone: 'neutral',
        stepCount: 0,
        ranCount: 0,
        riskCount: 0,
        offCount: 0,
        concernCount: 0,
        totalTokens: 0,
        defaultCollapsed: false,
      });
    } else {
      last.rows.push(row);
    }
  }

  for (const group of groups) {
    group.stepCount = group.rows.length;
    group.ranCount = group.rows.filter(r => rowHasRun(r.status)).length;
    group.riskCount = group.rows.filter(rowIsRisk).length;
    group.offCount = group.rows.filter(r => r.status === 'disabled').length;
    group.concernCount = group.rows.filter(rowHasConcern).length;
    group.totalTokens = group.rows.reduce((sum, r) => sum + (r.totalTokens || 0), 0);
    group.tone = groupTone(group.rows);
    group.defaultCollapsed = groupDefaultCollapsed(group);
  }

  return groups;
}
