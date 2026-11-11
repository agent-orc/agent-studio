import { describe, expect, it } from 'vitest';
import {
  buildCardCtxMenuItems,
  DELETE_ID,
  REVIEW_FIRST_ID,
  MOVE_PREFIX,
  MOVE_TOP_ID,
  MOVE_BOTTOM_ID,
  cardMoveTarget,
} from './task-card-view-model';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo } from '../../../../models/task.model';
import type { MenuItem, MenuRow } from '../../../../components/menu';

/**
 * AGT-2020: Delete moved off the hover trash button into the card context menu.
 * The destructive "Delete task" row must sit at the very end behind a separator
 * on every card kind, so it never abuts the everyday copy/assign rows.
 */
function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ATP-1',
    title: 'Task 1',
    state: TaskState.Ready,
    order: 1,
    agent: 'codex',
    createdAt: '2026-06-10T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/2-ready/task-1',
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
    ...overrides,
  };
}

function isRow(item: MenuItem): item is MenuRow {
  return item.kind === 'row';
}

describe('buildCardCtxMenuItems — delete row (AGT-2020)', () => {
  it('appends a destructive "Delete task" row at the very end of a task card menu', () => {
    const items = buildCardCtxMenuItems(makeJob());
    const last = items[items.length - 1];
    const beforeLast = items[items.length - 2];

    expect(isRow(last) && last.id).toBe(DELETE_ID);
    expect(isRow(last) && last.label).toBe('Delete task');
    expect(isRow(last) && last.danger).toBe(true);
    // Separator immediately precedes it so it never abuts the copy/assign rows.
    expect(beforeLast.kind).toBe('separator');
  });

  it('disables mutation rows in read-only mode while copy rows stay available', () => {
    const rows = buildCardCtxMenuItems(makeJob(), true).filter(isRow);

    expect(rows.find(item => item.id === 'copy-name')?.disabled).not.toBe(true);
    expect(rows.find(item => item.id === DELETE_ID)?.disabled).toBe(true);
  });

  it('keeps only copy and filter rows for a Reviewer (AGT-3670)', () => {
    for (const state of [TaskState.HumanReview, TaskState.Ready, TaskState.AutoReview]) {
      const items = buildCardCtxMenuItems(makeJob({ state }), false, undefined, null, { reviewsOnly: true });
      expect(items.filter(isRow).map(item => item.id)).toEqual(['copy-name', 'copy-id', 'copy-key', 'filter-dependents']);
      expect(items.some(item => item.kind === 'separator')).toBe(false);
    }
  });

  it('offers Review first and Normal review order on auto-review cards', () => {
    const normal = buildCardCtxMenuItems(makeJob({ state: TaskState.AutoReview })).filter(isRow);
    expect(normal.find(item => item.id === REVIEW_FIRST_ID)?.disabled).not.toBe(true);
    expect(normal.find(item => item.id === 'review-normal')?.disabled).toBe(true);
    const urgent = buildCardCtxMenuItems(makeJob({ state: TaskState.AutoReview, reviewPriority: 'urgent' })).filter(isRow);
    expect(urgent.find(item => item.id === REVIEW_FIRST_ID)?.disabled).toBe(true);
    expect(urgent.find(item => item.id === 'review-normal')?.disabled).not.toBe(true);
  });
});

describe('card move rows (AGT-3588)', () => {
  const moves = (state: string, blocked = false, visible?: readonly string[]) =>
    buildCardCtxMenuItems(makeJob({ state }), blocked, visible)
      .filter(isRow).filter((row) => row.id.startsWith(MOVE_PREFIX));
  const labels = (state: string, visible?: readonly string[]) => moves(state, false, visible).map((row) => row.label);

  it('lists every allowed manual target by lane name', () => {
    expect(labels(TaskState.HumanReview)).toEqual([
      'Move to Backlog', 'Move to Preparation', 'Move to Ready', 'Move to Accepted', 'Move to Archive',
    ]);
    expect(moves(TaskState.HumanReview).map((row) => cardMoveTarget(row.id))).toEqual([
      TaskState.Backlog, TaskState.Preparation, TaskState.Ready, TaskState.Completed, TaskState.Archive,
    ]);
    expect(labels(TaskState.CodeNotComplete)).toEqual([
      'Move to Backlog', 'Move to Preparation', 'Move to Ready', 'Move to Human review',
      'Move to Accepted', 'Move to Archive',
    ]);
  });

  it('reaches Ready from Backlog in one move', () => {
    expect(labels(TaskState.Backlog)).toContain('Move to Ready');
  });

  it('leaves out targets that skip execution on an unrun card', () => {
    for (const state of [TaskState.Backlog, TaskState.Preparation, TaskState.Ready, '2-ready-intake', TaskState.FailedPickup]) {
      const targets = moves(state).map((row) => cardMoveTarget(row.id));
      expect(targets, state).not.toContain(TaskState.HumanReview);
      expect(targets, state).not.toContain(TaskState.Completed);
    }
    expect(labels(TaskState.Ready)).toEqual(['Move to Backlog', 'Move to Preparation', 'Move to Archive']);
  });

  it('omits the current lane, hidden lanes, and orchestrator lanes', () => {
    expect(labels(TaskState.Archive)).toEqual([
      'Move to Backlog', 'Move to Preparation', 'Move to Ready', 'Move to Human review', 'Move to Accepted',
    ]);
    expect(labels(TaskState.HumanReview, [TaskState.Backlog, TaskState.Ready, TaskState.HumanReview]))
      .toEqual(['Move to Backlog', 'Move to Ready']);
    expect(labels('2-ready-intake')).not.toContain('Move to Ready');
    expect(cardMoveTarget(MOVE_PREFIX + TaskState.Progress)).toBeNull();
  });

  it('disables every move when blocked', () => {
    expect(moves(TaskState.Ready, true).every((row) => row.disabled)).toBe(true);
  });

  it('shows only Product-permitted moves for each lane', () => {
    const productMoves = (state: string) => buildCardCtxMenuItems(
      makeJob({ state }), false, undefined, null, { productSession: true },
    ).filter(isRow).filter((row) => row.id.startsWith(MOVE_PREFIX)).map((row) => row.label);
    expect(productMoves(TaskState.Backlog)).toEqual(['Move to Ready']);
    expect(productMoves(TaskState.Ready)).toEqual(['Move to Backlog']);
    expect(productMoves(TaskState.HumanReview)).toEqual(['Move to Accepted']);
    for (const state of [TaskState.Preparation, TaskState.Progress, TaskState.Completed, '2-ready-intake']) {
      expect(productMoves(state)).toEqual([]);
    }
    const rows = buildCardCtxMenuItems(makeJob({ state: TaskState.HumanReview }), false, undefined, null,
      { productSession: true }).filter(isRow);
    expect(rows.some((row) => row.id === DELETE_ID || row.id === REVIEW_FIRST_ID)).toBe(false);
  });

  it('shows no moves for a read-only viewer', () => {
    const rows = buildCardCtxMenuItems(makeJob({ state: TaskState.HumanReview }), false, undefined, null,
      { readOnlySession: true }).filter(isRow);
    expect(rows.some((row) => row.id.startsWith(MOVE_PREFIX) || row.id === DELETE_ID)).toBe(false);
  });
});

describe('Ready queue position rows (AGT-3588)', () => {
  const queueRows = (state: string, blocked = false, edge: { first: boolean; last: boolean } | null = null) =>
    buildCardCtxMenuItems(makeJob({ state }), blocked, undefined, edge)
      .filter(isRow).filter((row) => row.id === MOVE_TOP_ID || row.id === MOVE_BOTTOM_ID);

  it('offers Move to top and Move to bottom on Ready cards only', () => {
    expect(queueRows(TaskState.Ready).map((row) => row.label)).toEqual(['Move to top', 'Move to bottom']);
    expect(queueRows(TaskState.Backlog)).toEqual([]);
    expect(queueRows(TaskState.HumanReview)).toEqual([]);
  });

  it('disables the edge the card already sits at, and both when blocked', () => {
    expect(queueRows(TaskState.Ready, false, { first: true, last: false }).map((row) => !!row.disabled))
      .toEqual([true, false]);
    expect(queueRows(TaskState.Ready, false, { first: false, last: true }).map((row) => !!row.disabled))
      .toEqual([false, true]);
    expect(queueRows(TaskState.Ready, true).every((row) => row.disabled)).toBe(true);
  });
});

describe('card move rows follow the lane guard (AGT-3624)', () => {
  const pending = {
    status: 'pending' as const, deliveryRef: 'runner/host/ATP-1', sha: null, integrationBranch: 'develop', detail: null,
  };
  const row = (job: TaskInfo, target: string) => buildCardCtxMenuItems(job)
    .filter(isRow).find((item) => item.id === MOVE_PREFIX + target);

  it('disables a refused move and names the reason as a tooltip', () => {
    const job = makeJob({ state: TaskState.HumanReview, integration: pending });
    for (const target of [TaskState.Completed, TaskState.Archive]) {
      const item = row(job, target);
      expect(item?.disabled, target).toBe(true);
      expect(item?.tooltip, target).toMatch(/^This card's work is not in develop yet\. /);
    }
    expect(row(job, TaskState.Ready)?.disabled).toBe(false);
    expect(row(job, TaskState.Ready)?.tooltip).toBeUndefined();
  });

  it('keeps an admitted move enabled without a tooltip', () => {
    const job = makeJob({ state: TaskState.HumanReview, integration: { ...pending, status: 'integrated' } });
    expect(row(job, TaskState.Completed)?.disabled).toBe(false);
    expect(row(job, TaskState.Completed)?.tooltip).toBeUndefined();
  });

  it('disables Archive on an unrun card whose work must integrate', () => {
    expect(row(makeJob(), TaskState.Archive)?.disabled).toBe(true);
    expect(row(makeJob({ requiresIntegration: false }), TaskState.Archive)?.disabled).toBe(false);
  });

  it('keeps Delete available on every card', () => {
    const job = makeJob({ state: TaskState.HumanReview, integration: pending });
    expect(buildCardCtxMenuItems(job).filter(isRow).find((item) => item.id === DELETE_ID)?.disabled).toBe(false);
  });
});
