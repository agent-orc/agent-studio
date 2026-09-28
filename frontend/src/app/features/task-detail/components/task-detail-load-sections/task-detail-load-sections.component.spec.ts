import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TaskDetailLoadSectionsComponent } from './task-detail-load-sections.component';
import { seedTaskCore, type TaskCore, type TaskCoreView } from '../../../../models/task-core.model';
import type { TaskInfo } from '../../../../models/task.model';

const INFO = {
  id: 'task', key: 'AGT-2577', taskKey: 'watch::task', title: 'Heavy task',
  projectName: 'fixture', state: '5-human-review', order: 0, model: 'opus', modelExplicit: true,
  cliType: 'claude', watchPath: 'watch',
} as unknown as TaskInfo;

function coreOf(overrides: Partial<TaskCore> = {}): TaskCore {
  return {
    state: 'ready', projectId: 'PROJ-F', projectName: 'fixture', id: 'task', taskKey: 'watch::task',
    title: 'Heavy task', kind: 'task', taskType: 'chore', lane: '5-human-review', enteredLaneAt: '2026-09-28T10:00:00Z',
    order: 0, mode: 'coding', released: false, pendingIntent: false,
    pins: { modelExplicit: true, thinkingLevelExplicit: false, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
    runtime: { location: 'none', leaseState: 'none' }, runtimeVersion: 'r0',
    statusSummary: { state: 'ready', text: 'Waiting for <review>', originalBytes: 20 },
    prompt: { state: 'ready', text: 'Make the switch fast', originalBytes: 20, cursor: 'c1' },
    timeline: {
      state: 'ready', originalBytes: 60,
      events: [{ sequence: 7, ts: '2026-09-28T10:00:00Z', kind: 'moved', actor: 'operator', summary: 'Moved to review' }],
    },
    coreVersion: 3,
    ...overrides,
  };
}

function viewOf(state: TaskCoreView['state'], core: TaskCore | null, info: TaskInfo = INFO): TaskCoreView {
  return { seed: seedTaskCore(info, 'PROJ-F'), core, state };
}

describe('TaskDetailLoadSectionsComponent', () => {
  let fixture: ComponentFixture<TaskDetailLoadSectionsComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskDetailLoadSectionsComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskDetailLoadSectionsComponent);
    fixture.componentRef.setInput('info', INFO);
    fixture.detectChanges();
  });

  it('renders an independently identified skeleton for every detail section', () => {
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-detail-head"]')?.textContent).toContain('AGT-2577');
    expect(root.querySelectorAll('[aria-busy="true"]')).toHaveLength(3);
    expect(root.querySelector('[data-testid="task-detail-section-context"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="task-detail-section-activity"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="task-detail-section-evidence"]')).not.toBeNull();
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
  describe('with the task core (AGT-2956)', () => {
    const root = () => fixture.nativeElement as HTMLElement;
    const text = (testId: string) => root().querySelector(`[data-testid="${testId}"]`)?.textContent ?? null;

    it('paints board facts from the seed at once and keeps the heads loading', () => {
      fixture.componentRef.setInput('core', viewOf('seeded', null));
      fixture.detectChanges();
      expect(text('task-core-pins')).toContain('opus');
      expect(text('task-core-pins')).toContain('pinned');
      expect(root().querySelector('[data-testid="task-core-prompt"] [role="status"]')).not.toBeNull();
      expect(root().querySelector('[data-testid="task-detail-load-sections"]')?.getAttribute('data-core-state')).toBe('seeded');
    });

    it('paints the bounded heads as escaped text before the full detail', () => {
      fixture.componentRef.setInput('core', viewOf('ready', coreOf()));
      fixture.detectChanges();
      expect(text('task-core-prompt')).toContain('Make the switch fast');
      expect(text('task-core-prompt')).toContain('Shortened');
      expect(text('task-core-status')).toContain('Waiting for <review>');
      expect(root().querySelector('[data-testid="task-core-status"] review')).toBeNull();
      expect(text('task-core-timeline')).toContain('Moved to review');
      expect(root().querySelector('[data-testid="task-detail-section-context"]')?.getAttribute('aria-busy')).toBe('false');
      expect(root().querySelector('[data-testid="task-detail-section-activity"]')?.getAttribute('aria-busy')).toBe('false');
      // Git evidence is not core: it keeps waiting for its own resource.
      expect(root().querySelector('[data-testid="task-detail-section-evidence"]')?.getAttribute('aria-busy')).toBe('true');
    });

    it('renders explicit empty states for empty heads', () => {
      fixture.componentRef.setInput('core', viewOf('ready', coreOf({
        statusSummary: { state: 'missing', originalBytes: 0 },
        prompt: { state: 'missing', originalBytes: 0 },
        timeline: { state: 'missing', events: [], originalBytes: 0 },
      })));
      fixture.detectChanges();
      expect(text('task-core-prompt')).toContain('No prompt yet.');
      expect(text('task-core-status')).toContain('No status summary yet.');
      expect(text('task-core-timeline')).toContain('No events yet.');
    });

    it('never shows a core that belongs to another task', () => {
      const other = { ...INFO, id: 'other', taskKey: 'watch::other' } as TaskInfo;
      fixture.componentRef.setInput('core', viewOf('ready', coreOf(), other));
      fixture.detectChanges();
      expect(root().querySelector('[data-testid="task-core-prompt"]')).toBeNull();
      expect(root().querySelectorAll('[aria-busy="true"]')).toHaveLength(3);
    });

    it('a failed full-detail request keeps painted core and offers retry for Git evidence', () => {
      fixture.componentRef.setInput('core', viewOf('ready', coreOf()));
      fixture.componentRef.setInput('errorMessage', 'The detail request timed out.');
      fixture.detectChanges();
      expect(text('task-core-prompt')).toContain('Make the switch fast');
      expect(root().querySelectorAll('[role="alert"]')).toHaveLength(1);
      expect(root().querySelector('[data-testid="task-detail-section-retry-evidence"]')).not.toBeNull();
    });

    it('denied access shows no core content', () => {
      fixture.componentRef.setInput('core', viewOf('denied', null));
      fixture.detectChanges();
      expect(text('task-core-notice')).toContain('no longer have access');
      expect(root().querySelector('[data-testid="task-core-pins"]')).toBeNull();
      expect(root().querySelector('[data-testid="task-core-prompt"]')).toBeNull();
    });
  });
});
