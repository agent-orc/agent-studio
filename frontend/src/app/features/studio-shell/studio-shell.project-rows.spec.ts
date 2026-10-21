import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
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
    ...over,
  } as TaskInfo;
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
 * the Explorer dashboard mirrors.
 */
function visibleBoardLaneCount(grouped: GroupedJobs): number {
  return (
    grouped.ready.length +
    grouped.progress.length +
    grouped.humanReview.length +
    grouped.escalated.length
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

describe('Explorer lane dots mirror the visible board lanes', () => {
  it('keeps the aggregate equal to the sum of the visible dots (R3)', () => {
    const grouped = emptyGrouped();
    grouped.ready = [task('TE-1', '2-ready'), task('TE-9', '2-ready')];
    grouped.humanReview = [task('TE-8', '5-human-review'), task('TE-3', '5-human-review')];

    const row = buildProjectSidebarRows(grouped, [PROJECT], PROJECT)[0];
    const { ready, progress, humanReview } = row.laneCounts;

    expect(row.totalJobs).toBe(ready + progress + humanReview);
    expect(row.totalJobs).toBe(visibleBoardLaneCount(grouped));
  });

  // AGT-2795: open decision cards wait in Preparation for a person. The Board
  // row shows them as their own counter and the project number includes them,
  // so the aggregate still equals the sum of its visible children.
  it('counts open decision cards as a visible fourth counter included in the total', () => {
    const grouped = emptyGrouped();
    const pending = { question: 'q', options: [], decider: 'operator', status: 'pending' as const };
    grouped.preparation = [
      task('TE-20', '1-preparation', { kind: 'decision', decision: pending }),
      task('TE-21', '1-preparation'),
    ];
    grouped.completed = [task('TE-22', '6-completed', { kind: 'decision', decision: { ...pending, status: 'decided' } })];
    grouped.ready = [task('TE-1', '2-ready')];

    const row = buildProjectSidebarRows(grouped, [PROJECT], PROJECT)[0];
    expect(row.laneCounts).toEqual({ ready: 1, progress: 0, humanReview: 0, decisions: 1 });
    expect(row.totalJobs).toBe(2);

    const fixture = mountDashboard();
    fixture.componentRef.setInput('counts', row.laneCounts);
    fixture.componentRef.setInput('projectName', PROJECT);
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    const counter = root.querySelector(`[data-testid="studio-explorer-project-board-count-decisions-${PROJECT}"]`);
    expect(counter?.textContent?.trim()).toBe('1');
    expect(root.querySelector('[aria-label]')?.getAttribute('aria-label')).toContain('1 open decision');
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
