import type { TaskInfo } from '../../models/task.model';
import type { BatchGateState } from '../../models/batch-gate.model';
import { clock, timestamp } from './task-live-status.util';

type PerTaskGate = NonNullable<BatchGateState['perTaskGates']>[number];

export function perTaskGateFor(state: BatchGateState | null | undefined, task: TaskInfo): PerTaskGate | undefined {
  return state?.perTaskGates?.find(gate =>
    gate.taskKey === task.taskKey || gate.taskKey === task.key || gate.taskKey === task.id);
}

/** Headline of a paused batch member waiting for, or running, its own per-task gate. */
export function perTaskGateView(perTask: PerTaskGate) {
  const started = timestamp(perTask.startedAtUtc);
  const handoff = perTask.batchId
    ? `batch ${perTask.batchId} paused: ${perTask.reason.replace(/^batch [^ ]+ paused:\s*/, '')}`
    : perTask.reason;
  return {
    tone: perTask.running ? 'active' as const : 'waiting' as const,
    headline: perTask.running
      ? `Per-task gate running on ${perTask.host || 'gate host'}${started === null ? '' : ` since ${clock(started)}`}`
      : `Per-task gate queued (${perTask.position} of ${perTask.total}) after ${handoff}`,
    detail: perTask.running ? 'Red escalates; green integrates.' : 'One full suite per member. Red escalates; green integrates.',
    next: [] as string[],
    perTaskGate: true,
  };
}
