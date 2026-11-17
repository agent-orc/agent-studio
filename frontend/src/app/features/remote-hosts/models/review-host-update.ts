import type { RemoteHost } from './remote-host.model';
import { driftsFromStable, imageDigest, reviewImageComparison,
  type HostReleaseDriftSnapshot, type StableReleaseIdentity } from './host-release-drift';

export interface ReviewUpdateTarget {
  clientId: string;
  name: string;
  expectedCommit: string | null;
  expectedImageRef: string | null;
  /**
   * False when the role no longer drifts but is still draining (AGT-3852): the
   * panel then shows only for a switch the server recorded, so verification
   * and resume stay reachable after the new heartbeat removed the drift.
   */
  updatePending?: boolean;
}

export interface ReviewDrainStatus {
  draining: boolean;
  drained: boolean;
  running: readonly { kind: string; attemptId: string; taskKey: string }[];
  drainRequestedAt?: string | null;
  /** Server-recorded start of the deploy host switch (AGT-3852). */
  switchStartedAt?: string | null;
}

/** The review role, rather than the machine's coding role, owns this update. */
export function reviewUpdateTarget(
  roles: readonly RemoteHost[], stable: StableReleaseIdentity | null,
): ReviewUpdateTarget | null {
  const pending = (role: RemoteHost) => driftsFromStable(role.releaseDrift)
    || reviewImageComparison(role.release, role.releaseDrift)?.state === 'differs';
  const review = roles.find(role => role.serviceRole === 'review' && pending(role))
    ?? roles.find(role => role.serviceRole === 'review' && role.status === 'draining');
  if (!review) return null;
  const image = reviewImageComparison(review.release, review.releaseDrift);
  return { clientId: review.clientId, name: review.name,
    expectedCommit: stable?.commit ?? null,
    expectedImageRef: image?.expected ?? null,
    updatePending: pending(review) };
}

/** The server's lease authority is required to report no work before switching. */
export function reviewQuietPoint(status: ReviewDrainStatus): { quiet: boolean; reason: string | null } {
  const active = status.running.find(attempt => attempt.kind.toLowerCase() === 'review');
  if (active) return { quiet: false, reason: `Review ${active.taskKey || active.attemptId} is still active on this host.` };
  if (!status.draining) return { quiet: false, reason: 'The review host is not drained.' };
  if (!status.drained) return { quiet: false, reason: 'The host still holds an active lease.' };
  return { quiet: true, reason: null };
}

export type ReviewUpdateResumeStep = 'ready' | 'blocked' | 'quiet' | 'switching';

/**
 * The step a freshly loaded panel resumes at, from the server's drain status
 * alone (AGT-3852), so a reload and a second operator agree on it.
 */
export function reviewUpdateStepFromStatus(
  status: ReviewDrainStatus,
): { step: ReviewUpdateResumeStep; reason: string | null } {
  if (!status.draining) return { step: 'ready', reason: null };
  if (status.switchStartedAt) return { step: 'switching',
    reason: 'The host switch has started. Run the review host steps in the deploy procedure, then check for a new heartbeat.' };
  const point = reviewQuietPoint(status);
  return { step: point.quiet ? 'quiet' : 'blocked', reason: point.reason };
}

/** A successful deploy command is not proof; only a later host report is. */
export function reviewHeartbeatProvesUpdate(
  snapshot: HostReleaseDriftSnapshot, target: ReviewUpdateTarget, switchedAfter: string,
): boolean {
  const role = snapshot.hosts.find(item => item.runnerId === target.clientId && item.role === 'review');
  const seen = Date.parse(role?.lastSeenAt ?? '');
  if (!role || role.heartbeatStale || !Number.isFinite(seen) || seen <= Date.parse(switchedAfter)) return false;
  if (!target.expectedCommit || !target.expectedImageRef) return false;
  const expectedDigest = imageDigest(target.expectedImageRef);
  return !!expectedDigest && snapshot.stable.commit === target.expectedCommit
    && imageDigest(role.expectedImageRef) === expectedDigest
    && role.release?.commit === target.expectedCommit
    && imageDigest(role.release.imageRef) === expectedDigest;
}
