import type { TaskInfo } from '../../../models/task.model';

export type QueueEdge = 'top' | 'bottom';

export interface QueueKey {
  jobId: string;
  watchPath: string;
}

/** Orders before and after moving one card to an edge of its project's Ready queue. */
export interface ReadyQueueReorder {
  /** The card's project queue before the move (the undo replay). */
  prevQueue: QueueKey[];
  /** The card's project queue after the move (the reorder request). */
  nextQueue: QueueKey[];
  /** The whole painted lane before the move. */
  prevLane: QueueKey[];
  /** The whole painted lane after the move; other projects keep their slots. */
  nextLane: QueueKey[];
}

function key(job: TaskInfo): QueueKey {
  return { jobId: job.id, watchPath: job.watchPath };
}

function same(a: TaskInfo, b: Pick<TaskInfo, 'id' | 'watchPath'>): boolean {
  return a.id === b.id && a.watchPath === b.watchPath;
}

/**
 * AGT-3588: pickup order is per project, so Move to top / Move to bottom
 * reorder the card inside its own project's queue while cards of other
 * projects in a mixed lane stay where they are. Top mirrors the backend's
 * `PromoteToReadyTop`: cards carrying a pending intent keep the head.
 * Returns null when the card is not in the lane or already at that edge.
 */
export function readyQueueReorder(
  lane: readonly TaskInfo[],
  job: Pick<TaskInfo, 'id' | 'watchPath'>,
  edge: QueueEdge,
): ReadyQueueReorder | null {
  const queue = lane.filter((j) => j.watchPath === job.watchPath);
  const moving = queue.find((j) => same(j, job));
  if (!moving) return null;
  const others = queue.filter((j) => j !== moving);
  const next = edge === 'top'
    ? [...others.filter((j) => j.pendingIntent), moving, ...others.filter((j) => !j.pendingIntent)]
    : [...others, moving];
  if (next.every((j, i) => j === queue[i])) return null;
  let slot = 0;
  const nextLane = lane.map((j) => (j.watchPath === job.watchPath ? next[slot++] : j));
  return {
    prevQueue: queue.map(key),
    nextQueue: next.map(key),
    prevLane: lane.map(key),
    nextLane: nextLane.map(key),
  };
}
