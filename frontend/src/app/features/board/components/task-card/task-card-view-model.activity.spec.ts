import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import type { AutoReviewStatusView } from '../../../../services/auto-review-status.store';
import { buildAutoReviewProcessBadge } from './task-card-view-model';

const now = Date.parse('2026-07-23T12:00:00Z');

function task(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'review-1',
    taskKey: 'project::review-1',
    title: 'Review task',
    state: '4-auto-review',
    order: 1,
    agent: 'codex',
    createdAt: '2026-07-23T09:00:00Z',
    enteredLaneAt: '2026-07-23T09:50:00Z',
    watchPath: '/workspace',
    projectName: 'project',
    folderPath: '/workspace/4-auto-review/review-1',
    lastActivity: '2026-07-23T09:50:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ...overrides,
  };
}

function status(step: string): AutoReviewStatusView {
  return {
    lastTickAt: '2026-07-23T12:00:00Z',
    accept: 0,
    reissue: 0,
    escalate: 0,
    aspectsRun: 0,
    currentJob: 'review-1',
    currentProject: 'project',
    activeJobs: [{
      project: 'project',
      jobId: 'review-1',
      step,
      startedAt: '2026-07-23T11:52:00Z',
    }],
  };
}

describe('post-processing card activity', () => {
  it('names the active snapshot step', () => {
    expect(buildAutoReviewProcessBadge(task(), status('gate'), now)).toMatchObject({
      tone: 'active',
      label: 'Gate running',
    });
    expect(buildAutoReviewProcessBadge(task(), status('aspects'), now)?.label).toBe('Aspects');
    expect(buildAutoReviewProcessBadge(task(), status('grade'), now)?.label).toBe('Grade');
  });

  it('separates machine-lock queueing from active gate work', () => {
    expect(buildAutoReviewProcessBadge(task(), status('gate-queued'), now)).toMatchObject({
      tone: 'gate-queued',
      label: 'Waiting for a gate slot · 8m',
    });
    expect(buildAutoReviewProcessBadge(task(), status('gate-queued:2/3'), now)?.label)
      .toBe('Waiting for a gate slot (2/3 busy) · 8m');
    expect(buildAutoReviewProcessBadge(task(), status('gate:3/3'), now)?.label)
      .toBe('Gate running (slot 3/3)');
  });

  it('formats the wait from enteredLaneAt when no active snapshot matches', () => {
    expect(buildAutoReviewProcessBadge(task(), null, now)).toMatchObject({
      tone: 'waiting',
      label: 'waiting 2h 10m',
    });
  });

  it('uses lifecycle checks to label a legacy active snapshot', () => {
    const legacy = status('aspects');
    legacy.activeJobs = undefined;
    expect(buildAutoReviewProcessBadge(task({
      postProcessingChecks: [{ name: 'code-review-grade', status: 'running' }],
    }), legacy, now)?.label).toBe('Grade');
  });

  it('uses a running lifecycle check when the status snapshot is unavailable', () => {
    expect(buildAutoReviewProcessBadge(task({
      postProcessingChecks: [{ name: 'build-test-gate', status: 'running' }],
    }), null, now)).toMatchObject({
      tone: 'active',
      label: 'Gate running',
    });
  });

  it('uses the running phase only when no newer status snapshot is available', () => {
    expect(buildAutoReviewProcessBadge(task({
      phase: 'post-processing-running',
    }), null, now)).toMatchObject({
      tone: 'active',
      label: 'Processing',
    });
    expect(buildAutoReviewProcessBadge(task({
      phase: 'post-processing-running',
    }), status('grade'), now)).toMatchObject({
      tone: 'active',
      label: 'Grade',
    });
    expect(buildAutoReviewProcessBadge(task({
      id: 'another-review',
      phase: 'post-processing-running',
    }), status('grade'), now)).toMatchObject({
      tone: 'waiting',
      label: 'waiting 2h 10m',
    });
  });
});
