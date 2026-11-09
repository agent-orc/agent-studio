import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { BatchGateState } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { isolatedRedState, redHalvingState } from '../testing/batch-fixtures';
import { BatchLaneIndicatorComponent } from './batch-lane-indicator';

describe('BatchLaneIndicatorComponent', () => {
  it('counts unfinished runs and hides disabled or unavailable projects', async () => {
    const state = signal<BatchGateState | null>(redHalvingState);
    const watch = vi.fn(() => vi.fn());
    await TestBed.configureTestingModule({ imports: [BatchLaneIndicatorComponent], providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService, useValue: { state: () => state.asReadonly(), watch },
    }] }).compileComponents();
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
  it('names the red or isolating batch of the project', async () => {
    const state = signal<BatchGateState | null>(isolatedRedState);
    await TestBed.configureTestingModule({ imports: [BatchLaneIndicatorComponent], providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService, useValue: { state: () => state.asReadonly(), watch: vi.fn(() => vi.fn()) },
    }] }).compileComponents();
    const fixture = TestBed.createComponent(BatchLaneIndicatorComponent);
    fixture.componentRef.setInput('laneState', '4-auto-review');
    fixture.componentRef.setInput('projectScope', 'Demo');
    await fixture.whenStable();
    const button = () => fixture.nativeElement.querySelector('[data-testid="batch-lane-indicator"]') as HTMLElement;
    const alert = () => button().querySelector('[data-testid="batch-lane-alert"]') as HTMLElement | null;
    expect(button().dataset['batchAlert']).toBe('red');
    expect(button().dataset['batchId']).toBe('batch-14');
    expect(alert()!.dataset['tone']).toBe('danger');
    expect(alert()!.textContent!.trim()).toBe('Batch batch-14 red');
    expect(button().textContent).toContain('AGT-2 proven red');
    state.set(redHalvingState);
    await fixture.whenStable();
    expect(button().dataset['batchAlert']).toBe('isolating');
    expect(alert()!.dataset['tone']).toBe('running');
    expect(alert()!.textContent!.trim()).toBe('Batch batch-7 isolating');
    state.set({ ...redHalvingState, batches: [] });
    await fixture.whenStable();
    expect(button().dataset['batchAlert']).toBeUndefined();
    expect(alert()).toBeNull();
  });
});
