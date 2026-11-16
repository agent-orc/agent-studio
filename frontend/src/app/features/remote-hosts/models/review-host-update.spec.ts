import { describe, expect, it } from 'vitest';
import { reviewUpdateTarget, reviewQuietPoint, reviewHeartbeatProvesUpdate, reviewUpdateStepFromStatus } from './review-host-update';
import type { RemoteHost } from './remote-host.model';
import type { HostReleaseDriftSnapshot } from './host-release-drift';

const oldImage = `registry.example/review@sha256:${'a'.repeat(64)}`;
const expectedImage = `registry.example/review@sha256:${'b'.repeat(64)}`;
const review = {
  id: 'review-1', clientId: 'review-1', name: 'Review one', role: 'remote', serviceRole: 'review',
  status: 'online', os: 'Linux', address: null, uptimeLabel: null,
  lastHeartbeatAt: '2026-11-14T10:00:00Z', capabilities: [],
  cliQuotas: [], stats: null, release: { releaseId: 'old', version: '0.9.5',
    commit: 'a'.repeat(40), imageRef: oldImage },
  releaseDrift: { runnerId: 'review-1', name: 'Review one', hostId: 'machine-1', role: 'review',
    release: { releaseId: 'old', version: '0.9.5', commit: 'a'.repeat(40), imageRef: oldImage },
    state: 'differs', behindByHours: null, behindForHours: null, alarmDue: false, reason: 'Different commit',
    lastSeenAt: '2026-11-14T10:00:00Z', heartbeatStale: false, expectedImageRef: expectedImage },
} as RemoteHost;
const stable = { version: '0.9.5', commit: 'b'.repeat(40) };

describe('review host update proof', () => {
  it('offers a same-version different-commit review role with its expected digest', () => {
    const target = reviewUpdateTarget([review], stable);
    expect(target).toMatchObject({ clientId: 'review-1', expectedCommit: stable.commit,
      expectedImageRef: expectedImage });
  });

  it('also offers a stale image when the commit already matches', () => {
    const sameCommit = { ...review, release: { ...review.release!, commit: stable.commit },
      releaseDrift: { ...review.releaseDrift!, state: 'current' as const } };
    expect(reviewUpdateTarget([sameCommit], stable)?.expectedImageRef).toBe(expectedImage);
  });

  it('marks a still draining review role without drift as not pending', () => {
    const updated = { ...review, status: 'draining' as const,
      release: { ...review.release!, commit: stable.commit, imageRef: expectedImage },
      releaseDrift: { ...review.releaseDrift!, state: 'current' as const,
        release: { ...review.releaseDrift!.release!, commit: stable.commit, imageRef: expectedImage } } };
    expect(reviewUpdateTarget([updated], stable)).toMatchObject({ clientId: 'review-1', updatePending: false });
    expect(reviewUpdateTarget([{ ...updated, status: 'online' as const }], stable)).toBeNull();
    expect(reviewUpdateTarget([review], stable)?.updatePending).toBe(true);
  });

  it('derives the resume step from the server drain status and recorded switch start', () => {
    expect(reviewUpdateStepFromStatus({ draining: false, drained: false, running: [] }).step).toBe('ready');
    expect(reviewUpdateStepFromStatus({ draining: true, drained: false,
      running: [{ kind: 'review', attemptId: 'a1', taskKey: 'AGT-1' }] }).step).toBe('blocked');
    expect(reviewUpdateStepFromStatus({ draining: true, drained: true, running: [] }).step).toBe('quiet');
    expect(reviewUpdateStepFromStatus({ draining: true, drained: true, running: [],
      switchStartedAt: '2026-11-15T09:05:00Z' }).step).toBe('switching');
  });

  it('refuses the quiet point while a review is active', () => {
    expect(reviewQuietPoint({ draining: true, drained: false,
      running: [{ kind: 'review', attemptId: 'a1', taskKey: 'AGT-1' }] })).toEqual({
        quiet: false, reason: 'Review AGT-1 is still active on this host.' });
  });

  it('requires a fresh heartbeat with both the exact commit and expected digest', () => {
    const target = reviewUpdateTarget([review], stable)!;
    const entry = { ...review.releaseDrift!, lastSeenAt: '2026-11-14T10:05:00Z', heartbeatStale: false,
      state: 'current' as const, release: { ...review.release!, commit: stable.commit, imageRef: expectedImage } };
    const snapshot: HostReleaseDriftSnapshot = { observedAt: '2026-11-14T10:05:01Z', stable,
      behindCount: 0, hosts: [entry] };
    expect(reviewHeartbeatProvesUpdate(snapshot, target, '2026-11-14T10:04:00Z')).toBe(true);
    expect(reviewHeartbeatProvesUpdate(snapshot, target, '2026-11-14T10:05:00Z')).toBe(false);
    expect(reviewHeartbeatProvesUpdate({ ...snapshot, hosts: [{ ...entry, release: { ...entry.release!, imageRef: oldImage } }] },
      target, '2026-11-14T10:04:00Z')).toBe(false);
    expect(reviewHeartbeatProvesUpdate({ ...snapshot, hosts: [{ ...entry, heartbeatStale: true }] },
      target, '2026-11-14T10:04:00Z')).toBe(false);
    expect(reviewHeartbeatProvesUpdate({ ...snapshot, stable: { ...stable, commit: 'c'.repeat(40) } },
      target, '2026-11-14T10:04:00Z')).toBe(false);
    expect(reviewHeartbeatProvesUpdate({ ...snapshot, hosts: [{ ...entry, expectedImageRef: oldImage }] },
      target, '2026-11-14T10:04:00Z')).toBe(false);
    expect(reviewHeartbeatProvesUpdate({ ...snapshot, hosts: [{ ...entry,
      release: { ...entry.release!, imageRef: 'bad-image-ref' } }] },
      { ...target, expectedImageRef: 'also-bad-image-ref' }, '2026-11-14T10:04:00Z')).toBe(false);
  });
});
