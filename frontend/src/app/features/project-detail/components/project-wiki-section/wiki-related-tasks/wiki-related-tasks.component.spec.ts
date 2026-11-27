import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';
import { WikiRelatedTasksComponent } from './wiki-related-tasks.component';
import { TaskService } from '../../../../../services/task.service';
import { TaskReferenceNavigationService } from '../../../../../services/task-reference-navigation.service';
import { RelatedTaskReference } from '../../../../../models/project-docs.model';
import { TaskReferenceStatus } from '../../../../../components/task-reference-microcard/task-reference-microcard';

const liveStatus = (key: string): TaskReferenceStatus => ({
  key,
  exists: true,
  taskKey: `PROJ-001::${key.toLowerCase()}`,
  title: `${key} title`,
  lane: '6-completed',
  projectId: 'PROJ-001',
  projectName: 'Agent Studio',
  projectColor: '#a78bfa',
  merge: null,
  reviewGrade: null,
});

const ref = (key: string, title = `${key} title`, exists: boolean | null = true): RelatedTaskReference => ({
  key,
  title,
  linkedAt: '2026-07-10T00:00:00Z',
  source: 'auto',
  exists,
});

async function mount(related: RelatedTaskReference[], getReferenceStatuses: () => unknown) {
  await TestBed.configureTestingModule({
    imports: [WikiRelatedTasksComponent],
    providers: [
      { provide: TaskService, useValue: { getReferenceStatuses } },
      { provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(WikiRelatedTasksComponent);
  fixture.componentRef.setInput('related', related);
  // First pass runs the resolve effect (synchronous `of(...)`); second flushes
  // the hydrated `statuses()` into the micro-card list.
  fixture.detectChanges();
  fixture.detectChanges();
  return fixture;
}

describe('WikiRelatedTasksComponent', () => {
  it('renders each related task as the AGT-2050 micro-card, hydrated live', async () => {
    const getReferenceStatuses = vi.fn(() => of([liveStatus('AGT-2050')]));
    const fixture = await mount([ref('AGT-2050')], getReferenceStatuses);

    expect(getReferenceStatuses).toHaveBeenCalledWith(['AGT-2050']);
    const cards = fixture.nativeElement.querySelectorAll('[data-testid="task-reference-microcard"]');
    expect(cards).toHaveLength(1);
    // Live target => the micro-card's clickable anchor, not a ghost.
    expect(fixture.nativeElement.querySelector('a')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.task-ref--ghost')).toBeNull();
  });

  it('renders a dropped/deleted target as a deletion-tolerant ghost carrying its stored title', async () => {
    // Registry no longer resolves the key: the batch returns it as neither live
    // nor ghost (dropped). The stored reference must still render.
    const getReferenceStatuses = vi.fn(() => of([]));
    const fixture = await mount([ref('GONE-9', 'Retired page task', false)], getReferenceStatuses);

    const ghost = fixture.nativeElement.querySelector('.task-ref--ghost');
    expect(ghost).toBeTruthy();
    expect(fixture.nativeElement.querySelector('a')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('GONE-9');
    const tooltip = fixture.debugElement.query(By.directive(AppTooltipDirective))
      .injector.get(AppTooltipDirective);
    expect(tooltip.appTooltip()).toContain('Retired page task');
  });

  it('preserves stored order and shows every reference (live and ghost together)', async () => {
    const getReferenceStatuses = vi.fn(() => of([liveStatus('AGT-2050')]));
    const fixture = await mount([ref('AGT-2050'), ref('GONE-9', 'Retired', false)], getReferenceStatuses);

    const cards = fixture.nativeElement.querySelectorAll('[data-testid="task-reference-microcard"]');
    expect(cards).toHaveLength(2);
    expect(fixture.nativeElement.querySelectorAll('.task-ref--ghost')).toHaveLength(1);
  });

  it('renders nothing and skips the batch when there are no related tasks', async () => {
    const getReferenceStatuses = vi.fn(() => of([]));
    const fixture = await mount([], getReferenceStatuses);

    expect(getReferenceStatuses).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('[data-testid="wiki-related-tasks"]')).toBeNull();
  });
});
