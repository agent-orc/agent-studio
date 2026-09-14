import { describe, expect, it } from 'vitest';
import {
  archiveIntegrationVerdict,
  deliveryClaimVerdict,
  isDeliveredArchiveMove,
  mergeAcceptViewFor,
  needsPlanningAcceptWarning,
  overflowActionsFor,
  primaryActionFor,
} from './triage-actions.model';
import { TaskState } from '../../../models/task.model';
import type { PlanningSpawnSummary, TaskInfo, TaskMode } from '../../../models/task.model';
import type { TaskIntegrationStatus, TaskProvenanceRecord } from '../../../features/git';

function reviewJob(provenance: TaskProvenanceRecord | null = null, overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ATP-1',
    title: 'Task 1',
    state: TaskState.HumanReview,
    order: 1,
    agent: 'codex',
    createdAt: '2026-06-10T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/5-human-review/task-1',
    lastActivity: '2026-06-10T09:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: { sha: 'abc1234', shortSha: 'abc1234', message: 'task work', filesChanged: 1, files: ['x.ts'], at: '2026-06-10T10:00:00Z' },
    provenance,
    ...overrides,
  };
}

function mergedProvenance(mergeCommit: string | null): TaskProvenanceRecord {
  return {
    branch: 'task/task-1',
    base: 'base000',
    transitions: [],
    merge: mergeCommit
      ? { mergeCommit, workBranchHeadBefore: 'dev0000', workBranchHeadAfter: mergeCommit, atUtc: '2026-06-10T10:30:00Z' }
      : null,
  };
}

function integrated(overrides: Partial<TaskIntegrationStatus> = {}): TaskIntegrationStatus {
  return {
    status: 'integrated',
    deliveryRef: 'task/triage-fixture',
    sha: 'ddddddd',
    integrationBranch: 'develop',
    detail: 'anchor-ancestor',
    ...overrides,
  };
}

function overflowIds(state: string): string[] {
  return overflowActionsFor(state).map(b => b.id);
}

function planningJob(
  spawn: PlanningSpawnSummary | null | undefined,
  mode: TaskMode = 'planning',
): TaskInfo {
  return { ...reviewJob(), mode, planningSpawn: spawn };
}

function summary(partial: Partial<PlanningSpawnSummary>): PlanningSpawnSummary {
  return {
    spawned: [],
    spawnedCount: 0,
    noFollowUpDeclared: false,
    contractSatisfied: false,
    ...partial,
  };
}

describe('needsPlanningAcceptWarning — AGT-2069 spawn-contract accept guard', () => {
  const accept = TaskState.Completed;

  it('warns when a planning task with no spawns and no declaration is accepted', () => {
    const job = planningJob(summary({ contractSatisfied: false }));
    expect(needsPlanningAcceptWarning(job, accept)).toBe(true);
  });

  it('does not warn once a follow-up card was spawned', () => {
    const job = planningJob(summary({
      spawned: [{ targetKey: 'WEB-1', at: '2026-07-10T00:00:00Z' }],
      spawnedCount: 1,
      contractSatisfied: true,
    }));
    expect(needsPlanningAcceptWarning(job, accept)).toBe(false);
  });

  it('does not warn once no-follow-up is declared', () => {
    const job = planningJob(summary({ noFollowUpDeclared: true, contractSatisfied: true }));
    expect(needsPlanningAcceptWarning(job, accept)).toBe(false);
  });

  it('only guards the accept target (6-completed), not other moves', () => {
    const job = planningJob(summary({ contractSatisfied: false }));
    expect(needsPlanningAcceptWarning(job, TaskState.Backlog)).toBe(false);
    expect(needsPlanningAcceptWarning(job, TaskState.Ready)).toBe(false);
  });

  it('never guards coding or research tasks', () => {
    expect(needsPlanningAcceptWarning(planningJob(null, 'coding'), accept)).toBe(false);
    expect(needsPlanningAcceptWarning(planningJob(summary({}), 'research'), accept)).toBe(false);
  });

  it('does not guess when the planning projection is absent (older payload)', () => {
    expect(needsPlanningAcceptWarning(planningJob(null), accept)).toBe(false);
    expect(needsPlanningAcceptWarning(planningJob(undefined), accept)).toBe(false);
  });
});

describe('archive guard: containment decides, an absent record is a question', () => {
  type Status = 'integrated' | 'partial' | 'pending' | 'conflict-skipped' | 'no-branch';
  const completed = (status: Status | null): TaskInfo =>
    reviewJob(null, {
      state: TaskState.Completed,
      integration: status === null ? null : {
        status,
        deliveryRef: 'task/triage-fixture',
        sha: status === 'integrated' ? 'abc1234' : null,
        integrationBranch: 'develop',
        detail: status,
      },
    });

  it('reads a real negative verdict as not-integrated', () => {
    expect(archiveIntegrationVerdict(completed('pending'))).toBe('not-integrated');
    expect(archiveIntegrationVerdict(completed('partial'))).toBe('not-integrated');
    expect(archiveIntegrationVerdict(completed('conflict-skipped'))).toBe('not-integrated');
  });

  /**
   * AGT-2706: the delivery was contained in develop and in main; the dialog
   * appeared only because the card carried no integration record. An absent
   * projection is `unknown`, and the caller resolves it before speaking.
   */
  it('reads an absent projection as unknown, not as not-integrated', () => {
    expect(archiveIntegrationVerdict(completed(null))).toBe('unknown');
  });

  it('reads integrated work and a card with nothing to integrate as non-findings', () => {
    expect(archiveIntegrationVerdict(completed('integrated'))).toBe('integrated');
    expect(archiveIntegrationVerdict(completed('no-branch'))).toBe('nothing-to-integrate');
  });

  it('reads the same verdict from the per-card containment answer', () => {
    expect(deliveryClaimVerdict({ integrated: true, containmentStatus: 'integrated' }))
      .toBe('integrated');
    expect(deliveryClaimVerdict({ integrated: false, containmentStatus: 'pending' }))
      .toBe('not-integrated');
    expect(deliveryClaimVerdict({ integrated: false, containmentStatus: 'no-branch' }))
      .toBe('nothing-to-integrate');
    expect(deliveryClaimVerdict({ integrated: false, containmentStatus: 'unknown' }))
      .toBe('unknown');
  });

  it('covers only the Delivered -> Archive transition', () => {
    expect(isDeliveredArchiveMove(completed('pending'), TaskState.Archive)).toBe(true);
    expect(isDeliveredArchiveMove(completed('pending'), TaskState.Backlog)).toBe(false);
    expect(isDeliveredArchiveMove(reviewJob(), TaskState.Archive)).toBe(false);
  });
});

describe('primaryActionFor — Enter-bound primary per source lane', () => {
  it('uses decision-oriented labels for the escalated lane', () => {
    const primary = primaryActionFor('5e-escalated');
    expect(primary?.label).toBe('Continue (reissue)');
    expect(overflowActionsFor('5e-escalated').map(action => action.label)).toEqual(
      expect.arrayContaining(['Accept as-is', 'Abort']),
    );
  });

  it('labels the Completed lane primary "Archive & Next" and moves to 7-archive', () => {
    const primary = primaryActionFor('6-completed');
    expect(primary).not.toBeNull();
    expect(primary!.id).toBe('archive');
    expect(primary!.label).toBe('Archive & Next');
    expect(primary!.intent).toEqual({ kind: 'move', targetState: '7-archive' });
  });

  it('labels the Review lane primary "Merge into Develop" (→ 6-completed acceptance signal)', () => {
    const primary = primaryActionFor('5-human-review');
    expect(primary).not.toBeNull();
    expect(primary!.id).toBe('mark-done');
    expect(primary!.label).toBe('Merge into Develop');
    expect(primary!.intent).toEqual({ kind: 'move', targetState: '6-completed' });
  });

  it('leaves the Post Processing lane without a primary (Enter is a no-op)', () => {
    expect(primaryActionFor('4-auto-review')).toBeNull();
  });
});

describe('overflowActionsFor — Move to Completed / Move to Archive', () => {
  it('offers both moves from Ready, next to Send to Backlog and before Edit/Delete', () => {
    const ids = overflowIds('2-ready');
    expect(ids).toEqual([
      'move-to-top',
      'send-to-backlog',
      'move-to-completed',
      'move-to-archive',
      'edit-prompt',
      'delete',
    ]);
  });

  it('offers both moves from Backlog', () => {
    const ids = overflowIds('0-backlog');
    expect(ids).toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
    // Move entries precede the Edit/Delete safety nets.
    expect(ids.indexOf('move-to-completed')).toBeLessThan(ids.indexOf('edit-prompt'));
    expect(ids.indexOf('move-to-archive')).toBeLessThan(ids.indexOf('delete'));
  });

  it('offers both moves from a generic lane that has neither target (preparation)', () => {
    const ids = overflowIds('1-preparation');
    expect(ids).toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
  });

  it('skips Move to Completed when the lane already routes to 6-completed', () => {
    // 5-human-review's primary "Merge into Develop" targets 6-completed, so the
    // overflow must not add a duplicate. Move to Archive is still offered.
    const ids = overflowIds('5-human-review');
    expect(ids).not.toContain('move-to-completed');
    expect(ids).toContain('move-to-archive');
  });

  it('skips Move to Archive when the lane already routes to 7-archive', () => {
    // 6-completed's primary "Archive" targets 7-archive. Move to Completed is
    // suppressed too because 6-completed is the current lane.
    const ids = overflowIds('6-completed');
    expect(ids).not.toContain('move-to-archive');
    expect(ids).not.toContain('move-to-completed');
  });

  it('suppresses the current-lane target (no self-move from Archive)', () => {
    const ids = overflowIds('7-archive');
    expect(ids).not.toContain('move-to-archive');
    // Moving an archived card forward to Completed is still allowed.
    expect(ids).toContain('move-to-completed');
  });
});

describe('mergeAcceptViewFor — state-dependent Human Review acceptance primary', () => {
  it('keeps the "Merge into Develop" offer when nothing has landed yet', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance(null)));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
    expect(view.statusLabel).toBeNull();
    expect(view.landedState).toBe('on-branch-only');
  });

  it('uses Accept when there is no attributed task commit to merge', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance(null), { commit: null, commits: [] }));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Accept');
  });

  it('uses Accept when computed status says the task commit is already in develop', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance(null), {
      integration: integrated({ sha: 'abc1234' }),
    }));
    expect(view.landed).toBe(true);
    expect(view.acceptLabel).toBe('Accept');
  });

  it('does not treat a recorded merge attempt as target-branch proof', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance('ddddddd9abc')));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
  });

  it('uses canonical membership evidence in the status label', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance(null), {
      integration: integrated({ sha: 'ddddddd9abc' }),
    }));
    expect(view.landed).toBe(true);
    expect(view.landedState).toBe('merged-to-develop');
    expect(view.acceptLabel).toBe('Accept');
    expect(view.statusLabel).toBe('Merged to develop @ddddddd');
    expect(view.statusTooltip).toContain('already merged into develop at ddddddd');
  });

  it('upgrades wording to "Released to main" from the live landed-state hint', () => {
    const view = mergeAcceptViewFor(
      reviewJob(mergedProvenance(null), { integration: integrated() }),
      'released-to-main',
    );
    expect(view.landed).toBe(true);
    expect(view.landedState).toBe('released-to-main');
    expect(view.acceptLabel).toBe('Accept');
    expect(view.statusLabel).toBe('Released to main');
  });

  it('does not land purely on the live hint when canonical status is absent', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance(null)), 'merged-to-develop');
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
  });

  it('never lets a stale on-branch-only hint mask canonical membership', () => {
    const view = mergeAcceptViewFor(
      reviewJob(mergedProvenance(null), { integration: integrated() }),
      'on-branch-only',
    );
    expect(view.landed).toBe(true);
    expect(view.landedState).toBe('merged-to-develop');
  });

  it('ignores a blank/whitespace merge commit string', () => {
    const view = mergeAcceptViewFor(reviewJob(mergedProvenance('   ')));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
  });

  it('stays an offer for a legacy card with no provenance at all', () => {
    const view = mergeAcceptViewFor(reviewJob(null));
    expect(view.landed).toBe(false);
    expect(view.acceptLabel).toBe('Merge into Develop');
  });
});
