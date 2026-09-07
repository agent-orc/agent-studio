import { describe, expect, it } from 'vitest';
import { buildFailureClassBadge } from './task-card-view-model';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo } from '../../../../models/task.model';
import type { TaskIntegrationFailure } from '../../../../features/git';

/**
 * AGT-2749 — the failure-class badge on a parked card. On 2026-09-06 thirteen
 * cards sat in Human Review as product failures while every one of them was a
 * host or account fault, and the lane said nothing about it. The badge states
 * the classified failure class plus the bounded requeue counter; a product
 * failure and a card without a class stay quiet.
 */
function makeJob(failure: Partial<TaskIntegrationFailure> | null, state: string = TaskState.HumanReview): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    title: 'Task 1',
    state,
    order: 1,
    agent: 'claude',
    createdAt: '2026-09-06T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: `/tmp/watch/${state}/task-1`,
    lastActivity: '2026-09-06T09:30:00Z',
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
    integration: {
      status: 'pending',
      deliveryRef: 'task/task-1',
      sha: null,
      integrationBranch: 'develop',
      detail: null,
      failure: failure
        ? {
          code: 'gate-failed',
          label: 'Gate failed',
          reason: 'The gate run did not finish.',
          rebaseRecoveryAvailable: false,
          ...failure,
        }
        : null,
    },
  } as TaskInfo;
}

describe('buildFailureClassBadge (AGT-2749)', () => {
  it('states the infrastructure class and the retry counter in Human Review', () => {
    const badge = buildFailureClassBadge(makeJob({
      failureClass: 'infrastructure',
      failureSignature: 'gate-budget-exceeded',
      retryAttempt: 2,
      retryBudget: 3,
    }));

    expect(badge).not.toBeNull();
    expect(badge!.failureClass).toBe('infrastructure');
    expect(badge!.label).toBe('Infrastructure · retry 2/3');
    expect(badge!.retry).toEqual({ attempt: 2, budget: 3 });
    expect(badge!.tooltip).toContain('gate-budget-exceeded');
    expect(badge!.tooltip).toContain('Retry 2 of 3');
  });

  it('renders the same way for a quota class on an Escalated card', () => {
    const badge = buildFailureClassBadge(makeJob({
      failureClass: 'quota',
      failureSignature: 'cli-quota-exhausted',
      retryAttempt: 1,
    }, TaskState.Escalated));

    expect(badge!.label).toBe('Quota · retry 1/3');
    expect(badge!.retry).toEqual({ attempt: 1, budget: 3 });
  });

  it('renders nothing for a product failure class', () => {
    expect(buildFailureClassBadge(makeJob({
      failureClass: 'product',
      failureSignature: 'new-test-failures',
      retryAttempt: 0,
      retryBudget: 3,
    }))).toBeNull();
  });

  it('renders nothing when the payload carries no failure class', () => {
    expect(buildFailureClassBadge(makeJob({}))).toBeNull();
    expect(buildFailureClassBadge(makeJob({ failureClass: null }))).toBeNull();
    expect(buildFailureClassBadge(makeJob(null))).toBeNull();
  });

  it('states an unknown class without inventing a retry counter', () => {
    const badge = buildFailureClassBadge(makeJob({ failureClass: 'unknown' }));
    expect(badge!.label).toBe('Unclassified');
    expect(badge!.retry).toBeNull();
    expect(badge!.tooltip).toContain('No requeue budget');
  });

  it('falls back to unknown for a slug this frontend does not know', () => {
    const badge = buildFailureClassBadge(makeJob({ failureClass: 'sunspots', retryAttempt: 2 }));
    expect(badge!.failureClass).toBe('unknown');
    expect(badge!.retry).toBeNull();
  });

  it('stays off every lane except Review and Escalated', () => {
    for (const state of [TaskState.Progress, TaskState.AutoReview, TaskState.Completed]) {
      expect(buildFailureClassBadge(makeJob({
        failureClass: 'infrastructure',
        retryAttempt: 2,
      }, state))).toBeNull();
    }
  });
});
