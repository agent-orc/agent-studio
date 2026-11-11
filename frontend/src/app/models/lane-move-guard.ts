import { HttpErrorResponse } from '@angular/common/http';
import { isReplacedCommit, type IntegrationStatusValue } from '../features/git';
import { decisionMoveRefusal } from './decision-card-presentation';
import { laneName } from './lane-presentation';
import { TaskState, type TaskInfo } from './task.model';

/**
 * AGT-3624: the board's view of the server's lane admission. The card menu
 * disables a move the server will refuse and names the reason, following the
 * AGT-2795 decision lane guard pattern. Server refusals that still arrive are
 * translated into the same plain wording; the raw payload stays available as
 * technical details.
 */
export interface LaneMoveRefusal {
  /** One plain sentence: why the move is not possible. */
  sentence: string;
  /** What the operator can do next. */
  nextAction: string;
}

/** Targets the backend `DeliveryLanePolicy` guards. */
const DELIVERY_GUARDED_TARGETS: ReadonlySet<string> = new Set([
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
]);

/**
 * Lanes whose cards carry an integration verdict on the board payload.
 * Mirrors backend `TaskIntegrationStatusService.DeliveredLanes`.
 */
const DELIVERED_LANES: ReadonlySet<string> = new Set([
  TaskState.AutoReview,
  TaskState.Escalated,
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
]);

const READ_ONLY_MODES: ReadonlySet<string> = new Set(['planning', 'research', 'concept']);
const NO_BRANCH_TASK_TYPES: ReadonlySet<string> = new Set(['concept', 'decision']);

/** Effective delivery commits: replaced commits no longer count. */
function deliveryCommits(job: TaskInfo): number {
  const commits = job.commits?.length ? job.commits : job.commit ? [job.commit] : [];
  return commits.filter((commit) => !isReplacedCommit(commit)).length;
}

/**
 * Whether the card's delivery must be integrated. Mirrors backend
 * `AcceptanceIntegrationPolicy.IsIntegrationRequired`: delivery evidence
 * outranks the declared class.
 */
export function isIntegrationRequired(job: TaskInfo): boolean {
  if (deliveryCommits(job) > 0) return true;
  if (job.requiresIntegration != null) return job.requiresIntegration;
  if (job.noBranchExpected || READ_ONLY_MODES.has((job.mode ?? 'coding').toLowerCase())
    || job.kind === 'decision') return false;
  return !NO_BRANCH_TASK_TYPES.has((job.taskType ?? '').toLowerCase());
}

function integrationBranch(job: TaskInfo): string {
  return job.integration?.integrationBranch || 'develop';
}

function integrationRefusal(
  job: TaskInfo,
  targetState: string,
  status: IntegrationStatusValue | null,
): LaneMoveRefusal {
  const branch = integrationBranch(job);
  if (status === 'conflict-skipped') {
    return {
      sentence: `Integrating this card's work into ${branch} failed.`,
      nextAction: 'Open the card and resolve the integration failure, then try again.',
    };
  }
  if (status === 'no-branch') {
    return {
      sentence: 'The code this card was expected to deliver cannot be found.',
      nextAction: 'Open the card to recover its delivery, or delete the card if no code was needed.',
    };
  }
  if (status === 'merged-locally') {
    return {
      sentence: `This card's work is merged locally but not published to ${branch} yet.`,
      nextAction: `Publish ${branch}, then try again.`,
    };
  }
  if (status === null) {
    return {
      sentence: 'This card has not delivered its work yet.',
      nextAction: targetState === TaskState.Archive
        ? 'Run it first, or delete the card if it is no longer needed.'
        : `It can move to ${laneName(targetState)} after its run delivers.`,
    };
  }
  return {
    sentence: `This card's work is not in ${branch} yet.`,
    nextAction: targetState === TaskState.Archive
      ? 'Integrate its work first, or delete the card if the work is no longer wanted.'
      : `Wait for integration to finish, then move it to ${laneName(targetState)}.`,
  };
}

/**
 * Why the backend `DeliveryLanePolicy` would refuse moving `job` to
 * `targetState`, or null. A delivered card without a loaded verdict is left
 * to the server: an unknown verdict is not a refusal.
 */
export function deliveryMoveRefusal(job: TaskInfo, targetState: string): LaneMoveRefusal | null {
  if (!DELIVERY_GUARDED_TARGETS.has(targetState)) return null;
  const status = job.integration?.status ?? null;
  if (status === 'integrated' || status === 'not-applicable') return null;
  const source = job.state === '2-ready-intake' ? TaskState.Ready : job.state;
  if (DELIVERED_LANES.has(source)) {
    return status === null ? null : integrationRefusal(job, targetState, status);
  }
  // Outside the delivered lanes the server computes no verdict, so a card
  // whose work must integrate is refused.
  return isIntegrationRequired(job) ? integrationRefusal(job, targetState, null) : null;
}

/**
 * Every lane guard the board can predict for a manual move: the decision
 * lane guard first (AGT-2795), then the delivery lane guard.
 */
export function laneMoveRefusal(job: TaskInfo, targetState: string): LaneMoveRefusal | null {
  const decision = decisionMoveRefusal(job, targetState);
  if (decision) {
    return { sentence: decision, nextAction: 'Open the decision card to choose an option.' };
  }
  return deliveryMoveRefusal(job, targetState);
}

/** Menu tooltip for a refused move: the reason and the next step. */
export function laneMoveRefusalText(refusal: LaneMoveRefusal): string {
  return `${refusal.sentence} ${refusal.nextAction}`;
}

const SERVER_CATEGORY_STATUS: Readonly<Record<string, IntegrationStatusValue>> = {
  'integration-pending': 'pending',
  'missing-delivery': 'no-branch',
  'integration-conflict': 'conflict-skipped',
};

function payloadField(error: HttpErrorResponse, field: string): string | null {
  const payload = error.error as Record<string, unknown> | string | null;
  if (payload && typeof payload === 'object' && typeof payload[field] === 'string') {
    return (payload[field] as string).trim() || null;
  }
  return field === 'error' && typeof payload === 'string' ? payload.trim() || null : null;
}

/**
 * Plain wording for a lane move the server refused as an expected outcome
 * (HTTP 409), or null for a genuine failure that keeps the error dialog.
 */
export function describeMoveRefusal(
  error: unknown,
  job: TaskInfo,
  targetState: string,
): LaneMoveRefusal | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) return null;
  const code = payloadField(error, 'code');
  const message = payloadField(error, 'error') ?? '';
  if (code === 'completion-content-gap' || message.startsWith('Completion refused:')) {
    return {
      sentence: "This card's review does not cover its current brief, so it cannot be accepted yet.",
      nextAction: 'Open the card and rerun its review, or accept it there with a written reason.',
    };
  }
  if (code === 'run-attempt-missing') {
    return {
      sentence: 'The run that produced this delivery is no longer on record, so it cannot be accepted.',
      nextAction: 'Move the card to Ready so a fresh run delivers it again.',
    };
  }
  if (code === 'batch-gate-evidence-missing') {
    return {
      sentence: "This card's work has not passed a published gate yet.",
      nextAction: 'Wait for the next batch gate, then try again.',
    };
  }
  const category = /^([a-z-]+):/.exec(message)?.[1];
  if (category && category in SERVER_CATEGORY_STATUS) {
    return integrationRefusal(job, targetState, SERVER_CATEGORY_STATUS[category]);
  }
  if (message.startsWith('The accepted delivery changed')) {
    return {
      sentence: 'The delivery changed after it was accepted.',
      nextAction: 'Review and accept the current delivery before archiving it.',
    };
  }
  const predicted = laneMoveRefusal(job, targetState);
  if (predicted) return predicted;
  return {
    sentence: `This card cannot move to ${laneName(targetState)} right now.`,
    nextAction: 'Open the card to see what it needs, or refresh the board and try again.',
  };
}

/**
 * AGT-3624: what deleting `job` loses when its delivery is not known to be
 * integrated, or null when nothing undelivered is at stake.
 */
export function unintegratedDeliveryLoss(job: TaskInfo): string | null {
  const status = job.integration?.status ?? null;
  if (status === 'integrated' || status === 'not-applicable' || status === 'no-branch') return null;
  const commits = deliveryCommits(job);
  const ref = job.integration?.deliveryRef ?? null;
  if (commits === 0 && !ref) return null;
  const branch = integrationBranch(job);
  const work = [
    commits > 0 ? `${commits} commit${commits === 1 ? '' : 's'}` : null,
    ref ? `on ${ref}` : null,
  ].filter(Boolean).join(' ');
  const state = status === null ? 'has not been confirmed as integrated into' : 'is not integrated into';
  return `This card's delivered work (${work}) ${state} ${branch}. `
    + 'Deleting the card removes its prompt, run logs, review results and the only record of that delivery, '
    + 'so nothing will integrate it later.';
}
