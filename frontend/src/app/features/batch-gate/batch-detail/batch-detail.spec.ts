import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { BatchGateState } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { redHalvingState } from '../testing/batch-fixtures';
import { BatchDetailComponent } from './batch-detail';

describe('BatchDetailComponent', () => {
  it('uses the task id after the composite and reference keys, then hides disabled state', async () => {
    const state = signal<BatchGateState | null>(redHalvingState);
    const unavailable = signal(false);
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService,
      useValue: { state: () => state.asReadonly(), unavailable: () => unavailable.asReadonly(), watch: () => vi.fn() },
    }] });
    const fixture = TestBed.createComponent(BatchDetailComponent);
    fixture.componentRef.setInput('task', { projectName: 'Demo', taskKey: '/tmp/demo::AGT-2', key: 'OTHER', id: 'AGT-2' } as TaskInfo);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Batch batch-8');
    expect(fixture.nativeElement.textContent).toContain('Phase: running');
    state.set({ ...redHalvingState, plan: { ...redHalvingState.plan, decision: 'disabled' } });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="task-batch-verification"]')).toBeNull();
    state.set(null);
    unavailable.set(true);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="task-batch-verification"]')).toBeNull();
  });
});
