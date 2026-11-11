import { describe, expect, it } from 'vitest';
import { HttpErrorResponse } from '@angular/common/http';
import {
  deliveryMoveRefusal,
  describeMoveRefusal,
  isIntegrationRequired,
  laneMoveRefusal,
  laneMoveRefusalText,
  unintegratedDeliveryLoss,
} from './lane-move-guard';
import { TaskState, type TaskInfo } from './task.model';
import type { IntegrationStatusValue, TaskCommitInfo, TaskIntegrationStatus } from '../features/git';

function job(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'demo-1',
    taskKey: 'demo::demo-1',
    key: 'DEMO-1',
    title: 'Demo card',
    state: TaskState.HumanReview,
    order: 1,
    agent: 'codex',
    createdAt: '2026-11-09T09:00:00Z',
    watchPath: '/tmp/demo',
    projectName: 'Demo',
    folderPath: '/tmp/demo/5-human-review/demo-1',
    lastActivity: '2026-11-09T09:30:00Z',
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
    ...overrides,
  };
}

function integration(status: IntegrationStatusValue, extra: Partial<TaskIntegrationStatus> = {}): TaskIntegrationStatus {
  return { status, deliveryRef: null, sha: null, integrationBranch: 'develop', detail: null, ...extra };
}

const commit = { sha: 'a'.repeat(40), shortSha: 'aaaaaaa', message: 'feat: demo', at: '2026-11-09T09:10:00Z' } as TaskCommitInfo;

function conflict(body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status: 409, statusText: 'Conflict', error: body, url: '/api/v1/projects/x/tasks/demo-1/state' });
}

describe('deliveryMoveRefusal (AGT-3624, mirrors DeliveryLanePolicy)', () => {
  it('admits integrated and not-applicable deliveries on every guarded target', () => {
    for (const status of ['integrated', 'not-applicable'] as const) {
      for (const target of [TaskState.HumanReview, TaskState.Completed, TaskState.Archive]) {
        expect(deliveryMoveRefusal(job({ integration: integration(status) }), target)).toBeNull();
      }
    }
  });

  it('refuses each guarded target while the delivery is not integrated', () => {
    for (const status of ['pending', 'partial', 'merged-locally', 'conflict-skipped', 'no-branch'] as const) {
      for (const target of [TaskState.HumanReview, TaskState.Completed, TaskState.Archive]) {
        expect(deliveryMoveRefusal(job({ integration: integration(status) }), target), `${status} -> ${target}`)
          .not.toBeNull();
      }
    }
  });

  it('never guards an unguarded target', () => {
    const pending = job({ integration: integration('pending') });
    for (const target of [TaskState.Backlog, TaskState.Preparation, TaskState.Ready]) {
      expect(deliveryMoveRefusal(pending, target)).toBeNull();
    }
  });

  it('leaves a delivered card without a loaded verdict to the server', () => {
    expect(deliveryMoveRefusal(job({ integration: null }), TaskState.Completed)).toBeNull();
  });

  it('refuses archiving an unrun card whose work must integrate, but not a no-code card', () => {
    const unrun = job({ state: TaskState.Ready, integration: null });
    expect(deliveryMoveRefusal(unrun, TaskState.Archive)?.sentence).toBe('This card has not delivered its work yet.');
    expect(deliveryMoveRefusal({ ...unrun, requiresIntegration: false }, TaskState.Archive)).toBeNull();
    expect(deliveryMoveRefusal({ ...unrun, mode: 'planning' }, TaskState.Archive)).toBeNull();
    expect(deliveryMoveRefusal({ ...unrun, state: '2-ready-intake' }, TaskState.Archive)).not.toBeNull();
  });

  it('speaks plainly, without internal vocabulary', () => {
    const text = laneMoveRefusalText(deliveryMoveRefusal(job({ integration: integration('pending') }), TaskState.Completed)!);
    expect(text).toBe("This card's work is not in develop yet. Wait for integration to finish, then move it to Accepted.");
    expect(text).not.toMatch(/integration-pending|requirement-fit|brief version/);
  });
});

describe('isIntegrationRequired', () => {
  it('lets delivery evidence outrank the declared class', () => {
    expect(isIntegrationRequired(job({ requiresIntegration: false, commit }))).toBe(true);
    expect(isIntegrationRequired(job({ requiresIntegration: false }))).toBe(false);
    expect(isIntegrationRequired(job({ noBranchExpected: true }))).toBe(false);
    expect(isIntegrationRequired(job({ kind: 'decision' }))).toBe(false);
    expect(isIntegrationRequired(job({ taskType: 'concept' }))).toBe(false);
    expect(isIntegrationRequired(job())).toBe(true);
  });
});

describe('laneMoveRefusal', () => {
  it('puts the decision guard first', () => {
    const decision = job({
      kind: 'decision',
      state: TaskState.Preparation,
      decision: { question: 'Q', options: [], status: 'open' } as unknown as TaskInfo['decision'],
    });
    expect(laneMoveRefusal(decision, TaskState.Ready)?.sentence).toMatch(/Decision cards are decided/);
  });
});

describe('describeMoveRefusal', () => {
  const pendingCard = job({ integration: integration('pending') });

  it('ignores anything but a 409 refusal', () => {
    expect(describeMoveRefusal(new HttpErrorResponse({ status: 500 }), pendingCard, TaskState.Completed)).toBeNull();
    expect(describeMoveRefusal(new Error('x'), pendingCard, TaskState.Completed)).toBeNull();
  });

  it('translates the delivery guard category', () => {
    const refusal = describeMoveRefusal(conflict({
      error: 'integration-pending: Complete integration and publish the target branch before review. Current status: pending.',
      code: 'integration-dead-end',
    }), pendingCard, TaskState.HumanReview);
    expect(refusal).toEqual({
      sentence: "This card's work is not in develop yet.",
      nextAction: 'Wait for integration to finish, then move it to Human review.',
    });
  });

  it('translates the completion content refusal', () => {
    const refusal = describeMoveRefusal(conflict({
      error: 'Completion refused: delivery brief version is missing; no content verdict (requirement-fit); only no aspects ran.',
      code: 'completion-content-gap',
    }), pendingCard, TaskState.Completed);
    expect(refusal?.sentence).toBe("This card's review does not cover its current brief, so it cannot be accepted yet.");
  });

  it('falls back to a plain sentence for an unknown refusal', () => {
    const refusal = describeMoveRefusal(conflict({ error: 'Something internal' }), job(), TaskState.Archive);
    expect(refusal?.sentence).toBe('This card cannot move to Archive right now.');
  });
});

describe('unintegratedDeliveryLoss', () => {
  it('names the lost work for a delivery that is not integrated', () => {
    const loss = unintegratedDeliveryLoss(job({
      commits: [commit],
      integration: integration('pending', { deliveryRef: 'runner/host/DEMO-1' }),
    }));
    expect(loss).toContain("This card's delivered work (1 commit on runner/host/DEMO-1) is not integrated into develop.");
    expect(loss).toContain('nothing will integrate it later');
  });

  it('is null when nothing undelivered is at stake', () => {
    expect(unintegratedDeliveryLoss(job())).toBeNull();
    expect(unintegratedDeliveryLoss(job({ commits: [commit], integration: integration('integrated') }))).toBeNull();
    expect(unintegratedDeliveryLoss(job({ integration: integration('no-branch') }))).toBeNull();
  });

  it('says when integration is unconfirmed', () => {
    expect(unintegratedDeliveryLoss(job({ state: TaskState.Ready, commit })))
      .toContain('has not been confirmed as integrated into develop');
  });
});
