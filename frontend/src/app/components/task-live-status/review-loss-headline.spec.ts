import { describe, expect, it } from 'vitest';
import type { TaskLiveQueue } from '../../models/task.model';
import { reviewLossHeadline } from './review-loss-headline';

/** A fixed UTC clock keeps the expected sentences independent of the runner's time zone. */
const clock = (epochMs: number) => new Date(epochMs).toISOString().slice(11, 16);

describe('reviewLossHeadline (AGT-3690)', () => {
  it('reads the playthrough sentence for a review host restart with a scheduled retry', () => {
    const queue: TaskLiveQueue = {
      kind: 'review',
      state: 'retry-scheduled',
      lost: { cause: 'host-restarted', classification: 'ExecutorRestarted', hostId: 'lab-review-host',
        executorId: 'first-run-review', at: '2026-11-10T02:27:16Z' },
      retry: { number: 1, budget: 3, dueAt: '2026-11-10T02:28:16Z' },
    };

    expect(reviewLossHeadline(queue, clock)).toEqual({
      text: 'Review lost: review host restarted (lab-review-host, 02:27). Automatic retry 1 of 3 at 02:28.',
      before: 'Review lost: review host restarted (',
      host: 'lab-review-host',
      after: ', 02:27). Automatic retry 1 of 3 at 02:28.',
      hostRoute: '/workspace/settings/environment?host=first-run-review',
    });
  });

  it('names another infrastructure classification and links the host when no executor is known', () => {
    const headline = reviewLossHeadline({
      kind: 'review',
      state: 'retry-scheduled',
      lost: { cause: 'infrastructure', classification: 'SnapshotUnavailable', hostId: 'agent-runner-01',
        at: '2026-11-10T02:27:00Z' },
      retry: { number: 2, budget: 3, dueAt: '2026-11-10T02:30:00Z' },
    }, clock);

    expect(headline?.text).toBe(
      'Review lost: review infrastructure failed (SnapshotUnavailable) (agent-runner-01, 02:27). Automatic retry 2 of 3 at 02:30.');
    expect(headline?.hostRoute).toBe('/workspace/settings/environment?host=agent-runner-01');
  });

  it('says that no retry is scheduled when the budget is spent', () => {
    const headline = reviewLossHeadline({
      kind: 'review',
      state: 'stalled',
      lost: { cause: 'host-restarted', hostId: 'lab-review-host', at: '2026-11-10T02:40:00Z' },
    }, clock);

    expect(headline?.text).toBe(
      'Review lost: review host restarted (lab-review-host, 02:40). No automatic retry is scheduled · operator decision needed');
  });

  it('omits the parenthesis and the link when the loss names neither host nor time', () => {
    const headline = reviewLossHeadline({
      kind: 'review',
      state: 'retry-scheduled',
      lost: { cause: 'host-restarted' },
      retry: { budget: 3, dueAt: '2026-11-10T02:28:00Z' },
    }, clock);

    expect(headline?.text).toBe('Review lost: review host restarted. Automatic retry at 02:28.');
    expect(headline?.hostRoute).toBeNull();
  });

  it('returns null for a queue without a loss, so older payloads keep their wording', () => {
    expect(reviewLossHeadline({ kind: 'review', state: 'stalled' }, clock)).toBeNull();
    expect(reviewLossHeadline(null, clock)).toBeNull();
  });
});
