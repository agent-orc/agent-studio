import { describe, expect, it } from 'vitest';
import {
  buildRunActivityBadge,
  deriveStalledTaskState,
  freshestRunInfo,
  isTaskRunActive,
  STALLED_IDLE_THRESHOLD_MS,
} from './run-activity.util';
import { TaskState } from '../models/task.model';
import type { TaskInfo, TaskRunActivity } from '../models/task.model';

/**
 * ASS-1751: the run-activity pill makes a 3-progress card self-explanatory. The
 * three states that otherwise all look "untouched" must each render a distinct,
 * quiet pill:
 *   (a) failed + rapid-crash backoff  → "failed · Backoff bis HH:MM" with the time,
 *   (b) orphan / no active run        → "kein aktiver Run",
 *   (c) active run                    → "Run aktiv" (+ PID in the tooltip).
 * Plus a failed-idle variant for a failed run with no backoff. These tests pin
 * the label, tone, kind and tooltip for each, and the lane/absence guards.
 */
const NOW = Date.parse('2026-06-10T12:00:00Z');

function makeJob(runActivity: TaskRunActivity | null, overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ATP-1',
    title: 'Task 1',
    state: TaskState.Progress,
    order: 1,
    agent: 'codex',
    createdAt: '2026-06-10T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-06-10T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    runActivity,
    ...overrides,
  } as TaskInfo;
}

describe('buildRunActivityBadge — 3-progress run states (ASS-1751)', () => {
  it('returns null off the Progress lane even when runActivity is present', () => {
    const job = makeJob({ kind: 'active', processId: 10, attempt: 0 }, { state: TaskState.Ready });
    expect(buildRunActivityBadge(job, NOW)).toBeNull();
  });

  it('returns null when the backend attached no runActivity', () => {
    expect(buildRunActivityBadge(makeJob(null), NOW)).toBeNull();
    expect(buildRunActivityBadge(makeJob(undefined as unknown as null), NOW)).toBeNull();
  });

  describe('(c) active run', () => {
    it('shows restart adoption instead of an orphaned-run label', () => {
      const badge = buildRunActivityBadge(makeJob({
        kind: 'active',
        processId: 4242,
        attempt: 0,
        continuingAfterRestart: true,
      }), NOW);

      expect(badge).toMatchObject({
        kind: 'active',
        label: 'continuing after restart',
        tone: 'active',
      });
      expect(badge?.tooltip.title).toBe('Continuing after restart');
    });

    it('shows "Run aktiv" with the PID in the tooltip', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'active', processId: 4242, attempt: 0 }), NOW);
      expect(badge).not.toBeNull();
      expect(badge!.kind).toBe('active');
      expect(badge!.tone).toBe('active');
      expect(badge!.label).toBe('Run aktiv');
      expect(badge!.tooltip.body).toContain('4242');
    });

    it('omits the PID line when no live pid is known', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'active', processId: 0, attempt: 0 }), NOW);
      expect(badge!.tooltip.body).not.toContain('PID');
    });

    it('treats a live pre-step as active when the runner classification is stale', () => {
      const job = makeJob(
        { kind: 'failed-idle', attempt: 1, lastError: 'stale failure' },
        {
          execution: {
            jobId: 'task-1',
            taskKey: 'test::task-1',
            processId: 0,
            startedAt: new Date(NOW - 60_000).toISOString(),
            status: 'failed',
            exitCode: 1,
            durationSeconds: 1,
            model: 'gpt-5',
          },
          runner: null,
          executionLocation: null,
          liveStatus: {
            attempt: 1,
            activeStep: {
              stepId: 'pre-worktree-create',
              displayName: 'Create worktree',
              kind: 'pre',
              startedAt: new Date(NOW - 10_000).toISOString(),
            },
            nextSteps: [{ stepId: 'core', displayName: 'Agent execution' }],
          },
        },
      );

      expect(buildRunActivityBadge(job, NOW)).toMatchObject({
        kind: 'active',
        tone: 'active',
        label: 'Run aktiv',
      });
      expect(deriveStalledTaskState(job, NOW)).toBeNull();
    });

    it('keeps a between-steps run active while its execution is still running', () => {
      const job = makeJob(
        { kind: 'failed-idle', attempt: 1, lastError: 'stale failure' },
        {
          execution: {
            jobId: 'task-1',
            taskKey: 'test::task-1',
            processId: 4242,
            startedAt: new Date(NOW - 30_000).toISOString(),
            status: 'running',
            exitCode: null,
            durationSeconds: null,
            model: 'gpt-5',
          },
          liveStatus: {
            attempt: 2,
            activeStep: null,
            nextSteps: [{ stepId: 'post-tests', displayName: 'Tests' }],
            latestEventAt: new Date(NOW - 1_000).toISOString(),
          },
        },
      );

      const badge = buildRunActivityBadge(job, NOW);
      expect(badge).toMatchObject({ kind: 'active', tone: 'active', label: 'Run aktiv' });
      expect(badge!.tooltip.body).toContain('4242');
      expect(deriveStalledTaskState(job, NOW)).toBeNull();
    });
  });

  describe('(a) failed + backoff', () => {
    it('shows the retry clock when the backoff is in the future', () => {
      const backoffUntil = new Date(NOW + 90_000).toISOString(); // +90s
      const badge = buildRunActivityBadge(
        makeJob({ kind: 'failed-backoff', backoffUntil, attempt: 2, lastError: 'git push rejected' }),
        NOW,
      );
      expect(badge!.kind).toBe('failed-backoff');
      expect(badge!.tone).toBe('failed');
      const clock = new Date(backoffUntil).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
      expect(badge!.label).toBe(`failed · Backoff bis ${clock}`);
      expect(badge!.tooltip.body).toContain('Versuch:');
      expect(badge!.tooltip.body).toContain('git push rejected');
    });

    it('falls back to "wartet auf Reissue" when the backoff has already elapsed', () => {
      const backoffUntil = new Date(NOW - 5_000).toISOString();
      const badge = buildRunActivityBadge(
        makeJob({ kind: 'failed-backoff', backoffUntil, attempt: 3 }),
        NOW,
      );
      expect(badge!.label).toBe('failed · wartet auf Reissue');
    });

    it('escapes HTML in the last-error tooltip line', () => {
      const backoffUntil = new Date(NOW + 60_000).toISOString();
      const badge = buildRunActivityBadge(
        makeJob({ kind: 'failed-backoff', backoffUntil, attempt: 1, lastError: '<img src=x onerror=alert(1)>' }),
        NOW,
      );
      expect(badge!.tooltip.body).toContain('&lt;img');
      expect(badge!.tooltip.body).not.toContain('<img');
    });
  });

  describe('failed + idle (no backoff)', () => {
    it('shows "failed · kein aktiver Run"', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'failed-idle', attempt: 1, lastError: 'missing sentinel' }), NOW);
      expect(badge!.kind).toBe('failed-idle');
      expect(badge!.tone).toBe('failed');
      expect(badge!.label).toBe('failed · kein aktiver Run');
      expect(badge!.tooltip.body).toContain('missing sentinel');
    });
  });

  describe('(b) orphan / no active run', () => {
    it('shows the muted "kein aktiver Run" pill', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'no-active-run', attempt: 0 }), NOW);
      expect(badge!.kind).toBe('no-active-run');
      expect(badge!.tone).toBe('idle');
      expect(badge!.label).toBe('kein aktiver Run');
      expect(badge!.tooltip.body).toContain('Backend-Neustart');
    });

    it('omits the attempt line when there is no recorded failure streak', () => {
      const badge = buildRunActivityBadge(makeJob({ kind: 'no-active-run', attempt: 0 }), NOW);
      expect(badge!.tooltip.body).not.toContain('Versuch:');
    });

    it('does not infer an active run from upcoming steps alone', () => {
      const badge = buildRunActivityBadge(
        makeJob(
          { kind: 'no-active-run', attempt: 0 },
          {
            liveStatus: {
              attempt: 1,
              activeStep: null,
              nextSteps: [{ stepId: 'core', displayName: 'Agent execution' }],
              queue: null,
              latestEventAt: new Date(NOW - 60_000).toISOString(),
            },
          },
        ),
        NOW,
      );

      expect(badge).toMatchObject({ kind: 'no-active-run', tone: 'idle', label: 'kein aktiver Run' });
    });
  });
});

describe('deriveStalledTaskState', () => {
  it('keeps a freshly started Progress card healthy during the idle grace period', () => {
    const job = makeJob(
      { kind: 'no-active-run', attempt: 0 },
      { enteredLaneAt: new Date(NOW - STALLED_IDLE_THRESHOLD_MS).toISOString() },
    );

    expect(deriveStalledTaskState(job, NOW)).toBeNull();
  });

  it('marks a no-active-run card stalled only after the idle threshold', () => {
    const job = makeJob(
      { kind: 'no-active-run', attempt: 0 },
      { enteredLaneAt: new Date(NOW - STALLED_IDLE_THRESHOLD_MS - 1).toISOString(), lastActivity: '' },
    );

    expect(deriveStalledTaskState(job, NOW)).toMatchObject({ reason: 'idle', label: 'Stalled' });
  });

  it('marks an explicitly failed idle run stalled immediately', () => {
    const job = makeJob(
      { kind: 'failed-idle', attempt: 1, lastError: 'agent did not produce a reply' },
      { enteredLaneAt: new Date(NOW - 1_000).toISOString() },
    );

    expect(deriveStalledTaskState(job, NOW)).toMatchObject({ reason: 'failed', label: 'Stalled' });
  });

  it('uses an acute outcome issue when the runner registry only reports no-active-run', () => {
    const job = makeJob(
      { kind: 'no-active-run', attempt: 0 },
      {
        enteredLaneAt: new Date(NOW - 1_000).toISOString(),
        outcomeIssue: {
          kind: 'classifier-unknown',
          label: 'Unclear',
          severity: 'Warn',
          summary: 'Tool router reported an execution error',
          lastSeenAt: new Date(NOW - 2_000).toISOString(),
        },
      },
    );

    expect(deriveStalledTaskState(job, NOW)).toMatchObject({ reason: 'failed' });
  });

  it('does not flag live or scheduled-retry cards', () => {
    expect(deriveStalledTaskState(
      makeJob({ kind: 'active', processId: 42, attempt: 0 }),
      NOW,
    )).toBeNull();
    expect(deriveStalledTaskState(
      makeJob({ kind: 'failed-backoff', attempt: 1, backoffUntil: new Date(NOW + 60_000).toISOString() }),
      NOW,
    )).toBeNull();
  });

  it('does not let a retained runner badge hide a disconnected remote orphan', () => {
    const staleAt = new Date(NOW - STALLED_IDLE_THRESHOLD_MS - 1).toISOString();
    const job = makeJob(
      { kind: 'no-active-run', attempt: 0 },
      {
        enteredLaneAt: staleAt,
        lastActivity: '',
        runner: {
          runnerId: 'runner-1',
          runnerName: 'Runner 1',
          hostname: 'remote-host',
          backendName: 'remote',
          isRemote: true,
          leaseId: 'retained-lease',
          fencingToken: 3,
          acquiredAt: staleAt,
        },
        executionLocation: {
          state: 'remote-disconnected',
          executionKind: 'remote',
          runnerId: 'runner-1',
          hostDisplayName: 'Runner 1',
          startedAt: staleAt,
          lastHeartbeat: staleAt,
          lastActivityAt: staleAt,
          connectionState: 'disconnected',
          leaseState: 'expired',
          trustReason: 'The retained lease heartbeat is stale.',
        },
      },
    );

    expect(deriveStalledTaskState(job, NOW)).toMatchObject({ reason: 'idle', label: 'Stalled' });
  });
});

/**
 * AGT-2378: a task tab renders a TaskDetail fetched once on open. Only the board
 * list keeps receiving the runtime overlay (push + heartbeat), so every
 * run-liveness derivation in the detail has to read through to the live entry —
 * otherwise a remote run, which has no local CLI poll to compensate, stays
 * pinned on "kein aktiver Run" for as long as the card is open.
 */
describe('freshestRunInfo', () => {
  it('prefers the live board entry for the same task key', () => {
    const snapshot = makeJob({ kind: 'no-active-run', attempt: 0 });
    const live = makeJob({ kind: 'active', processId: 4242, attempt: 0 });

    expect(freshestRunInfo(snapshot, [makeJob(null, { taskKey: 'test::other' }), live]))
      .toBe(live);
    expect(buildRunActivityBadge(freshestRunInfo(snapshot, [live]), NOW))
      .toMatchObject({ kind: 'active', label: 'Run aktiv' });
  });

  it('falls back to the snapshot when the task is not in the live list', () => {
    const snapshot = makeJob({ kind: 'active', processId: 42, attempt: 0 });

    expect(freshestRunInfo(snapshot, [])).toBe(snapshot);
    expect(freshestRunInfo(snapshot, [makeJob(null, { taskKey: 'test::other' })])).toBe(snapshot);
  });

  /**
   * The live entry may only override the snapshot when it is demonstrably at
   * least as fresh. A mutation applied in the detail (lane move) lands in the
   * snapshot instantly, while the board push carrying the same change can be
   * seconds behind — an unconditional live-wins rule makes the display jump
   * back to the pre-move lane.
   */
  describe('does not let a lagging board push undo a detail mutation', () => {
    it('keeps the snapshot when the just-moved lane is newer than the live entry', () => {
      const snapshot = makeJob({ kind: 'no-active-run', attempt: 0 }, {
        state: TaskState.HumanReview,
        enteredLaneAt: new Date(NOW - 1_000).toISOString(),
      });
      const live = makeJob({ kind: 'active', processId: 4242, attempt: 0 }, {
        state: TaskState.Progress,
        enteredLaneAt: new Date(NOW - 120_000).toISOString(),
        // Still heartbeating against the lane it was picked up in: newer than the
        // move, but no evidence at all about where the task now lives.
        executionLocation: {
          state: 'local-running',
          executionKind: 'local',
          connectionState: 'connected',
          leaseState: 'active',
          trustReason: 'process',
          lastHeartbeat: new Date(NOW).toISOString(),
          lastActivityAt: new Date(NOW).toISOString(),
        },
      });

      expect(freshestRunInfo(snapshot, [live])).toBe(snapshot);
    });

    it('takes the live entry when its state stamp is newer than the snapshot', () => {
      const snapshot = makeJob({ kind: 'active', processId: 42, attempt: 0 }, {
        state: TaskState.Progress,
        enteredLaneAt: new Date(NOW - 120_000).toISOString(),
      });
      const live = makeJob({ kind: 'no-active-run', attempt: 0 }, {
        state: TaskState.HumanReview,
        enteredLaneAt: new Date(NOW - 1_000).toISOString(),
      });

      expect(freshestRunInfo(snapshot, [live])).toBe(live);
    });

    it('still prefers the live entry inside the same lane, whatever the stamps say', () => {
      const snapshot = makeJob({ kind: 'no-active-run', attempt: 0 }, {
        enteredLaneAt: new Date(NOW - 1_000).toISOString(),
      });
      const live = makeJob({ kind: 'active', processId: 4242, attempt: 0 }, {
        enteredLaneAt: new Date(NOW - 120_000).toISOString(),
      });

      expect(freshestRunInfo(snapshot, [live])).toBe(live);
      expect(buildRunActivityBadge(freshestRunInfo(snapshot, [live]), NOW))
        .toMatchObject({ kind: 'active', label: 'Run aktiv' });
    });

    it('keeps the snapshot when neither side carries a usable timestamp', () => {
      const blank = { enteredLaneAt: null, phaseEnteredAt: null, lastActivity: '', createdAt: '' };
      const snapshot = makeJob({ kind: 'no-active-run', attempt: 0 }, { ...blank, state: TaskState.HumanReview });
      const live = makeJob({ kind: 'active', processId: 4242, attempt: 0 }, { ...blank, state: TaskState.Progress });

      expect(freshestRunInfo(snapshot, [live])).toBe(snapshot);
    });

    it('keeps the snapshot on an equally fresh live entry in a different lane', () => {
      const enteredLaneAt = new Date(NOW - 1_000).toISOString();
      const snapshot = makeJob({ kind: 'no-active-run', attempt: 0 }, { state: TaskState.HumanReview, enteredLaneAt });
      const live = makeJob({ kind: 'active', processId: 4242, attempt: 0 }, { state: TaskState.Progress, enteredLaneAt });

      expect(freshestRunInfo(snapshot, [live])).toBe(snapshot);
    });
  });
});

/**
 * AGT-2378: a remote run owns its task through a fenced lease and attempt
 * records — the local slot registry and local CLI execution registry, which
 * `runActivity` is classified from, know nothing about it.
 */
describe('isTaskRunActive — remote ownership', () => {
  it('trusts a held run lease over a negative runner classification', () => {
    const job = makeJob({ kind: 'no-active-run', attempt: 0 }, {
      runner: {
        runnerId: 'agent-runner-01',
        runnerName: 'agent-runner-01',
        hostname: 'agent-runner-01',
        backendName: 'stable',
        isRemote: true,
        leaseId: 'lease-1',
        fencingToken: 7,
        acquiredAt: new Date(NOW - 30_000).toISOString(),
      },
    });

    expect(isTaskRunActive(job)).toBe(true);
    expect(buildRunActivityBadge(job, NOW)).toMatchObject({ kind: 'active', label: 'Run aktiv' });
    expect(deriveStalledTaskState(job, NOW)).toBeNull();
  });

  it('trusts a connected remote execution location with no local process', () => {
    const job = makeJob({ kind: 'failed-idle', attempt: 2, lastError: 'stale failure' }, {
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        hostDisplayName: 'agent-runner-01',
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'lease',
      },
    });

    expect(isTaskRunActive(job)).toBe(true);
    expect(buildRunActivityBadge(job, NOW)).toMatchObject({ kind: 'active', label: 'Run aktiv' });
  });

  it('stays inactive when nothing owns the task any more', () => {
    const job = makeJob({ kind: 'no-active-run', attempt: 0 }, {
      executionLocation: {
        state: 'recovering',
        executionKind: 'none',
        connectionState: 'recovering',
        leaseState: 'none',
        trustReason: 'no owner',
      },
    });

    expect(isTaskRunActive(job)).toBe(false);
    expect(buildRunActivityBadge(job, NOW)).toMatchObject({ kind: 'no-active-run', label: 'kein aktiver Run' });
  });
});
