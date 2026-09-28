import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TaskDetailLoadSectionsComponent } from './task-detail-load-sections.component';

describe('TaskDetailLoadSectionsComponent', () => {
  let fixture: ComponentFixture<TaskDetailLoadSectionsComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskDetailLoadSectionsComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskDetailLoadSectionsComponent);
    fixture.componentRef.setInput('info', {
      id: 'task', key: 'AGT-2577', taskKey: 'watch::task', title: 'Heavy task',
      projectName: 'fixture', state: '5-human-review',
    });
    fixture.detectChanges();
  });

  it('renders an independently identified skeleton for every detail section', () => {
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-detail-head"]')?.textContent).toContain('AGT-2577');
    expect(root.querySelectorAll('[data-testid^="task-detail-section-"][aria-busy="true"]')).toHaveLength(3);
    expect(root.querySelector('[data-testid="task-detail-section-context"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="task-detail-section-activity"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="task-detail-section-evidence"]')).not.toBeNull();
  });

  it('renders every bounded core field before enrichment is ready', () => {
    fixture.componentRef.setInput('core', {
      state: 'ready', projectId: 'fixture', id: 'task', taskKey: 'watch::task',
      key: 'AGT-2577', title: 'Heavy task', lane: '5-human-review',
      taskType: 'chore', mode: 'coding', order: 2, coreVersion: 7,
      blocking: { dependencyBlocked: false },
      pins: { model: 'pinned-model', modelExplicit: true, thinkingLevel: null,
        thinkingLevelExplicit: false, cliType: 'codex' },
      runtime: { executionStatus: 'running', location: 'runner', runnerName: 'runner-1',
        attemptId: 'attempt-1', heartbeatAt: null },
      statusSummary: { text: 'Status head' }, prompt: { text: 'Prompt head', cursor: null },
      timeline: { events: [{ sequence: 1, summary: 'Recent work' }] },
    });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    for (const id of ['identity', 'state', 'pins', 'execution', 'status', 'prompt', 'timeline'])
      expect(root.querySelector(`[data-testid="task-core-${id}"]`)).not.toBeNull();
    expect(root.querySelector('[data-testid="task-core"]')?.textContent)
      .toContain('Recent work');
    expect(root.querySelector('[data-testid="task-core"]')?.textContent)
      .toContain('Status head');
    expect(root.querySelector('[data-testid="task-core"]')?.textContent)
      .toContain('Prompt head');
  });

  it('keeps each section visible with a retry action after failure', () => {
    const retried = vi.fn();
    fixture.componentInstance.retry.subscribe(retried);
    fixture.componentRef.setInput('errorMessage', 'The detail request timed out.');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(3);
    root.querySelector<HTMLButtonElement>('[data-testid="task-detail-section-retry-activity"]')?.click();
    expect(retried).toHaveBeenCalledOnce();
  });
});
