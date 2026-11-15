import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProjectTestRunsPanelComponent, testRunResultTone } from './project-test-runs-panel';

describe('ProjectTestRunsPanelComponent', () => {
  it('shows the ordered lifecycle pipeline and attached cards', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTestRunsPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectTestRunsPanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/projects/Demo/test-runs').flush({
      project: 'Demo',
      headCommit: 'ffffffff',
      runs: [
        item('TR-plan', 'planned', null, 1, [{ taskKey: 'DEM-1', title: 'First' }]),
        item('TR-live', 'running', null, 2, [{ taskKey: 'DEM-2', title: 'Second' }]),
        item('TR-done', 'completed', 'passed', 3, []),
      ],
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="test-run-lane-planned"]').textContent).toContain('TR-plan');
    expect(fixture.nativeElement.querySelector('[data-testid="test-run-lane-running"]').textContent).toContain('TR-live');
    expect(fixture.nativeElement.querySelector('[data-testid="test-run-lane-completed"]').textContent).toContain('TR-done');
    expect(fixture.nativeElement.textContent).toContain('DEM-1');
    expect(fixture.nativeElement.querySelector('[data-testid="test-run-pipeline-summary"]').textContent).toContain('1 planned');
    expect(fixture.nativeElement.querySelector('[data-testid="test-run-planned-order"]').textContent).toContain('#1');
    const result = fixture.nativeElement.querySelector('[data-testid="test-run-result"]') as HTMLElement;
    expect(result.dataset['tone']).toBeTruthy();
  });
});

describe('testRunResultTone', () => {
  it('maps results and states, keeping finished failures non-acute, and falls back to neutral', () => {
    expect(['passed', 'failed', 'canceled', 'running', 'planned', 'completed', 'later'].map(testRunResultTone))
      .toEqual(['success', 'warning', 'neutral', 'running', 'neutral', 'neutral', 'neutral']);
  });
});

function item(id: string, state: string, result: string | null, order: number, attachedTasks: unknown[]) {
  return {
    run: {
      id, projectId: 'PROJ-001', trigger: 'pipeline', commit: `${order}`.repeat(8), branch: 'develop',
      scope: { level: 'project', testSet: 'all' }, state, result, durationSeconds: result ? 12 : null,
      host: state === 'planned' ? null : 'runner-01', plannedOrder: order, createdAt: '2026-07-22T10:00:00Z',
      startedAt: state === 'planned' ? null : '2026-07-22T10:01:00Z', completedAt: result ? '2026-07-22T10:02:00Z' : null,
    },
    attachedTasks,
  };
}
