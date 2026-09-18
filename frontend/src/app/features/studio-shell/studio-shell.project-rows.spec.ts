import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { excludeEpics } from '../board';
import { buildProjectSidebarRows } from './studio-shell.project-rows';
import { ExplorerLaneDashboardComponent } from './components/explorer-lane-dashboard/explorer-lane-dashboard.component';
import type { GroupedJobs, TaskInfo } from '../../models/task.model';

const PROJECT = 'Token Economy';

function task(id: string, state: string, over: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id,
    taskKey: `ws::${id}`,
    title: id,
    state,
    order: 1,
    agent: 'claude',
    createdAt: '2026-08-23T08:00:00Z',
    lastActivity: '2026-08-23T09:00:00Z',
    watchPath: 'ws',
    projectName: PROJECT,
    folderPath: '/tmp',
    epicId: null,
    ...over,
  } as TaskInfo;
}

function epic(id: string, state: string): TaskInfo {
  return task(id, state, { kind: 'epic' });
}

function emptyGrouped(): GroupedJobs {
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
  } as GroupedJobs;
}

/**
 * The number of cards the flat lane board actually draws for the three lanes
 * the Explorer dashboard mirrors. Derived from `excludeEpics` - the board's own
 * display contract - rather than from a literal, so this stays a real invariant
 * if the board's filter ever changes.
 */
function visibleBoardLaneCount(grouped: GroupedJobs): number {
  const board = excludeEpics(grouped);
  return (
    board.ready.length +
    board.progress.length +
    board.humanReview.length +
    board.escalated.length
  );
}

function mountDashboard() {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  return TestBed.createComponent(ExplorerLaneDashboardComponent);
}

function renderedDotCount(grouped: GroupedJobs): number {
  const rows = buildProjectSidebarRows(grouped, [PROJECT], PROJECT);
  const fixture = mountDashboard();
  fixture.componentRef.setInput('counts', rows[0].laneCounts);
  fixture.componentRef.setInput('projectName', PROJECT);
  fixture.componentRef.setInput('view', 'dots');
  fixture.detectChanges();

  const root: HTMLElement = fixture.nativeElement;
  return root.querySelectorAll('[data-lane]').length;
}

describe('Explorer lane dots mirror the visible board lanes', () => {
  // Operator sighting 2026-08-23 (Token Economy): the tree showed a green lane
  // dot on "Board" while every board lane read 0. The dot counted TE-8, an EPIC
  // parked in 5-human-review; the board hides epics because they are containers
  // with their own Epics view. Tree and board now share `excludeEpics`.
  it('draws no dot for a project whose only card is an epic in human review', () => {
    const grouped = emptyGrouped();
    grouped.humanReview = [epic('TE-8', '5-human-review')];

    const rows = buildProjectSidebarRows(grouped, [PROJECT], PROJECT);

    expect(visibleBoardLaneCount(grouped)).toBe(0);
    expect(rows[0].laneCounts).toEqual({ ready: 0, progress: 0, humanReview: 0 });
    expect(rows[0].totalJobs).toBe(0);
    expect(renderedDotCount(grouped)).toBe(0);
  });

  it('dot count equals the visible board lane count for a mixed epic/task fixture', () => {
    const grouped = emptyGrouped();
    grouped.ready = [task('TE-1', '2-ready'), epic('TE-9', '2-ready')];
    grouped.progress = [task('TE-2', '3-progress')];
    grouped.humanReview = [epic('TE-8', '5-human-review'), task('TE-3', '5-human-review')];
    grouped.escalated = [task('TE-4', '5e-escalated')];

    const rows = buildProjectSidebarRows(grouped, [PROJECT], PROJECT);

    // 1 ready + 1 progress + 1 human review + 1 escalated = 4 visible cards;
    // the two epics are drawn by neither surface.
    expect(visibleBoardLaneCount(grouped)).toBe(4);
    expect(rows[0].laneCounts).toEqual({ ready: 1, progress: 1, humanReview: 2 });
    expect(renderedDotCount(grouped)).toBe(visibleBoardLaneCount(grouped));
  });

  it('keeps the aggregate equal to the sum of the visible dots (R3)', () => {
    const grouped = emptyGrouped();
    grouped.ready = [task('TE-1', '2-ready'), epic('TE-9', '2-ready')];
    grouped.humanReview = [epic('TE-8', '5-human-review'), task('TE-3', '5-human-review')];

    const row = buildProjectSidebarRows(grouped, [PROJECT], PROJECT)[0];
    const { ready, progress, humanReview } = row.laneCounts;

    expect(row.totalJobs).toBe(ready + progress + humanReview);
    expect(row.totalJobs).toBe(visibleBoardLaneCount(grouped));
  });

  it('does not count stalled or unsatisfiable Ready cards as pullable work', () => {
    const grouped = emptyGrouped();
    grouped.ready = [
      task('TE-1', '2-ready'),
      task('TE-2', '2-ready', { pickupHold: { classification: 'stalled' } as TaskInfo['pickupHold'] }),
      task('TE-3', '2-ready', { pickupHold: { classification: 'unsatisfiable' } as TaskInfo['pickupHold'] }),
    ];

    const row = buildProjectSidebarRows(grouped, [PROJECT], PROJECT)[0];

    expect(row.laneCounts.ready).toBe(1);
    expect(row.totalJobs).toBe(1);
  });
});
