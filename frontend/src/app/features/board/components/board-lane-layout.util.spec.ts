/**
 * AGT-2819: these assertions are the board-lane behaviour the `App` shell used
 * to carry inline as `focusGroups` / `laneGroups`. They pin the public
 * behaviour of the extracted unit so the split is provably behaviour-preserving.
 */
import { describe, expect, it } from 'vitest';
import {
  DELIVERY_GROUP_ID, buildDeliveryGroup, buildFocusLanes, buildLaneGroups, focusVisibleLaneStates, laneChrome,
} from './board-lane-layout.util';
import type { DeliveryColumn, DeliveryColumnCard, DeliveryColumnsResponse } from '../../../models/delivery-column.model';
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

describe('focusVisibleLaneStates', () => {
  it('lists every always-rendered lane in board order when no container is focused', () => {
    expect(focusVisibleLaneStates(null)).toEqual([
      TaskState.Ready,
      TaskState.Preparation,
      TaskState.Backlog,
      TaskState.Progress,
      TaskState.AutoReview,
      TaskState.HumanReview,
      TaskState.Completed,
      TaskState.Archive,
    ]);
  });

  it('narrows to the focused container', () => {
    expect(focusVisibleLaneStates('decide')).toEqual([
      TaskState.HumanReview,
      TaskState.Completed,
      TaskState.Archive,
    ]);
  });
});

describe('buildDeliveryGroup', () => {
  const card = (key: string, releaseVersion: string | null = null, rolledBack = false): DeliveryColumnCard =>
    ({ key, releaseId: releaseVersion, releaseVersion, rolledBack });
  const column = (id: string, name: string, parts: Partial<DeliveryColumn> = {}): DeliveryColumn => ({
    id, environmentId: id === 'integrated' ? null : id, name, kind: null, order: 0,
    currentRelease: null, lastRun: null, health: null, pendingApproval: null,
    landed: [], waiting: [], rolledBack: [], ...parts,
  });
  const keyed = (key: string, project = 'demo') =>
    ({ ...task(key.toLowerCase(), TaskState.Completed), displayKey: key, projectName: project }) as TaskInfo;
  const lanes = (projectId: string, columns: DeliveryColumn[]): DeliveryColumnsResponse =>
    ({ projectId, releaseDepth: 5, columns });
  const keys = (cards: { task: TaskInfo }[]) => cards.map((c) => c.task.displayKey);

  const fixture = lanes('demo', [
    column('integrated', 'Integrated', { landed: [card('AGT-5')] }),
    column('dev', 'Dev', {
      currentRelease: { id: 'r2', version: '2026.10.21.1', channel: 'default', createdAtUtc: '2026-10-21T09:00:00Z' },
      lastRun: { id: 'd1', releaseId: 'r2', state: 'succeeded', trigger: 'deploy', atUtc: '2026-10-21T09:12:00Z' },
      health: 'healthy',
      landed: [card('AGT-3', '2026.10.21.1'), card('AGT-1', '2026.10.20.3')],
    }),
    column('stage', 'Stage', {
      landed: [card('AGT-1', '2026.10.20.3')],
      waiting: [card('AGT-3', '2026.10.21.1'), card('AGT-4', '2026.10.21.1', true)],
    }),
    column('production', 'Production', {
      pendingApproval: { deploymentId: 'd9', releaseId: 'r1', releaseVersion: '2026.10.20.3', requestedAtUtc: '2026-10-21T10:00:00Z' },
      waiting: [card('AGT-1', '2026.10.20.3')],
    }),
  ]);
  const tasks = ['AGT-1', 'AGT-3', 'AGT-4', 'AGT-5'].map((key) => keyed(key));

  it('follows Done & Decide with an Integrated column and one column per environment in read-model order', () => {
    const group = buildDeliveryGroup([fixture], tasks, false);
    expect(group.id).toBe(DELIVERY_GROUP_ID);
    expect(group.columns.map((c) => c.title)).toEqual([
      'Integrated', 'Delivery to Dev', 'Delivery to Stage', 'Delivery to Production',
    ]);
    expect(group.columns[0].integrated).toBe(true);
    expect(group.columns[0].headers).toEqual([]);
  });

  it('lists landed cards first and waiting cards after, keeping the rolled-back flag', () => {
    const [, dev, stage, production] = buildDeliveryGroup([fixture], tasks, false).columns;
    expect(keys(dev.landed)).toEqual(['AGT-3', 'AGT-1']);
    expect(keys(stage.landed)).toEqual(['AGT-1']);
    expect(keys(stage.waiting)).toEqual(['AGT-3', 'AGT-4']);
    expect(stage.waiting.map((c) => c.rolledBack)).toEqual([false, true]);
    expect(production.approvalPending).toBe(true);
    expect(production.headers[0].approval?.releaseVersion).toBe('2026.10.20.3');
    expect(dev.headers[0]).toMatchObject({ projectId: 'demo', releaseVersion: '2026.10.21.1', health: 'healthy' });
  });

  it('renders only cards the board feed carries and counts exactly the visible tiles', () => {
    const group = buildDeliveryGroup([fixture], tasks.filter((t) => t.displayKey !== 'AGT-1'), false);
    const sum = group.columns.reduce((n, c) => n + c.landed.length + c.waiting.length, 0);
    expect(group.count).toBe(sum);
    expect(group.columns.flatMap((c) => [...keys(c.landed), ...keys(c.waiting)])).not.toContain('AGT-1');
  });

  it('shows each card once in its furthest environment when the display option is on', () => {
    const group = buildDeliveryGroup([fixture], tasks, true);
    const placed = group.columns.map((c) => ({ landed: keys(c.landed), waiting: keys(c.waiting) }));
    expect(placed).toEqual([
      { landed: ['AGT-5'], waiting: [] },
      { landed: ['AGT-3'], waiting: [] },
      { landed: ['AGT-1'], waiting: ['AGT-4'] },
      { landed: [], waiting: [] },
    ]);
    expect(group.count).toBe(4);
  });

  it('adds a rolled-back card the read model lists outside waiting to the waiting part', () => {
    const response = lanes('demo', [column('stage', 'Stage', { rolledBack: [card('AGT-4', '2026.10.21.1', true)] })]);
    const [stage] = buildDeliveryGroup([response], tasks, false).columns;
    expect(keys(stage.waiting)).toEqual(['AGT-4']);
    expect(stage.waiting[0].rolledBack).toBe(true);
  });

  it('merges the same environment of several projects into one column with a header per project', () => {
    const other = lanes('api', [column('integrated', 'Integrated'), column('stage', 'Stage', { landed: [card('API-1', 'v1')] })]);
    const group = buildDeliveryGroup([fixture, other], [...tasks, keyed('API-1', 'api')], false);
    const stage = group.columns.find((c) => c.id === 'stage')!;
    expect(stage.headers.map((h) => h.projectId)).toEqual(['demo', 'api']);
    expect(keys(stage.landed)).toEqual(['AGT-1', 'API-1']);
    expect(group.columns.map((c) => c.id)).toEqual(['integrated', 'dev', 'stage', 'production']);
  });

  it('matches card keys per project, so an equal key in another project does not resolve', () => {
    const group = buildDeliveryGroup([fixture], [keyed('AGT-1', 'other')], false);
    expect(group.count).toBe(0);
  });
});
