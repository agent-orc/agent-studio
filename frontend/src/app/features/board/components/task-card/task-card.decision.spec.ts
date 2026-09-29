import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskCardComponent } from './task-card.component';
import { buildVisibleDependencyChip } from './task-card-view-model';
import { ClientService } from '../../../../services/client.service';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { TaskSelectionService } from '../../../task-detail/runtime';
import type { ClientSummary, DecisionContent, TaskInfo } from '../../../../models/task.model';

/**
 * AGT-2795: a decision card reads as a decision on the board (kind badge plus
 * the decider's name, separate from the tag chips), and a card waiting on a
 * pending decision says "blocked by AGT-nnnn" with a link to it.
 */
describe('TaskCardComponent decision cards (AGT-2795)', () => {
  async function mount(job: TaskInfo) {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    TestBed.inject(ClientService).clients.set([
      { id: 'reviewer-7', displayName: 'Robin Reviewer', kind: 'human' } as ClientSummary,
    ]);
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();
    return fixture;
  }

  const q = (fixture: { nativeElement: HTMLElement }, id: string) =>
    fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('shows the Decision kind badge and the resolved decider name on a pending decision', async () => {
    const fixture = await mount(makeJob({
      kind: 'decision',
      state: '1-preparation',
      tags: ['area:release'],
      decision: decision({ decider: 'reviewer-7' }),
    }));
    const badge = q(fixture, 'task-card-decision-badge');
    expect(badge?.textContent?.trim()).toBe('Decision');
    expect(badge?.getAttribute('data-open')).toBe('true');
    expect(q(fixture, 'task-card-decider')?.textContent).toContain('Decider: Robin Reviewer');
    // The kind badge sits in the title, not among the tag chips.
    expect(badge?.closest('[data-testid="task-card-title"]')).not.toBeNull();
  });

  it('calms to a Decided badge naming who decided once settled', async () => {
    const fixture = await mount(makeJob({
      kind: 'decision',
      state: '6-completed',
      decision: decision({ status: 'decided', chosenOptionId: 'a', decidedBy: 'operator' }),
    }));
    const badge = q(fixture, 'task-card-decision-badge');
    expect(badge?.textContent?.trim()).toBe('Decided');
    expect(badge?.getAttribute('data-open')).toBe('false');
    expect(q(fixture, 'task-card-decider')?.textContent).toContain('Decided by: Operator');
  });

  it('renders no decision badge on an ordinary task', async () => {
    const fixture = await mount(makeJob({ kind: 'task' }));
    expect(q(fixture, 'task-card-decision-badge')).toBeNull();
    expect(q(fixture, 'task-card-decider')).toBeNull();
  });

  it('shows "blocked by AGT-nnnn" on a dependant and opens the decision on click', async () => {
    const target = makeJob({ id: 'decide-1', taskKey: 'test::decide-1', key: 'AGT-2792', kind: 'decision', state: '1-preparation', decision: decision() });
    const fixture = await mount(makeJob({
      state: '1-preparation',
      blockedBy: ['AGT-2792'],
      waitsOn: {
        blocked: true,
        cycleDetected: false,
        items: [{ key: 'AGT-2792', resolved: true, fulfilled: false, pendingDecision: true, targetJobId: 'decide-1', targetWatchPath: '/tmp/watch' }],
      },
    }));
    const chip = q(fixture, 'task-card-blocked-by');
    expect(chip?.tagName).toBe('BUTTON');
    expect(chip?.textContent?.trim()).toBe('blocked by AGT-2792');

    TestBed.inject(TaskService).jobs.set([target]);
    const openDetail = vi.spyOn(TestBed.inject(TaskSelectionService), 'openDetail').mockImplementation(() => undefined);
    chip!.click();
    expect(openDetail).toHaveBeenCalledWith(expect.objectContaining({ id: 'decide-1' }));
  });

  it('keeps Pick next from queueing a blocked Ready card and explains why', async () => {
    const fixture = await mount(makeJob({ state: '2-ready', blockedBy: ['AGT-2792'] }));
    const info = vi.spyOn(TestBed.inject(NotificationService), 'info').mockReturnValue(0);
    const emitted = vi.fn();
    fixture.componentInstance.pickNextRequested.subscribe(emitted);
    const pickNext = q(fixture, 'task-card-pick-next');
    expect(pickNext?.getAttribute('aria-disabled')).toBe('true');
    pickNext!.click();
    expect(emitted).not.toHaveBeenCalled();
    expect(info).toHaveBeenCalledWith(expect.stringContaining('Blocked by pending decision AGT-2792'));
  });
});

function decision(overrides: Partial<DecisionContent> = {}): DecisionContent {
  return {
    question: 'Keep the lock file for the Stable release contract?',
    options: [
      { id: 'a', label: 'Lock file back' },
      { id: 'b', label: 'Identity without lock file' },
    ],
    decider: 'operator',
    status: 'pending',
    dependants: ['AGT-2793'],
    ...overrides,
  };
}

function makeJob(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'AGT-2793',
    title: 'Task 1',
    state: '3-progress',
    order: 1,
    agent: 'codex',
    createdAt: '2026-09-13T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-09-13T09:30:00Z',
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
  } as TaskInfo;
}

describe('buildVisibleDependencyChip with pending decision edges (AGT-2795)', () => {
  it('leaves a pending decision edge to the blocked-by chip and keeps other edges', () => {
    const decisionEdge = { key: 'AGT-2792', resolved: true, fulfilled: false, pendingDecision: true };
    const otherEdge = { key: 'AGT-2800', resolved: true, fulfilled: false };
    const only = { blocked: true, cycleDetected: false, items: [decisionEdge] };
    expect(buildVisibleDependencyChip({ waitsOn: only } as TaskInfo)).toBeNull();
    const mixed = { blocked: true, cycleDetected: false, items: [decisionEdge, otherEdge] };
    expect(buildVisibleDependencyChip({ waitsOn: mixed } as TaskInfo)?.label).toBe('waits for completion: AGT-2800');
  });
});
