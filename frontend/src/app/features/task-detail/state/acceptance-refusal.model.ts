import { Injectable, computed, inject, signal, type Signal } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../models/task.model';
import { serverErrorMessage } from '../../../utils/server-error-message';
import {
  AcceptanceReadinessStore,
  acceptanceOverrideAllowed,
  acceptancePrimaryReason,
  secondaryAcceptanceGaps,
  type AcceptanceReadinessItem,
} from '../../acceptance-readiness';

/**
 * AGT-3704: why the Human Review Accept cannot act now, shown next to the
 * button with the one action that resolves it. Before this, a held Accept
 * explained itself only in a tooltip and a refused move only refreshed the
 * board, so an owner could accept a card solely through the raw API.
 */
export type AcceptanceRefusalAction = 'override' | 'retry-integration' | 'review-again' | 'send-round';

export interface AcceptanceRefusal {
  /** Owner-facing sentence: why Accept is held or was refused. */
  reason: string;
  /** The single action offered next to the reason. */
  action: AcceptanceRefusalAction;
  actionLabel: string;
  /**
   * `hold`: the client keeps Accept disabled. `server`: the server refused a click.
   * `readiness`: the completion dry run (acceptance readiness) refuses it.
   */
  source: 'hold' | 'server' | 'readiness';
  /**
   * AGT-3830: the refusal's actions replace plain Accept as the primary. Set
   * for a content refusal (review concerns, no content verdict), which a
   * click on Accept can only repeat.
   */
  replacesAccept?: true;
  /**
   * The step offered beside a content refusal's "Review again": accept with
   * a written reason when policy lets an override admit the card, else the
   * round that closes the gap (AGT-3867).
   */
  secondaryAction?: Pick<AcceptanceRefusal, 'action' | 'actionLabel'>;
  /** The content review's own concern, if the current dry run names it. */
  concern?: string | null;
  /**
   * F12: set when the acceptance-readiness answer ranks the reason. `reason`
   * is then its primary sentence, and other gaps of the card are secondary.
   */
  headline?: string;
  nextStep?: string;
  secondary?: readonly string[];
}

/** A server refusal of the move to Accepted, kept for the card it refused. */
export interface ServerAcceptanceRefusal {
  taskKey: string;
  code: string | null;
  message: string;
  /** F2: the run whose delivery was refused (its start); a later run's delivery drops the refusal. */
  delivery?: string | null;
}

const OVERRIDE_LABEL = 'Accept with override…';
const ROUND_LABEL = 'Send a round…';
const CONTENT_GAP = 'completion-content-gap';

/** The completion's own refusal as an acceptance sentence. */
export function acceptanceRefusalSentence(text: string): string {
  return text.trim().replace(/^Completion refused:\s*/i, 'Acceptance refused: ');
}

/** The one action of a refusal: the override when policy allows it, else the round that closes the gap. */
function nextAction(refusal: string, readiness: AcceptanceReadinessItem | null, code: string | null = null)
  : Pick<AcceptanceRefusal, 'action' | 'actionLabel'> {
  const canOverride = code === 'batch-gate-evidence-missing' || readiness?.stage === 'batch-gate'
    ? false
    : readiness ? acceptanceOverrideAllowed(readiness) : code === CONTENT_GAP ? contentRefusalOverrideAllowed(refusal) : true;
  return !canOverride
    ? { action: 'send-round', actionLabel: ROUND_LABEL }
    : { action: 'override', actionLabel: OVERRIDE_LABEL };
}

/** Readiness classes a click on Accept is refused for on content grounds. */
const CONTENT_REFUSAL_CLASSES: ReadonlySet<string> = new Set(['review-concerns', 'no-content-verdict']);
const CONTENT_REFUSAL_CODE = 'completion-content-gap';

function contentRefusal(reason: string, source: AcceptanceRefusal['source'], refusal: string,
  readiness: AcceptanceReadinessItem | null, hold: AcceptanceRefusal | null = null,
  info: TaskInfo | null = null): AcceptanceRefusal {
  const secondaryAction = nextAction(refusal, readiness, CONTENT_REFUSAL_CODE);
  const primary = readiness ? acceptancePrimaryReason(readiness) : null;
  const concern = info ? acceptanceRefusalConcern(info, readiness) : null;
  return {
    reason, action: 'review-again', actionLabel: 'Review again', source, replacesAccept: true,
    ...(concern ? { concern } : {}),
    ...(primary ? { headline: primary.headline, nextStep: primary.nextStep,
      secondary: secondaryAcceptanceGaps(primary, [hold?.reason]) } : {}),
    secondaryAction: secondaryAction.action === 'override'
      ? { action: 'override', actionLabel: 'Accept with a reason…' } : secondaryAction,
  };
}

/** The readiness reason in the words the board and Needs you show. */
function readinessReason(item: AcceptanceReadinessItem): string {
  const reason = item.reason?.trim() || item.refusal?.trim() || item.headline;
  const missing = item.missing?.trim();
  return missing && !reason.includes(missing) ? `${reason} Missing: ${missing}` : reason;
}

/**
 * The refusal for `info`, or null when Accept may act. `awaitingGit` is the
 * bounded provenance wait: that state renders its own progress skeleton, so
 * a speculative hold reason is not added beside it. `readiness` is the
 * card's completion dry run (AGT-3867): when it refuses, its reason leads and
 * the integration hold is listed under it (F12), so an integrated card whose
 * content review returned concerns says why Accept will be refused before
 * the click.
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
  const ownReadiness = readiness && (readiness.taskKey === info.taskKey || readiness.jobId === info.id) ? readiness : null;
  const hold = integrationHold(info);
  // AGT-3830: a server content refusal, or the readiness check that predicts
  // it, makes the next step (review again, or accept with a written reason)
  // the primary instead of an Accept that can only be refused again.
  // The server's actual refusal outranks a stale ready dry run.
  // `AcceptanceRefusalStore.forCard` drops it once a round, a review request,
  // a newer ready check or a new delivery replaces it (F2).
  if (server && server.taskKey === info.taskKey && server.code === CONTENT_REFUSAL_CODE)
    return ownReadiness && CONTENT_REFUSAL_CLASSES.has(ownReadiness.readiness)
      ? contentRefusal(readinessReason(ownReadiness), 'server', ownReadiness.refusal, ownReadiness, hold, info)
      : contentRefusal(server.message, 'server', server.message, null, hold, info);
  if (server && server.taskKey === info.taskKey && server.code !== 'run-attempt-missing'
    && server.code !== CONTENT_REFUSAL_CODE)
    return { reason: server.message, ...nextAction(server.message, ownReadiness, server.code), source: 'server', concern: null };
  if (ownReadiness && CONTENT_REFUSAL_CLASSES.has(ownReadiness.readiness))
    return contentRefusal(readinessReason(ownReadiness), 'readiness', ownReadiness.refusal, ownReadiness, hold, info);
  if (awaitingGit) return null;
  const refused = readinessHold(ownReadiness);
  const primary = refused && ownReadiness ? acceptancePrimaryReason(ownReadiness) : null;
  // AGT-3874: until the dry run answers (after a reload), a content verdict
  // that is not `pass` already says why Accept will be refused.
  if (!refused || !primary) return hold ?? (ownReadiness ? null : contentVerdictHold(info));
  // A round is the only step when policy forbids the override; otherwise the
  // integration hold keeps its own action (a retry after a conflict).
  const action = refused.action === 'send-round' || !hold ? refused : hold;
  return {
    reason: primary.reason,
    headline: primary.headline,
    nextStep: primary.nextStep,
    secondary: secondaryAcceptanceGaps(primary, [hold?.reason]),
    action: action.action,
    actionLabel: action.actionLabel,
    source: 'hold',
  };
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
    const refusal = acceptanceRefusalFor(card, refusals.forCard(card), false, readiness.itemFor(card));
    return refusal ? acceptanceRefusalTooltip(refusal) : null;
  });
}

/** A refused completion dry run, except the run-attempt gap, which has its own notice. */
function readinessHold(item: AcceptanceReadinessItem | null): AcceptanceRefusal | null {
  if (!item || item.readiness === 'ready' || item.stage === 'admitted' || item.stage === 'lineage') return null;
  return { reason: item.reason, ...nextAction(item.refusal, item), source: 'hold' };
}

/** The client-side integration hold: why the board projection keeps Accept disabled. */
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

/** The reason of a refusal that replaces Accept, or null for any other refusal. */
export function contentRefusalReason(refusal: AcceptanceRefusal | null): string | null {
  return refusal?.replacesAccept ? refusal.reason : null;
}

/**
 * AGT-3874: the hold of the latest content verdict (requirement-fit, or
 * concept-fit for a concept card) when it is not `pass`, in its summary, else
 * null. The completion content check refuses Accept on any such verdict; a
 * card with no content verdict yet adds no hold here.
 */
export function contentVerdictHold(info: TaskInfo): AcceptanceRefusal | null {
  const verdict = contentVerdict(info);
  const status = verdict?.status?.trim().toLowerCase();
  if (!verdict || !status || status === 'pass') return null;
  const reason = verdict.summary?.trim() || `The ${verdict.aspect} review returned ${status}.`;
  return { reason, action: 'override', actionLabel: OVERRIDE_LABEL, source: 'hold' };
}

function contentVerdict(info: TaskInfo) {
  const aspect = info.mode === 'concept' ? 'concept-fit' : 'requirement-fit';
  const verdict = info.reviewProjection?.attempts?.[0]?.aspects
    ?.find(row => row.aspect?.toLowerCase() === aspect);
  return verdict ? { ...verdict, aspect } : null;
}

/**
 * AGT-3873: the concern the content review raised, in its own words: the
 * dry run's concern, else the summary of the latest content verdict that is
 * not `pass`. Null when neither names one.
 */
export function acceptanceRefusalConcern(
  info: TaskInfo,
  readiness: AcceptanceReadinessItem | null = null,
): string | null {
  const fromReadiness = readiness?.concern?.trim();
  if (fromReadiness) return fromReadiness;
  const verdict = contentVerdict(info);
  const status = verdict?.status?.trim().toLowerCase();
  return status && status !== 'pass' ? verdict?.summary?.trim() || null : null;
}

/**
 * AGT-3873: a content refusal the owner may override with a written reason,
 * and that a content review against the current brief may also resolve. The
 * completion policy lets an override replace a content verdict, never a
 * missing research report.
 */
export function acceptanceRefusalOverridable(refusal: Pick<ServerAcceptanceRefusal, 'code' | 'message'>): boolean {
  return refusal.code === CONTENT_GAP && contentRefusalOverrideAllowed(refusal.message);
}

/** Legacy move errors do not carry admission facts; preserve the report safeguard until they do. */
export function contentRefusalOverrideAllowed(refusal: string): boolean {
  return !/required report is missing/i.test(refusal);
}

/** The refusal sentence of a failed move to Accepted, in the server's words. */
export function serverAcceptanceRefusal(info: TaskInfo, error: unknown): ServerAcceptanceRefusal {
  const body = (error as { error?: { code?: unknown; hint?: unknown } } | null)?.error;
  const code = typeof body?.code === 'string' ? body.code : null;
  const hint = typeof body?.hint === 'string' && body.hint.trim() ? body.hint.trim() : null;
  const delivery = deliveryOf(info);
  return {
    taskKey: info.taskKey,
    code,
    message: hint ?? acceptanceRefusalSentence(serverErrorMessage(error, 'The server refused the acceptance.')),
    ...(delivery ? { delivery } : {}),
  };
}

function deliveryOf(info: Pick<TaskInfo, 'execution'>): string | null {
  return info.execution?.startedAt || null;
}

/**
 * The latest server refusal per card. The refused click rolls the optimistic
 * navigation back to the card, so the toolbar can show the refusal there.
 */
@Injectable({ providedIn: 'root' })
export class AcceptanceRefusalStore {
  private readonly readiness = inject(AcceptanceReadinessStore);
  /** Each refusal with the readiness sequence it was recorded at (F2). */
  private readonly refusals = signal<ReadonlyMap<string, { refusal: ServerAcceptanceRefusal; at: number }>>(new Map());
  private readonly focus = signal<{ taskKey: string; at: number } | null>(null);
  /**
   * AGT-3830: the card whose refusal reason should take focus once it renders.
   * Only the visible refusal takes it (the toolbar and the phone each render
   * one), and only shortly after the refused click.
   */
  readonly focusRequest = this.focus.asReadonly();
  /** An explicit action from a toast or banner opens the written-reason form. */
  readonly overrideRequest = signal<string | null>(null);

  requestFocus(taskKey: string): void {
    this.focus.set({ taskKey, at: Date.now() });
  }

  forTask(taskKey: string | null | undefined): ServerAcceptanceRefusal | null {
    return taskKey ? this.refusals().get(taskKey)?.refusal ?? null : null;
  }

  /**
   * F2: the recorded refusal of `info` while it still describes the card.
   * A round or a content review requested afterwards, a later readiness
   * check that answers `ready`, or a new delivery drops it; otherwise the
   * toolbar and the Accept tooltip kept the old reason until a reload.
   */
  forCard(info: Pick<TaskInfo, 'taskKey' | 'id' | 'execution'>): ServerAcceptanceRefusal | null {
    const entry = info.taskKey ? this.refusals().get(info.taskKey) : undefined;
    if (!entry) return null;
    const delivery = deliveryOf(info);
    if (entry.refusal.delivery && delivery && delivery !== entry.refusal.delivery) return null;
    return this.readiness.supersedes(info, entry.at) ? null : entry.refusal;
  }

  record(refusal: ServerAcceptanceRefusal): void {
    const at = this.readiness.sequence();
    this.refusals.update(current => new Map(current).set(refusal.taskKey, { refusal, at }));
  }

  requestOverride(taskKey: string): void {
    this.overrideRequest.set(taskKey);
  }

  /** Consumes the pending override request of `taskKey`; true when there was one. */
  takeOverrideRequest(taskKey: string): boolean {
    if (this.overrideRequest() !== taskKey) return false;
    this.overrideRequest.set(null);
    return true;
  }

  clear(taskKey: string): void {
    if (this.overrideRequest() === taskKey) this.overrideRequest.set(null);
    if (!this.refusals().has(taskKey)) return;
    this.refusals.update(current => {
      const next = new Map(current);
      next.delete(taskKey);
      return next;
    });
  }
}
