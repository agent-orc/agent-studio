import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { GateQueueReleaseComponent } from './gate-queue-release.component';

const QUEUE_REASON =
  'machine build/test gate queue wait violated machine-gate-queue budget (limit=9327000ms, consumed=9327018ms, phase=queue)';

function create(reason: string, state: string = TaskState.Escalated) {
  TestBed.configureTestingModule({
    imports: [GateQueueReleaseComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(GateQueueReleaseComponent);
  fixture.componentRef.setInput('info', {
    id: 'AGT-3213',
    state,
    projectName: 'Demo',
    watchPath: '/test/project',
    parkedBlocker: { blockerType: 'operator-decision', reason },
  } as unknown as TaskInfo);
  fixture.detectChanges();
  return { fixture, http: TestBed.inject(HttpTestingController) };
}

describe('GateQueueReleaseComponent', () => {
  it('releases every queue-stalled card of the project with one action', () => {
    const { fixture, http } = create(QUEUE_REASON);
    http.expectOne('/api/projects/Demo/batch-gate/queue-stalls')
      .flush({ project: 'Demo', releasable: ['AGT-3213', 'AGT-3214', 'AGT-3215'] });
    fixture.detectChanges();

    const action = fixture.nativeElement.querySelector('[data-testid="gate-queue-release-action"]') as HTMLButtonElement;
    expect(action.textContent).toContain('Release 3 to the gate queue');
    action.click();
    const release = http.expectOne('/api/projects/Demo/batch-gate/queue-stalls/release');
    expect(release.request.method).toBe('POST');
    release.flush({ released: ['AGT-3213', 'AGT-3214'], refused: [{ taskKey: 'AGT-3215', reason: 'The lane move failed.' }] });
    http.expectOne('/api/projects/Demo/batch-gate/queue-stalls').flush({ project: 'Demo', releasable: [] });
    fixture.detectChanges();

    const outcome = fixture.nativeElement.querySelector('[data-testid="gate-queue-release-outcome"]') as HTMLElement;
    expect(outcome.textContent).toContain('Released 2 cards to the gate queue; 1 stayed escalated: AGT-3215');
    expect(fixture.nativeElement.querySelector('[data-testid="gate-queue-release-action"]')).toBeNull();
    http.verify();
  });

  it('stays hidden for an escalation that is not a queue overrun', () => {
    const { fixture, http } = create('deterministic-suite-red');

    http.expectNone('/api/projects/Demo/batch-gate/queue-stalls');
    expect(fixture.nativeElement.querySelector('[data-testid="gate-queue-release"]')).toBeNull();
  });
});
