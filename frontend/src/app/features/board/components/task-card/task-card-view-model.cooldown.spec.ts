import { describe, expect, it } from 'vitest';
import { INFRA_RETRY_BUDGET, buildCooldownRetryBanner } from './task-card-view-model';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, TaskRunActivity } from '../../../../models/task.model';

/**
 * DtC step 6 — the CooldownRetry banner marks a 3-progress card that infra-crashed
 * and is holding out its scheduled re-pickup backoff (the `runActivity.failed-backoff`
 * state). It must read distinctly from a live "Running live" run, so the builder
 * only fires for the failed-backoff kind and reports `retrying k/3 · in Ns`.
 *
 * AGT-2703 moved the "is the backoff still holding?" comparison into the client,
 * so the builder now reads `backoffUntil` against the card's clock: a deadline in
 * the future is a cooldown, an elapsed one is not a cooldown at all.
 */
function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    title: 'Task 1',
    state: TaskState.Progress,
    order: 1,
    agent: 'claude',
    createdAt: '2026-07-09T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-07-09T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  } as TaskInfo;
}

function activity(overrides: Partial<TaskRunActivity> = {}): TaskRunActivity {
  // The backend sends the post-backoff kind plus the deadline; a cooldown is
  // therefore a card whose deadline is still ahead of the card's clock.
  return { kind: 'failed-idle', attempt: 1, backoffUntil: new Date(NOW + 120_000).toISOString(), ...overrides };
}

const NOW = Date.parse('2026-07-11T12:00:00Z');

describe('buildCooldownRetryBanner (DtC step 6)', () => {
  it('renders "infra-crashed · retrying k/3" with a live seconds countdown', () => {
    const backoffUntil = new Date(NOW + 210_000).toISOString();
    const banner = buildCooldownRetryBanner(
      makeJob({ runActivity: activity({ attempt: 2, backoffUntil, lastError: 'exit -1' }) }),
      NOW,
    );
    expect(banner).not.toBeNull();
    expect(banner!.attempt).toBe(2);
    expect(banner!.budget).toBe(INFRA_RETRY_BUDGET);
    expect(banner!.label).toBe('infra-crashed · retrying 2/3');
    expect(banner!.secondsLeft).toBe(210);
    expect(banner!.countdown).toBe('in 210s');
    expect(banner!.tooltip).toContain('exit -1');
    expect(banner!.tooltip).toContain('CooldownRetry');
  });

  it('stops being a cooldown once the backoff timer has elapsed', () => {
    // The card's own clock retires the banner on the next tick; it no longer
    // waits for a poll to reclassify the card server-side.
    const backoffUntil = new Date(NOW - 5_000).toISOString();
    const banner = buildCooldownRetryBanner(makeJob({ runActivity: activity({ backoffUntil }) }), NOW);
    expect(banner).toBeNull();
  });

  it('does NOT fire for a failed card with no scheduled re-pickup at all', () => {
    const banner = buildCooldownRetryBanner(
      makeJob({ runActivity: activity({ backoffUntil: null }) }), NOW);
    expect(banner).toBeNull();
  });

  it('clamps the attempt into [1, budget] so the k/3 never overflows', () => {
    const hi = buildCooldownRetryBanner(makeJob({ runActivity: activity({ attempt: 9 }) }), NOW);
    expect(hi!.attempt).toBe(INFRA_RETRY_BUDGET);
    expect(hi!.label).toBe('infra-crashed · retrying 3/3');
    const lo = buildCooldownRetryBanner(makeJob({ runActivity: activity({ attempt: 0 }) }), NOW);
    expect(lo!.attempt).toBe(1);
  });

  it('does NOT fire for a live run, an idle run, or off the Progress lane', () => {
    // A live run never carries a deadline, so a stale one must not resurrect the
    // banner underneath it.
    expect(buildCooldownRetryBanner(makeJob({ runActivity: activity({ kind: 'active' }) }), NOW)).toBeNull();
    expect(buildCooldownRetryBanner(
      makeJob({ runActivity: activity({ kind: 'failed-idle', backoffUntil: null }) }), NOW)).toBeNull();
    expect(buildCooldownRetryBanner(makeJob({ runActivity: null }), NOW)).toBeNull();
    expect(
      buildCooldownRetryBanner(
        makeJob({ state: TaskState.HumanReview, runActivity: activity() }),
        NOW,
      ),
    ).toBeNull();
  });
});
