import { Injectable, signal } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../models/task.model';
import { serverErrorMessage } from '../../../utils/server-error-message';

/**
 * AGT-3704: why the Human Review Accept cannot act now, shown next to the
 * button with the one action that resolves it. Before this, a held Accept
 * explained itself only in a tooltip and a refused move only refreshed the
 * board, so an owner could accept a card solely through the raw API.
 */
export type AcceptanceRefusalAction = 'override' | 'retry-integration';

export interface AcceptanceRefusal {
  /** Owner-facing sentence: why Accept is held or was refused. */
  reason: string;
  /** The single action offered next to the reason. */
  action: AcceptanceRefusalAction;
  actionLabel: string;
  /** `hold`: the client keeps Accept disabled. `server`: the server refused a click. */
  source: 'hold' | 'server';
}

/** A server refusal of the move to Accepted, kept for the card it refused. */
export interface ServerAcceptanceRefusal {
  taskKey: string;
  code: string | null;
  message: string;
}

const OVERRIDE_LABEL = 'Accept with override…';

/**
 * The refusal for `info`, or null when Accept may act. `awaitingGit` is the
 * bounded provenance wait: that state renders its own progress skeleton, so
 * a speculative hold reason is not added beside it.
 */
export function acceptanceRefusalFor(
  info: TaskInfo,
  server: ServerAcceptanceRefusal | null,
  awaitingGit: boolean,
): AcceptanceRefusal | null {
  if (info.state !== TaskState.HumanReview) return null;
  // The attempt-gap notice owns the run-attempt refusal and its override.
  if (info.acceptanceAttemptGap) return null;
  if (server && server.taskKey === info.taskKey && server.code !== 'run-attempt-missing')
    return { reason: server.message, action: 'override', actionLabel: OVERRIDE_LABEL, source: 'server' };
  if (awaitingGit) return null;
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

/** The refusal sentence of a failed move to Accepted, in the server's words. */
export function serverAcceptanceRefusal(info: TaskInfo, error: unknown): ServerAcceptanceRefusal {
  const body = (error as { error?: { code?: unknown; hint?: unknown } } | null)?.error;
  const code = typeof body?.code === 'string' ? body.code : null;
  const hint = typeof body?.hint === 'string' && body.hint.trim() ? body.hint.trim() : null;
  return {
    taskKey: info.taskKey,
    code,
    message: hint ?? serverErrorMessage(error, 'The server refused the acceptance.'),
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
