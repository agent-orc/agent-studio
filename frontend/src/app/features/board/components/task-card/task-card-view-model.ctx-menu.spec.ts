import { describe, expect, it } from 'vitest';
import {
  buildCardCtxMenuItems,
  DELETE_ID,
  REVIEW_FIRST_ID,
  MOVE_PREFIX,
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

  it('offers Review first and Normal review order on auto-review cards', () => {
    const normal = buildCardCtxMenuItems(makeJob({ state: TaskState.AutoReview })).filter(isRow);
    expect(normal.find(item => item.id === REVIEW_FIRST_ID)?.disabled).not.toBe(true);
    expect(normal.find(item => item.id === 'review-normal')?.disabled).toBe(true);
    const urgent = buildCardCtxMenuItems(makeJob({ state: TaskState.AutoReview, reviewPriority: 'urgent' })).filter(isRow);
    expect(urgent.find(item => item.id === REVIEW_FIRST_ID)?.disabled).toBe(true);
    expect(urgent.find(item => item.id === 'review-normal')?.disabled).not.toBe(true);
  });
});

describe('card move rows', () => {
  const moves = (state: string, blocked = false, visible?: readonly string[]) =>
    buildCardCtxMenuItems(makeJob({ state }), blocked, visible)
      .filter(isRow).filter((row) => row.id.startsWith(MOVE_PREFIX));

  it('uses visible manual neighbours and removes duplicate shortcuts', () => {
    const rows = moves(TaskState.HumanReview);
    expect(rows.map((row) => row.label)).toEqual([
      'Move back: Ready', 'Move forward: Accepted', 'Move to Archive',
    ]);
    expect(rows.map((row) => cardMoveTarget(row.id))).toEqual([
      TaskState.Ready, TaskState.Completed, TaskState.Archive,
    ]);
  });

  it('omits missing neighbours, hidden lanes, and current lane shortcuts', () => {
    expect(moves(TaskState.Backlog).map((row) => row.label)).toEqual([
      'Move forward: Preparation', 'Move to Accepted', 'Move to Archive',
    ]);
    expect(moves(TaskState.Archive).map((row) => row.label)).toEqual([
      'Move back: Accepted',
    ]);
    expect(moves(TaskState.HumanReview, false, [TaskState.Backlog, TaskState.Ready, TaskState.HumanReview])
      .map((row) => row.label)).toEqual([
      'Move back: Ready', 'Move to Accepted', 'Move to Archive',
    ]);
  });

  it('maps virtual Ready to Ready and disables every move when blocked', () => {
    expect(moves('2-ready-intake', true).map((row) => row.label)[0]).toBe('Move back: Preparation');
    expect(moves(TaskState.CodeNotComplete).map((row) => row.label).slice(0, 2)).toEqual([
      'Move back: Ready', 'Move forward: Human review',
    ]);
    expect(moves(TaskState.Ready, true).every((row) => row.disabled)).toBe(true);
    expect(cardMoveTarget(MOVE_PREFIX + TaskState.Progress)).toBeNull();
  });
});
