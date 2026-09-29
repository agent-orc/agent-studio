/**
 * One source for how a decision card and the work it blocks read on the board,
 * in the task detail, and in the wiki pulse inbox (AGT-2795, Dossier
 * `docs/operations/decision-cards`).
 *
 * A decision card is kind `decision`: a question with two to four options, a
 * named decider (D2 = B: a client identity or role, operator by default), and
 * dependants that stay blocked while it is pending. This module is the pure
 * half: `TaskInfo` goes in, presentation-ready values come out, so the badge,
 * the detail panel, the blocked-by chip, and the claim explanation cannot
 * drift apart.
 */
import type { DecisionContent, DecisionOption, TaskInfo } from './task.model';

/** Resolves a client identity id to a display name; returns null when unknown. */
export type DeciderNameLookup = (clientId: string) => string | null | undefined;

export function isDecisionCard(job: Pick<TaskInfo, 'kind'> | null | undefined): boolean {
  return job?.kind === 'decision';
}

/**
 * Mirrors backend `DecisionStatuses.Normalize`: only `decided` is settled;
 * `pending`, the legacy `requested`, and anything unknown read as open.
 */
export function isDecisionOpen(decision: Pick<DecisionContent, 'status'> | null | undefined): boolean {
  return (decision?.status ?? '').trim().toLowerCase() !== 'decided';
}

/**
 * Human name for a decider value: `operator` is the default role, `role:<r>`
 * names a role, `owner` is the owner role, anything else is a client identity
 * id resolved through `lookup` when the registry knows it.
 */
export function deciderName(decider: string | null | undefined, lookup?: DeciderNameLookup): string {
  const value = (decider ?? '').trim();
  if (!value || value.toLowerCase() === 'operator') return 'Operator';
  if (value.toLowerCase() === 'owner') return 'Owner';
  if (value.toLowerCase().startsWith('role:')) {
    const role = value.slice(5).trim();
    return role ? `${role.charAt(0).toUpperCase()}${role.slice(1)}` : 'Operator';
  }
  return lookup?.(value) || value;
}

export interface DecisionBadgeView {
  open: boolean;
  /** `Decision` while pending, `Decided` once settled. */
  label: string;
  /** Resolved decider name shown next to the badge. */
  decider: string;
  tooltip: string;
}

/** Kind badge for a decision card; null for every other kind. */
export function buildDecisionBadge(job: TaskInfo, lookup?: DeciderNameLookup): DecisionBadgeView | null {
  if (!isDecisionCard(job) || !job.decision) return null;
  const open = isDecisionOpen(job.decision);
  const decider = deciderName(job.decision.decider, lookup);
  const blocks = job.decision.dependants?.length ?? 0;
  const tooltip = open
    ? `Decision card: waiting for ${decider} to choose an option.`
      + (blocks > 0 ? ` Blocks ${job.decision.dependants!.join(', ')}.` : '')
    : `Decision card: decided by ${deciderName(job.decision.decidedBy, lookup) || decider}.`;
  return { open, label: open ? 'Decision' : 'Decided', decider, tooltip };
}

/**
 * Keys of the pending decision cards this card waits on. Prefers the
 * server-computed `blockedBy`; falls back to `waitsOn` edges flagged
 * `pendingDecision` for payloads that carry only the waits-on projection.
 */
export function decisionBlockers(job: Pick<TaskInfo, 'blockedBy' | 'waitsOn'> | null | undefined): string[] {
  if (!job) return [];
  if (job.blockedBy && job.blockedBy.length > 0) return job.blockedBy;
  return job.waitsOn?.items.filter((item) => item.pendingDecision && !item.fulfilled).map((item) => item.key) ?? [];
}

/**
 * The sentence the claim, start, and move controls show while a card waits on
 * a pending decision. Mirrors the backend `DecisionLaneGuard` refusal so the
 * disabled control and the 4xx say the same thing. Null when not blocked.
 */
export function decisionBlockReason(keys: readonly string[]): string | null {
  if (keys.length === 0) return null;
  const noun = keys.length === 1 ? 'decision' : 'decisions';
  return `Blocked by pending ${noun} ${keys.join(', ')}. It cannot be claimed or moved to Ready or In Progress until the decider chooses an option.`;
}

/** Lanes a blocked card may not enter; mirrors backend `DecisionLaneGuard`. */
export const DECISION_BLOCKED_TARGET_LANES: readonly string[] = ['2-ready', '3-progress'];

/**
 * Why a move of `job` to `targetState` would be refused by the decision lane
 * guard, or null when the decision rules allow it. Decision cards never enter
 * a runner lane; cards waiting on a pending decision cannot enter Ready or
 * In Progress.
 */
export function decisionMoveRefusal(job: TaskInfo, targetState: string): string | null {
  if (isDecisionCard(job)) {
    const open = isDecisionOpen(job.decision);
    if (targetState === '1-preparation') {
      return open ? null : 'A decided decision card returns to Preparation only through Reopen decision.';
    }
    if (targetState === '6-completed' || targetState === '7-archive') {
      return open ? 'A pending decision cannot be completed or archived. Choose an option first.' : null;
    }
    return 'Decision cards are decided, not run: they never enter a runner lane.';
  }
  if (!DECISION_BLOCKED_TARGET_LANES.includes(targetState)) return null;
  return decisionBlockReason(decisionBlockers(job));
}

/** Pending decision cards in a job list (the project header count and inbox). */
export function openDecisionCards<T extends TaskInfo>(jobs: readonly T[]): T[] {
  return jobs.filter((job) => isDecisionCard(job) && job.decision && isDecisionOpen(job.decision));
}

export function recommendedOption(decision: DecisionContent): DecisionOption | null {
  const id = decision.recommendedOptionId?.trim().toLowerCase();
  if (!id) return null;
  return decision.options.find((option) => option.id.trim().toLowerCase() === id) ?? null;
}

export function chosenOption(decision: DecisionContent): DecisionOption | null {
  const id = decision.chosenOptionId?.trim().toLowerCase();
  if (!id) return null;
  return decision.options.find((option) => option.id.trim().toLowerCase() === id) ?? null;
}

/** True when the due date has passed while the decision is still open. */
export function isDecisionOverdue(decision: DecisionContent, nowMs: number = Date.now()): boolean {
  if (!isDecisionOpen(decision) || !decision.dueDate) return false;
  const due = Date.parse(decision.dueDate);
  return Number.isFinite(due) && due < nowMs;
}

/** One pending decision card as the wiki pulse inbox lists it. */
export interface DecisionInboxItem {
  /** Internal `project::id` key used to open the card. */
  taskKey: string;
  /** Display key, e.g. `AGT-2792`. */
  key: string;
  title: string;
  question: string;
  decider: string;
  dueDate: string | null;
  overdue: boolean;
  /** Keys of the cards this decision blocks. */
  blocks: string[];
}

/**
 * Pending decision cards of one project, overdue first, then oldest first, for
 * the workbench inbox beside the Dossier decisions (Dossier §7 "Pending").
 */
export function buildDecisionInboxItems(
  jobs: readonly TaskInfo[],
  projectName: string,
  lookup?: DeciderNameLookup,
  nowMs: number = Date.now(),
): DecisionInboxItem[] {
  const overdue = (job: TaskInfo): boolean => isDecisionOverdue(job.decision!, nowMs);
  return openDecisionCards(jobs)
    .filter((job) => job.projectName === projectName && job.state !== '7-archive')
    .sort((a, b) => Number(overdue(b)) - Number(overdue(a))
      || (a.createdAt ?? '').localeCompare(b.createdAt ?? '')
      || (a.key ?? a.id).localeCompare(b.key ?? b.id))
    .map((job) => ({
      taskKey: job.taskKey,
      key: job.key || job.displayKey || job.id,
      title: job.title || job.id,
      question: job.decision!.question,
      decider: deciderName(job.decision!.decider, lookup),
      dueDate: job.decision!.dueDate ?? null,
      overdue: overdue(job),
      blocks: job.decision!.dependants ?? [],
    }));
}
