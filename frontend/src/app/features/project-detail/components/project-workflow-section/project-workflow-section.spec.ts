import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { ProjectWorkflowSectionComponent, switchTone } from './project-workflow-section';
import { TaskService } from '../../../../services/task.service';

/**
 * Render-path spec for the T6a Workflow / Lanes page (stage 1). Stubs
 * TaskService so the read-only surface renders from seeded settings:
 * the combined lane + sort list, the live transition view (auto-commit / attribution /
 * gates / auto-push), and the stage 2/3 placeholders. Also verifies the
 * per-lane sort controls reflect the resolved strategy map.
 */
describe('ProjectWorkflowSectionComponent', () => {
  function mount() {
    const taskServiceStub: Partial<TaskService> = {
      getLaneSortStrategies: () =>
        of({
          resolved: { '3-progress': 'newest-first', '0-backlog': 'oldest-first' },
          overrides: {},
          available: [],
        }),
      setLaneSortStrategy: (_p: string, lane: string, strategy: string) =>
        of({ lane, strategy, override: strategy }),
      getProjectSnapshot: () =>
        of({ settings: { autoCommit: true, autoPushStrategy: 'on-completed' } }) as unknown as ReturnType<
          TaskService['getProjectSnapshot']
        >,
      getPipelineCatalogue: () =>
        of({
          pipelineId: 'p',
          steps: [
            {
              id: 'aspect-requirement-fit',
              displayName: 'Requirement fit',
              kind: 'aspect',
              usesModel: true,
              usesPrompt: true,
              supportsMode: true,
              canDisable: true,
              defaultEnabled: true,
              supportsCondition: false,
            },
            {
              id: 'auto-commit',
              displayName: 'Auto commit',
              kind: 'core',
              usesModel: false,
              usesPrompt: false,
              supportsMode: false,
              canDisable: false,
              defaultEnabled: true,
              supportsCondition: false,
            },
          ],
        }) as unknown as ReturnType<TaskService['getPipelineCatalogue']>,
      getAllProjectSettings: () => of({}) as unknown as ReturnType<TaskService['getAllProjectSettings']>,
    };

    TestBed.configureTestingModule({
      imports: [ProjectWorkflowSectionComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: taskServiceStub },
      ],
    });

    const fixture = TestBed.createComponent(ProjectWorkflowSectionComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    return fixture;
  }

  it('renders the lane list in board order with one combined row per lane', () => {
    const el = mount().nativeElement as HTMLElement;
    const list = el.querySelector('[data-testid="workflow-lane-list"]');
    expect(list).toBeTruthy();
    const rows = Array.from(el.querySelectorAll('[data-testid^="workflow-lane-"]')).filter(
      (n) => n.getAttribute('data-testid') !== 'workflow-lane-list',
    );
    // 9 sortable lanes, each rendered as workflow-lane-<state>.
    expect(rows.length).toBe(9);
  });

  it('drives the read-only transitions from live settings', () => {
    const el = mount().nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="workflow-transition-auto-commit"]')).toBeTruthy();
    expect(
      el.querySelector('[data-testid="workflow-transition-state-auto-commit"]')?.textContent?.trim(),
    ).toBe('On');
    expect(
      el.querySelector('[data-testid="workflow-transition-state-attribution"]')?.textContent?.trim(),
    ).toBe('SHA stamped');
    // One catalogue step supports a gate mode and is enabled by default.
    expect(
      el.querySelector('[data-testid="workflow-transition-state-gates"]')?.textContent?.trim(),
    ).toBe('1 active gate step');
    expect(
      el.querySelector('[data-testid="workflow-transition-state-auto-push"]')?.textContent?.trim(),
    ).toBe('On completed');
    const tones = ['auto-commit', 'attribution', 'gates', 'auto-push'].map(
      (key) => (el.querySelector(`[data-testid="workflow-transition-state-${key}"]`) as HTMLElement | null)?.dataset['tone'],
    );
    expect(tones).toEqual(['success', 'success', 'success', 'success']);
  });

  it('maps an effective transition state to a tone', () => {
    expect([true, false, null, undefined].map(switchTone)).toEqual(['success', 'neutral', 'running', 'running']);
  });

  it('shows per-lane sort controls inside the board-order rows', async () => {
    const fixture = mount();
    // ngModel defers its initial model->view write to a microtask, so wait for
    // the fixture to settle before reading the rendered <select> value.
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="project-detail-lane-sort"]')).toBeTruthy();
    const select = el.querySelector<HTMLSelectElement>(
      '[data-testid="lane-sort-select-3-progress"]',
    );
    expect(select?.value).toBe('newest-first');
  });

  it('keeps the stage 2/3 work as labelled placeholders, not controls', () => {
    const el = mount().nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="workflow-stage2-placeholder"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="workflow-stage3-placeholder"]')).toBeTruthy();
    // Placeholders must not introduce any input controls.
    const stage2 = el.querySelector('[data-testid="workflow-stage2-placeholder"]')!;
    expect(stage2.querySelectorAll('input, select, button').length).toBe(0);
  });
});
