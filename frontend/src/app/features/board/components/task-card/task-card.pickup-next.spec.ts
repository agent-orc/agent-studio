import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskCardComponent } from './task-card.component';
import { TaskService } from '../../../../services/task.service';
import type { ProjectRunnerStatus, TaskInfo } from '../../../../models/task.model';

/**
 * AGT-3587: every Ready card used to carry a "Next" control, so the board
 * claimed every card ran next. Only the head of the project's pickup queue
 * (runner status `queuedJobIds[0]`) says "Next" now; the others offer
 * "Move to top".
 */
describe('TaskCardComponent Ready queue marker (AGT-3587)', () => {
  async function mount(job: TaskInfo, queuedJobIds: string[]) {
    await TestBed.configureTestingModule({
      imports: [TaskCardComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    TestBed.inject(TaskService).runnerStatus.set({
      projects: { Test: { projectName: 'Test', queuedJobIds } as ProjectRunnerStatus },
    });
    const fixture = TestBed.createComponent(TaskCardComponent);
    fixture.componentRef.setInput('job', job);
    fixture.detectChanges();
    return fixture;
  }

  const q = (fixture: { nativeElement: HTMLElement }, id: string) =>
    fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('marks the queue head as Next and offers no move control on it', async () => {
    const fixture = await mount(makeJob('task-1'), ['task-1', 'task-2']);

    expect(q(fixture, 'task-card-pickup-next')?.textContent?.trim()).toBe('Next');
    expect(q(fixture, 'task-card-pick-next')).toBeNull();
  });

  it('offers Move to top, not Next, on a queued card behind the head', async () => {
    const fixture = await mount(makeJob('task-2'), ['task-1', 'task-2']);

    expect(q(fixture, 'task-card-pickup-next')).toBeNull();
    const move = q(fixture, 'task-card-pick-next');
    expect(move?.getAttribute('aria-label')).toBe('Move to top');
    expect(move?.textContent).toContain('Move to top');
    expect(move?.textContent).not.toContain('Next');
  });
});

function makeJob(id: string): TaskInfo {
  return {
    id,
    taskKey: `test::${id}`,
    key: id.toUpperCase(),
    title: id,
    state: '2-ready',
    order: 1,
    agent: 'codex',
    createdAt: '2026-11-01T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Test',
    folderPath: `/tmp/watch/2-ready/${id}`,
    lastActivity: '2026-11-01T09:00:00Z',
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
  } as TaskInfo;
}
