import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, TaskReferenceLink } from '../../../../models/task.model';
import type { ReleaseGateTarget } from './release-gate-action.component';

/** A dependsOn target is satisfied once it reaches completed or archive. */
export function isTerminalState(state: string): boolean {
  return state === TaskState.Completed || state === TaskState.Archive;
}

/**
 * AGT-2709 target side: the incoming dependents whose `dependsOn` edge opts
 * into `releaseGate`. Their cards stay blocked after this task completes until
 * the explicit flag is set, so they are exactly the audience of the decision.
 */
export function releaseGatedDependents(links: readonly TaskReferenceLink[]): TaskReferenceLink[] {
  return links.filter(link => link.releaseGate === true);
}

export function dependentKey(link: TaskReferenceLink): string {
  return link.sourceKey ?? link.sourceJobId;
}

/**
 * The release control for the task itself. Null before the task is terminal
 * (there is nothing to release yet) and null when nothing gates on it, so the
 * row never offers a no-op flag.
 */
export function ownReleaseTarget(
  info: TaskInfo,
  gatedDependents: readonly TaskReferenceLink[],
): ReleaseGateTarget | null {
  if (!isTerminalState(info.state) || gatedDependents.length === 0) return null;
  return {
    jobId: info.id,
    watchPath: info.watchPath,
    key: info.key ?? info.id,
    released: info.released === true,
  };
}

/**
 * AGT-2709 dependent side: the release-gated targets this task waits on that
 * are terminal but not released, keyed by upper-case key. The backend waits-on
 * overlay already resolved each target's job id and watch path (cross-project
 * and archive-inclusive), so the operator can release from the dependent card
 * instead of navigating to the target.
 */
export function pendingReleaseTargets(info: TaskInfo): Map<string, ReleaseGateTarget> {
  const map = new Map<string, ReleaseGateTarget>();
  for (const item of info.waitsOn?.items ?? []) {
    if (!item.waitingForRelease || !item.targetJobId) continue;
    map.set(item.key.trim().toUpperCase(), {
      jobId: item.targetJobId,
      watchPath: item.targetWatchPath ?? undefined,
      key: item.key,
      released: item.targetReleased === true,
    });
  }
  return map;
}
