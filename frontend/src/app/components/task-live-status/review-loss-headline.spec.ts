import { describe, expect, it } from 'vitest';
import type { TaskLiveQueue } from '../../models/task.model';
import { reviewLossHeadline } from './review-loss-headline';

/** A fixed UTC clock keeps the expected sentences independent of the runner's time zone. */
const clock = (epochMs: number) => new Date(epochMs).toISOString().slice(11, 16);
/** Before every due time below, so the retry is still a promise. */
const beforeDue = Date.parse('2026-11-10T02:27:30Z');

describe('reviewLossHeadline (AGT-3690)', () => {
  it('reads the playthrough sentence for a review host restart with a scheduled retry', () => {
    const queue: TaskLiveQueue = {
      kind: 'review',
      state: 'retry-scheduled',
      lost: { cause: 'host-restarted', classification: 'ExecutorRestarted', hostId: 'lab-review-host',
        executorId: 'first-run-review', at: '2026-11-10T02:27:16Z' },
      retry: { number: 1, budget: 3, dueAt: '2026-11-10T02:28:16Z' },
    };

    expect(reviewLossHeadline(queue, clock, beforeDue)).toEqual({
      text: 'Review lost: review host restarted (lab-review-host, 02:27). Automatic retry 1 of 3 at 02:28.',
      before: 'Review lost: review host restarted (',
      host: 'lab-review-host',
      after: ', 02:27). Automatic retry 1 of 3 at 02:28.',
      hostRoute: '/workspace/settings/environment?host=first-run-review',
      overdue: false,
    });
  });

  it('names another infrastructure classification and links the host when no executor is known', () => {
    const headline = reviewLossHeadline({
      kind: 'review',
      state: 'retry-scheduled',
      lost: { cause: 'infrastructure', classification: 'SnapshotUnavailable', hostId: 'agent-runner-01',
        at: '2026-11-10T02:27:00Z' },
      retry: { number: 2, budget: 3, dueAt: '2026-11-10T02:30:00Z' },
    }, clock, beforeDue);

    expect(headline?.text).toBe(
      'Review lost: review infrastructure failed (SnapshotUnavailable) (agent-runner-01, 02:27). Automatic retry 2 of 3 at 02:30.');
    expect(headline?.hostRoute).toBe('/workspace/settings/environment?host=agent-runner-01');
  });

  it('says that no retry is scheduled when the budget is spent', () => {
    const headline = reviewLossHeadline({
      kind: 'review',
      state: 'stalled',
      lost: { cause: 'host-restarted', hostId: 'lab-review-host', at: '2026-11-10T02:40:00Z' },
    }, clock, beforeDue);

    expect(headline?.text).toBe(
      'Review lost: review host restarted (lab-review-host, 02:40). No automatic retry is scheduled · operator decision needed');
  });

  it('omits the parenthesis and the link when the loss names neither host nor time', () => {
    const headline = reviewLossHeadline({
      kind: 'review',
      state: 'retry-scheduled',
      lost: { cause: 'host-restarted' },
      retry: { budget: 3, dueAt: '2026-11-10T02:28:00Z' },
    }, clock, beforeDue);

    expect(headline?.text).toBe('Review lost: review host restarted. Automatic retry at 02:28.');
    expect(headline?.hostRoute).toBeNull();
  });

  it('returns null for a queue without a loss, so older payloads keep their wording', () => {
    expect(reviewLossHeadline({ kind: 'review', state: 'stalled' }, clock, beforeDue)).toBeNull();
    expect(reviewLossHeadline(null, clock, beforeDue)).toBeNull();
  });
});

describe('reviewLossHeadline past the retry due time (AGT-3848)', () => {
  // Playthrough AGT-3648 S6-F2: the retry was due at 23:37 and never started.
  const queue: TaskLiveQueue = {
    kind: 'review',
    state: 'retry-scheduled',
    lost: { cause: 'host-restarted', classification: 'ExecutorRestarted', hostId: 'lab-review-host',
      executorId: 'first-run-review', at: '2026-11-15T23:36:00Z' },
    retry: { number: 1, budget: 2, dueAt: '2026-11-15T23:37:00Z', parkAt: '2026-11-15T23:43:00Z' },
  };

  it('keeps the promise up to the due time and while the scheduler can still start it', () => {
    const headline = reviewLossHeadline(queue, clock, Date.parse('2026-11-15T23:37:30Z'))!;
    expect(headline.text).toBe(
      'Review lost: review host restarted (lab-review-host, 23:36). Automatic retry 1 of 2 at 23:37.');
    expect(headline.overdue).toBe(false);
  });

  it('says the retry is overdue since the due time and names when the watchdog parks the card', () => {
    const headline = reviewLossHeadline(queue, clock, Date.parse('2026-11-15T23:38:07Z'))!;
    expect(headline.text).toBe('Review lost: review host restarted (lab-review-host, 23:36). '
      + 'Automatic retry 1 of 2 overdue since 23:37 · parks in Escalated by 23:43.');
    expect(headline.text).not.toContain('at 23:37');
    expect(headline.after).toBe(', 23:36). Automatic retry 1 of 2 overdue since 23:37 · parks in Escalated by 23:43.');
    expect(headline.overdue).toBe(true);
  });

  it('promises nothing once the park time passed too', () => {
    const headline = reviewLossHeadline(queue, clock, Date.parse('2026-11-15T23:50:00Z'))!;
    expect(headline.text).toBe('Review lost: review host restarted (lab-review-host, 23:36). '
      + 'Automatic retry 1 of 2 overdue since 23:37 · not parked by 23:43 · operator decision needed');
    expect(headline.overdue).toBe(true);
  });

  it('asks for an operator decision when an older server sends no park time', () => {
    const headline = reviewLossHeadline({ ...queue, retry: { budget: 2, dueAt: '2026-11-15T23:37:00Z' } },
      clock, Date.parse('2026-11-15T23:38:00Z'))!;
    expect(headline.text).toBe('Review lost: review host restarted (lab-review-host, 23:36). '
      + 'Automatic retry overdue since 23:37 · operator decision needed');
  });
});
