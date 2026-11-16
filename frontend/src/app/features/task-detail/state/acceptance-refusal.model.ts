import { Injectable, computed, inject, signal, type Signal } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../models/task.model';
import { serverErrorMessage } from '../../../utils/server-error-message';
import { AcceptanceReadinessStore, acceptanceOverrideAllowed, type AcceptanceReadinessItem } from '../../acceptance-readiness';

/**
 * AGT-3704: why the Human Review Accept cannot act now, shown next to the
 * button with the one action that resolves it. Before this, a held Accept
 * explained itself only in a tooltip and a refused move only refreshed the
 * board, so an owner could accept a card solely through the raw API.
 */
export type AcceptanceRefusalAction = 'override' | 'retry-integration' | 'send-round';

export interface AcceptanceRefusal {
  /** Owner-facing sentence: why Accept is held or was refused. */
  reason: string;
  /** The single action offered next to the reason. */
  action: AcceptanceRefusalAction;
  actionLabel: string;
  /** `hold`: read from the card's facts before any click. `server`: the server refused a click. */
  source: 'hold' | 'server';
}

/** A server refusal of the move to Accepted, kept for the card it refused. */
export interface ServerAcceptanceRefusal {
  taskKey: string;
  code: string | null;
  message: string;
}

const OVERRIDE_LABEL = 'Accept with override…';
const ROUND_LABEL = 'Send a round…';

/** The completion's own refusal as an acceptance sentence. */
export function acceptanceRefusalSentence(text: string): string {
  return text.trim().replace(/^Completion refused:\s*/i, 'Acceptance refused: ');
}

/** The one action of a refusal: the override when policy allows it, else the round that closes the gap. */
function nextAction(refusal: string, readiness: AcceptanceReadinessItem | null)
  : Pick<AcceptanceRefusal, 'action' | 'actionLabel'> {
  return !acceptanceOverrideAllowed(refusal) && readiness?.round
    ? { action: 'send-round', actionLabel: ROUND_LABEL }
    : { action: 'override', actionLabel: OVERRIDE_LABEL };
}

/**
 * The refusal for `info`, or null when Accept may act. `awaitingGit` is the
 * bounded provenance wait: that state renders its own progress skeleton, so
 * a speculative hold reason is not added beside it. `readiness` is the
 * card's completion dry run (AGT-3867): an integrated card whose content
 * review returned concerns says why Accept will be refused before the click.
 */
export function acceptanceRefusalFor(
  info: TaskInfo,
  server: ServerAcceptanceRefusal | null,
  awaitingGit: boolean,
  readiness: AcceptanceReadinessItem | null = null,
): AcceptanceRefusal | null {
  if (info.state !== TaskState.HumanReview) return null;
  // The attempt-gap notice owns the run-attempt refusal and its override.
  if (info.acceptanceAttemptGap) return null;
  if (server && server.taskKey === info.taskKey && server.code !== 'run-attempt-missing')
    return { reason: server.message, ...nextAction(server.message, readiness), source: 'server' };
  if (awaitingGit) return null;
  // AGT-3874: until the dry run answers (after a reload), a content verdict
  // that is not `pass` already says why Accept will be refused.
  return integrationHold(info) ?? (readiness ? readinessHold(readiness) : contentVerdictHold(info));
}

/**
 * AGT-3867: the Accept tooltip of a held or refused card. It says why the
 * click will be refused and names the step that resolves it, instead of only
 * describing the merge.
 */
export function acceptanceRefusalTooltip(refusal: AcceptanceRefusal): string {
  const reason = refusal.source === 'hold' ? `Accept will be refused. ${refusal.reason}` : refusal.reason;
  return `${reason} Next step: ${refusal.actionLabel.replace(/…$/, '')}.`;
}

/** The Accept tooltip of `info` from the recorded refusal and the readiness dry run, in an injection context. */
export function injectAcceptanceRefusalTooltip(info: () => TaskInfo): Signal<string | null> {
  const refusals = inject(AcceptanceRefusalStore);
  const readiness = inject(AcceptanceReadinessStore);
  return computed(() => {
    const card = info();
    const refusal = acceptanceRefusalFor(card, refusals.forTask(card.taskKey), false, readiness.itemFor(card));
    return refusal ? acceptanceRefusalTooltip(refusal) : null;
  });
}

/** A refused completion dry run, except the run-attempt gap, which has its own notice. */
function readinessHold(item: AcceptanceReadinessItem | null): AcceptanceRefusal | null {
  if (!item || item.readiness === 'ready' || item.stage === 'admitted' || item.stage === 'lineage') return null;
  return { reason: item.reason, ...nextAction(item.refusal, item), source: 'hold' };
}

function integrationHold(info: TaskInfo): AcceptanceRefusal | null {
  const integration = info.integration;
  const status = integration?.status;
  if (status === 'integrated' || status === 'not-applicable') return null;
  const branch = integration?.integrationBranch || 'develop';
  if (status === 'conflict-skipped') {
    const failure = integration?.failure;
    return {
      reason: failure?.reason || failure?.label || `Integration into ${branch} failed.`,
      action: 'retry-integration',
      actionLabel: 'Retry integration',
      source: 'hold',
    };
  }
  const reason = !integration
    ? 'No integration verdict for this delivery yet.'
    : integration.reachUnavailable
      ? `The ${branch} reach check could not run.`
      : status === 'merged-locally'
        ? `The delivery is merged locally but not yet pushed to ${branch}.`
        : status === 'partial'
          ? `Part of the delivery is not in ${branch} yet.`
          : status === 'no-branch'
            ? 'No delivery branch or commit was found for this card.'
            : `The delivery is not in ${branch} yet.`;
  return { reason, action: 'override', actionLabel: OVERRIDE_LABEL, source: 'hold' };
}

/**
 * AGT-3874: the hold of the latest content verdict (requirement-fit, or
 * concept-fit for a concept card) when it is not `pass`, in its summary, else
 * null. The completion content check refuses Accept on any such verdict; a
 * card with no content verdict yet adds no hold here.
 */
export function contentVerdictHold(info: TaskInfo): AcceptanceRefusal | null {
  const aspect = info.mode === 'concept' ? 'concept-fit' : 'requirement-fit';
  const verdict = info.reviewProjection?.attempts?.[0]?.aspects
    ?.find(row => row.aspect?.toLowerCase() === aspect);
  const status = verdict?.status?.trim().toLowerCase();
  if (!verdict || !status || status === 'pass') return null;
  const reason = verdict.summary?.trim() || `The ${aspect} review returned ${status}.`;
  return { reason, action: 'override', actionLabel: OVERRIDE_LABEL, source: 'hold' };
}

/** The refusal sentence of a failed move to Accepted, in the server's words. */
export function serverAcceptanceRefusal(info: TaskInfo, error: unknown): ServerAcceptanceRefusal {
  const body = (error as { error?: { code?: unknown; hint?: unknown } } | null)?.error;
  const code = typeof body?.code === 'string' ? body.code : null;
  const hint = typeof body?.hint === 'string' && body.hint.trim() ? body.hint.trim() : null;
  return {
    taskKey: info.taskKey,
    code,
    message: hint ?? acceptanceRefusalSentence(serverErrorMessage(error, 'The server refused the acceptance.')),
  };
}

/**
 * The latest server refusal per card. The refused click rolls the optimistic
 * navigation back to the card, so the toolbar can show the refusal there.
 */
@Injectable({ providedIn: 'root' })
export class AcceptanceRefusalStore {
  private readonly refusals = signal<ReadonlyMap<string, ServerAcceptanceRefusal>>(new Map());

  forTask(taskKey: string | null | undefined): ServerAcceptanceRefusal | null {
    return taskKey ? this.refusals().get(taskKey) ?? null : null;
  }

  record(refusal: ServerAcceptanceRefusal): void {
    this.refusals.update(current => new Map(current).set(refusal.taskKey, refusal));
  }

  clear(taskKey: string): void {
    if (!this.refusals().has(taskKey)) return;
    this.refusals.update(current => {
      const next = new Map(current);
      next.delete(taskKey);
      return next;
    });
  }
}
