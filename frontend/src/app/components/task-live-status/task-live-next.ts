import type { TaskInfo, TaskLiveStatus } from '../../models/task.model';
import { TaskState } from '../../models/task.model';
import type { BatchMemberState } from '../../models/batch-gate.model';
import { isTaskRunActive } from '../../services/run-activity.util';

export interface CauseHold {
  causeKey: string;
  summary: string;
}

/** NEXT follows the lane and hold facts that choose CURRENT. A completed
 * attempt can have no pre-step records, so its pipeline preview is stale. */
export function nextForTask(task: TaskInfo, status: TaskLiveStatus, causeHeld: CauseHold | null,
  batch: string | null): string[] {
  if (status.activeStep || task.state === TaskState.Progress && isTaskRunActive(task)) {
    return status.nextSteps.slice(0, 3).map(step => step.displayName);
  }
  if (causeHeld) return nextForHold(causeHeld);
  if (task.waitsOn?.blocked) return ['Dependency resolution', 'Runner pickup'];
  if (task.pickupHold) return ['Resolve pickup hold', 'Runner pickup'];
  const wait = postProcessingWait(task, status.queue, batch);
  if (wait) return [wait.action];
  if (task.state === TaskState.Ready) {
    return ['Runner pickup', ...status.nextSteps.slice(0, 2).map(step => step.displayName)];
  }
  if (task.state === TaskState.HumanReview || task.state === TaskState.Escalated) {
    return [humanReviewAction(task)];
  }
  return status.nextSteps.slice(0, 3).map(step => step.displayName);
}

export type PostProcessingWaitKind = 'reviewing' | 'stalled' | 'batch' | 'workflow' | 'review';

export interface PostProcessingWait {
  kind: PostProcessingWaitKind;
  action: string;
}

/** The one place that decides which review, operator, batch or workflow wait
 * a card is in. CURRENT and NEXT both read it so their answers agree. */
export function postProcessingWait(task: TaskInfo, queue: TaskLiveStatus['queue'],
  batch: string | null): PostProcessingWait | null {
  const autoReview = task.state === TaskState.AutoReview;
  if (autoReview && queue?.state === 'reviewing') return { kind: 'reviewing', action: 'Review verdict' };
  if (autoReview && queue?.state === 'stalled') return { kind: 'stalled', action: 'Operator decision' };
  if (batch) return { kind: 'batch', action: batch };
  if (!autoReview) return null;
  const workflow = postProcessingAction(queue?.reason);
  return workflow ? { kind: 'workflow', action: workflow } : { kind: 'review', action: 'Review' };
}

export function batchAction(member: BatchMemberState | null): string | null {
  if (!member) return null;
  switch (member.status) {
    case 'pending': return 'Batch formation';
    case 'in-batch': return 'Batch verdict';
    case 'published': return 'Delivery integration';
    case 'fallback': return member.waitKind === 'integration' ? 'Delivery integration' : 'Per-task gate';
    case 'pre-check-red':
    case 'review-fix': return 'Review fix';
  }
}

export function nextForHold(hold: CauseHold): string[] {
  return [`Resolve ${hold.causeKey}`, 'Review'];
}

export function postProcessingAction(reason: string | null | undefined): string | null {
  const wait = reason?.toLowerCase() ?? '';
  if (wait.includes('operator action')) return 'Operator decision';
  if (wait.includes('integration')) return 'Delivery integration';
  if (wait.includes('batch') || wait.includes('gate')) return 'Batch verdict';
  return null;
}

export function humanReviewAction(task: TaskInfo): string {
  if (task.phase === 'integrating' || task.integration?.status === 'pending'
    || task.integration?.status === 'conflict-skipped') return 'Delivery integration';
  return 'Owner decision';
}
