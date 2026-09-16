/**
 * AGT-2819: these assertions are the board-lane behaviour the `App` shell used
 * to carry inline as `focusGroups` / `laneGroups`. They pin the public
 * behaviour of the extracted unit so the split is provably behaviour-preserving.
 */
import { describe, expect, it } from 'vitest';
import { buildFocusLanes, buildLaneGroups, laneChrome } from './board-lane-layout.util';
import { READY_PHASES } from './ready-lane-split.util';
import { lanePresentation } from '../../../models/lane-presentation';
import { GroupedJobs, TaskInfo, TaskState } from '../../../models/task.model';

function task(id: string, state: string, phase?: string): TaskInfo {
  return {
    id,
    taskKey: `ws::${id}`,
    title: id,
    state,
    order: 1,
    agent: 'claude',
    createdAt: '2026-09-16T12:00:00Z',
    watchPath: 'ws',
    projectName: 'demo',
    folderPath: '/tmp',
    lastActivity: '2026-09-16T12:00:00Z',
    phase,
  } as TaskInfo;
}

function emptyGrouped(overrides: Partial<GroupedJobs> = {}): GroupedJobs {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    review: [],
    completed: [],
    archive: [],
    ...overrides,
  };
}

describe('laneChrome', () => {
  it('reads the display name and glyph from the lane presentation catalogue', () => {
    const lane = laneChrome(TaskState.HumanReview);
    expect(lane).toEqual({
      state: TaskState.HumanReview,
      title: lanePresentation(TaskState.HumanReview).name,
      icon: lanePresentation(TaskState.HumanReview).glyph,
    });
  });
});

describe('buildFocusLanes', () => {
  it('renders the seven standard lanes in workflow order', () => {
    expect(buildFocusLanes(emptyGrouped()).map(lane => lane.state)).toEqual([
      TaskState.Backlog,
      TaskState.Preparation,
      TaskState.Ready,
      TaskState.Progress,
      TaskState.AutoReview,
      TaskState.Escalated,
      TaskState.HumanReview,
      TaskState.Completed,
      TaskState.Archive,
    ]);
  });

  it('hides the code-not-complete park lane while it is empty', () => {
    const states = buildFocusLanes(emptyGrouped()).map(lane => lane.state);
    expect(states).not.toContain(TaskState.CodeNotComplete);
  });

  it('shows the code-not-complete park lane between progress and auto-review once populated', () => {
    const states = buildFocusLanes(
      emptyGrouped({ codeNotComplete: [task('stuck', TaskState.CodeNotComplete)] }),
    ).map(lane => lane.state);
    expect(states.indexOf(TaskState.CodeNotComplete)).toBe(states.indexOf(TaskState.Progress) + 1);
    expect(states.indexOf(TaskState.CodeNotComplete)).toBe(states.indexOf(TaskState.AutoReview) - 1);
  });

  it('carries each lane its own cards', () => {
    const card = task('t1', TaskState.Progress);
    const lanes = buildFocusLanes(emptyGrouped({ progress: [card] }));
    expect(lanes.find(lane => lane.state === TaskState.Progress)?.jobs).toEqual([card]);
  });
});

describe('buildLaneGroups', () => {
  it('maps the workflow onto the three contiguous containers', () => {
    const groups = buildLaneGroups(emptyGrouped(), false);
    expect(groups.map(group => group.id)).toEqual(['backlog', 'active', 'decide']);
    expect(groups.map(group => group.label)).toEqual(['Backlog', 'Active', 'Done & Decide']);
  });

  it('puts Ready first in the backlog container so it is not buried', () => {
    const [backlog] = buildLaneGroups(emptyGrouped(), false);
    expect(backlog.lanes.map(lane => lane.state)).toEqual([
      TaskState.Ready,
      TaskState.Preparation,
      TaskState.Backlog,
    ]);
  });

  it('splits Ready by phase and renders the intake lane only while it has cards', () => {
    const human = task('human', TaskState.Ready, READY_PHASES.humanReady);
    const intake = task('intake', TaskState.Ready, READY_PHASES.intakeRunning);
    const [backlog] = buildLaneGroups(emptyGrouped({ ready: [human, intake] }), false);
    expect(backlog.lanes.map(lane => lane.state)).toEqual([
      TaskState.Ready,
      '2-ready-intake',
      TaskState.Preparation,
      TaskState.Backlog,
    ]);
    expect(backlog.lanes[0].jobs).toEqual([human]);
    expect(backlog.lanes[1].jobs).toEqual([intake]);
  });

  it('hides the escalated lane when it is empty and no drag is in flight', () => {
    const [, , decide] = buildLaneGroups(emptyGrouped(), false);
    expect(decide.lanes.map(lane => lane.state)).toEqual([
      TaskState.HumanReview,
      TaskState.Completed,
      TaskState.Archive,
    ]);
  });

  it('keeps the escalated lane as a drop target while a card is being dragged', () => {
    const [, , decide] = buildLaneGroups(emptyGrouped(), true);
    expect(decide.lanes[0].state).toBe(TaskState.Escalated);
    expect(decide.lanes[0].jobs).toEqual([]);
  });

  it('puts intervention before acceptance once a card is escalated', () => {
    const [, , decide] = buildLaneGroups(
      emptyGrouped({ escalated: [task('e1', TaskState.Escalated)] }),
      false,
    );
    expect(decide.lanes.map(lane => lane.state)).toEqual([
      TaskState.Escalated,
      TaskState.HumanReview,
      TaskState.Completed,
      TaskState.Archive,
    ]);
  });

  it('hides the code-not-complete park lane in the active container until it is populated', () => {
    expect(buildLaneGroups(emptyGrouped(), false)[1].lanes.map(lane => lane.state)).toEqual([
      TaskState.Progress,
      TaskState.AutoReview,
    ]);
    const populated = buildLaneGroups(
      emptyGrouped({ codeNotComplete: [task('stuck', TaskState.CodeNotComplete)] }),
      false,
    );
    expect(populated[1].lanes.map(lane => lane.state)).toEqual([
      TaskState.Progress,
      TaskState.CodeNotComplete,
      TaskState.AutoReview,
    ]);
  });
});
