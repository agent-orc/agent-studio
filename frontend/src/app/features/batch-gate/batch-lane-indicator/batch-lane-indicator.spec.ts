import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { BatchGateState } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { redHalvingState } from '../testing/batch-fixtures';
import { BatchLaneIndicatorComponent } from './batch-lane-indicator';

describe('BatchLaneIndicatorComponent', () => {
  it('counts unfinished runs and hides disabled or unavailable projects', async () => {
    const state = signal<BatchGateState | null>(redHalvingState);
    const watch = vi.fn(() => vi.fn());
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService, useValue: { state: () => state.asReadonly(), watch },
    }] });
    const fixture = TestBed.createComponent(BatchLaneIndicatorComponent);
    fixture.componentRef.setInput('laneState', '4-auto-review');
    fixture.componentRef.setInput('jobs', [{ projectName: 'Demo' }] as TaskInfo[]);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('1 running');
    expect(watch).toHaveBeenCalledOnce();
    state.set({ ...redHalvingState, plan: { ...redHalvingState.plan, decision: 'disabled' } });
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="batch-lane-indicator"]')).toBeNull();
    state.set(null);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="batch-lane-indicator"]')).toBeNull();
  });
});
