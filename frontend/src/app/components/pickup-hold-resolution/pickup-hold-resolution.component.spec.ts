import { describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import type { PickupHoldResolution } from '../../models/task.model';
import type { TaskInfo } from '../../models/task.model';
import type { RemedyAction } from '../../models/remedy-action.model';
import { TaskService } from '../../services/task.service';
import { PickupHoldResolutionComponent, controlFor } from './pickup-hold-resolution.component';

const navigation = { canOpen: (action: RemedyAction | null | undefined) => action?.target !== 'unknown-route' };
const way = (kind: string, action: RemedyAction | null, patch: Partial<PickupHoldResolution> = {}): PickupHoldResolution =>
  ({ kind, label: kind, detail: '', action, ...patch });

// Rendering is covered through PickupHoldComponent; this pins the control rule.
describe('controlFor', () => {
  it('maps each typed action to its control', () => {
    const api = (target: string): RemedyAction => ({ kind: 'api', target, project: 'demo' });
    expect(controlFor(api('set-execution-runner'), way('route-to-remote-runner', null), navigation)).toBe('runner-picker');
    expect(controlFor(api('set-cli-type'), way('assign-agent', null), navigation)).toBe('cli-picker');
    expect(controlFor(api('start-run'), way('wait-out-backoff', null), navigation)).toBe('api');
    expect(controlFor({ kind: 'setting', target: 'execution-runner', project: 'demo' }, way('x', null), navigation)).toBe('link');
  });

  it('keeps a malformed, unknown, or missing action as explanatory text', () => {
    const edit: RemedyAction = { kind: 'api', target: 'edit-waits-on' };
    expect(controlFor(null, way('drop-dependency', null), navigation)).toBe('none');
    expect(controlFor(edit, way('drop-dependency', edit), navigation)).toBe('none');
    expect(controlFor(edit, way('drop-cycle-edge', edit, { targetKey: 'APP-2' }), navigation)).toBe('none');
    expect(controlFor({ kind: 'api', target: 'format-disk' }, way('x', null), navigation)).toBe('none');
    expect(controlFor({ kind: 'route', target: 'unknown-route' }, way('x', null), navigation)).toBe('none');
    expect(controlFor({ kind: 'shell', target: 'PUT /api/x' }, way('x', null), navigation)).toBe('none');
  });
});

describe('PickupHoldResolutionComponent intake verdict', () => {
  it('reports a non-Pass verdict and preserves server errors', async () => {
    const response = new Subject<{ outcome: string; reason: string; details: string }>();
    await TestBed.configureTestingModule({
      imports: [PickupHoldResolutionComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const runTaskIntake = vi.spyOn(TestBed.inject(TaskService), 'runTaskIntake')
      .mockReturnValue(response.asObservable());
    const fixture = TestBed.createComponent(PickupHoldResolutionComponent);
    fixture.componentRef.setInput('resolution', way('pass-intake', { kind: 'api', target: 'run-intake' }));
    fixture.componentRef.setInput('task', { id: 'task-1', watchPath: '/tmp/demo', projectName: 'demo' } as TaskInfo);
    fixture.detectChanges();
    fixture.componentInstance.apply();
    response.next({ outcome: 'Hold', reason: 'Dependency is blocked.', details: '' });
    expect(fixture.componentInstance.notice()).toBeNull();
    expect(fixture.componentInstance.error()).toBe('Hold: Dependency is blocked.');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]')?.textContent)
      .toContain('Hold: Dependency is blocked.');

    const failed = new Subject<{ outcome: string; reason: string; details: string }>();
    runTaskIntake.mockReturnValueOnce(failed.asObservable());
    fixture.componentInstance.apply();
    failed.error({ error: { error: 'Intake runner unavailable.' } });
    expect(fixture.componentInstance.error()).toBe('Intake runner unavailable.');
  });
});
