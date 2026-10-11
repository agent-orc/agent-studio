import { describe, expect, it } from 'vitest';
import {
  buildCardCtxMenuItems,
  DELETE_ID,
  EPIC_ASSIGN_PREFIX,
  EPIC_DETACH_ID,
  MOVE_PREFIX,
  cardMoveTarget,
} from './task-card-view-model';
import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, EpicRollup } from '../../../../models/task.model';
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

function deleteRow(items: MenuItem[]): MenuRow | undefined {
  return items.filter(isRow).find((r) => r.id === DELETE_ID);
}

describe('buildCardCtxMenuItems — delete row (AGT-2020)', () => {
  it('appends a destructive "Delete task" row at the very end of a task card menu', () => {
    const items = buildCardCtxMenuItems(makeJob(), false, [], null);
    const last = items[items.length - 1];
    const beforeLast = items[items.length - 2];

    expect(isRow(last) && last.id).toBe(DELETE_ID);
    expect(isRow(last) && last.label).toBe('Delete task');
    expect(isRow(last) && last.danger).toBe(true);
    // Separator immediately precedes it so it never abuts the copy/assign rows.
    expect(beforeLast.kind).toBe('separator');
  });

  it('labels the row "Delete epic" on an epic card and is still the only-needed action', () => {
    const items = buildCardCtxMenuItems(makeJob({ kind: 'epic' }), true, [], null);
    const row = deleteRow(items);
    expect(row).toBeTruthy();
    expect(row!.label).toBe('Delete epic');
    expect(row!.danger).toBe(true);
    // Even when the epic card has no epic-assignment section, delete is present.
    expect(items[items.length - 1]).toBe(row);
  });

  it('keeps the delete row present alongside epic-assignment rows', () => {
    const epics: EpicRollup[] = [
      { id: 'epic-9', title: 'Big Epic', watchPath: '/tmp/watch' } as EpicRollup,
    ];
    const items = buildCardCtxMenuItems(makeJob(), false, epics, null);
    // Delete is still last, after the epic section.
    expect(deleteRow(items)).toBeTruthy();
    expect(items[items.length - 1]).toBe(deleteRow(items));
  });

  it('disables mutation rows in read-only mode while copy rows stay available', () => {
    const epics = [
      { id: 'epic-9', title: 'Big Epic', watchPath: '/tmp/watch' } as EpicRollup,
    ];
    const rows = buildCardCtxMenuItems(makeJob(), false, epics, 'epic-9', true).filter(isRow);

    expect(rows.find(item => item.id === 'copy-name')?.disabled).not.toBe(true);
    expect(rows.find(item => item.id === `${EPIC_ASSIGN_PREFIX}epic-9`)?.disabled).toBe(true);
    expect(rows.find(item => item.id === EPIC_DETACH_ID)?.disabled).toBe(true);
    expect(rows.find(item => item.id === DELETE_ID)?.disabled).toBe(true);
  });
});

describe('card move rows', () => {
  const moves = (state: string, blocked = false, visible?: readonly string[]) =>
    buildCardCtxMenuItems(makeJob({ state }), false, [], null, blocked, visible)
      .filter(isRow).filter((row) => row.id.startsWith(MOVE_PREFIX));

  it('uses visible manual neighbours and removes duplicate shortcuts', () => {
    const rows = moves(TaskState.HumanReview);
    expect(rows.map((row) => row.label)).toEqual([
      'Move back: Ready', 'Move forward: Delivered', 'Move to Archive',
    ]);
    expect(rows.map((row) => cardMoveTarget(row.id))).toEqual([
      TaskState.Ready, TaskState.Completed, TaskState.Archive,
    ]);
  });

  it('omits missing neighbours, hidden lanes, and current lane shortcuts', () => {
    expect(moves(TaskState.Backlog).map((row) => row.label)).toEqual([
      'Move forward: Preparation', 'Move to Delivered', 'Move to Archive',
    ]);
    expect(moves(TaskState.Archive).map((row) => row.label)).toEqual([
      'Move back: Delivered',
    ]);
    expect(moves(TaskState.HumanReview, false, [TaskState.Backlog, TaskState.Ready, TaskState.HumanReview])
      .map((row) => row.label)).toEqual([
      'Move back: Ready', 'Move to Delivered', 'Move to Archive',
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
