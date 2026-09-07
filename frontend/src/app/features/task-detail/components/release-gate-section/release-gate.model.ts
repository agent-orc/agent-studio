import {
  TaskInfo,
  TaskReferenceLink,
  TaskState,
  WaitsOnItem,
} from '../../../../models/task.model';

/**
 * AGT-2709 — pure visibility rules for the release-gate affordance. Kept out
 * of the component so the "when may an operator release?" decision is testable
 * without a render harness, the same way `WaitsOnEvaluator` keeps the backend
 * gate pure.
 *
 * A `references.dependsOn` edge with `releaseGate: true` is fulfilled only once
 * its target is terminal AND carries the explicit `released` flag. Nothing in
 * the lifecycle sets that flag, so the operator needs an affordance on both
 * ends of the edge: on the target ("release this for its dependents") and on
 * the dependent ("release the thing I am waiting for", without navigating).
 */

/** Lanes whose content is final enough to be released for dependents. */
export function isReleasableLane(state: string | null | undefined): boolean {
  return state === TaskState.Completed || state === TaskState.Archive;
}

/**
 * The incoming `dependsOn` edges that gate on this task's explicit release —
 * i.e. exactly the dependents a release decision moves. Other relation kinds
 * and ungated edges are unaffected by the flag and are filtered out.
 */
export function releaseGatedDependents(
  dependents: readonly TaskReferenceLink[] | null | undefined,
): TaskReferenceLink[] {
  return (dependents ?? []).filter(
    link => link.releaseGate === true && link.kind === 'dependsOn',
  );
}

/**
 * True when the target-side action is worth offering: the task is terminal and
 * at least one dependent actually gates on its release. Offering it earlier
 * would approve content that does not exist yet; offering it without a gated
 * dependent would add a control that changes nothing.
 */
export function canOfferRelease(
  info: TaskInfo,
  dependents: readonly TaskReferenceLink[] | null | undefined,
): boolean {
  return isReleasableLane(info.state) && releaseGatedDependents(dependents).length > 0;
}

/**
 * The waits-on targets this task is held back by *only* for want of an explicit
 * release. `waitingForRelease` already means "terminal but not released", so the
 * lane check is the backend's; a resolved `targetJobId` is required because the
 * release call addresses the target by folder id plus watch path.
 */
export function releasableWaitsOnTargets(info: TaskInfo): WaitsOnItem[] {
  return (info.waitsOn?.items ?? []).filter(
    item => item.waitingForRelease === true && !!item.targetJobId,
  );
}

/**
 * Stable label for a dependent: its F33 key, falling back to the folder id for
 * the (pre-F33) keyless case. Deliberately key-only - the References section
 * already renders each dependent's title one row below, and repeating it here
 * would say the same thing twice.
 */
export function dependentKey(link: TaskReferenceLink): string {
  return link.sourceKey ?? link.sourceJobId;
}
