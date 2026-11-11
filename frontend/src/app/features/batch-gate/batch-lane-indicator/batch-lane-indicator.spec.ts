import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { BatchGateState } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { isolatedRedState, redHalvingState } from '../testing/batch-fixtures';
import { BatchLaneIndicatorComponent } from './batch-lane-indicator';

describe('BatchLaneIndicatorComponent', () => {
  it('counts per-task fallback gates separately from batch waits', async () => {
    const state = signal<BatchGateState | null>({ ...redHalvingState,
      pending: [{ ...redHalvingState.pending[0], taskKey: 'AGT-2', reviewAttemptId: 'r2' },
        { ...redHalvingState.pending[0], taskKey: 'AGT-3', reviewAttemptId: 'r3' }],
      perTaskGates: [{ taskKey: 'AGT-2', reviewAttemptId: 'r2', position: 1, total: 2,
        reason: 'batch b1 paused: fresh gate verdict required', running: false },
        { taskKey: 'AGT-3', reviewAttemptId: 'r3', position: 2, total: 2,
          reason: 'batch b1 paused: fresh gate verdict required', running: true, host: 'gate-01' }],
    });
    await TestBed.configureTestingModule({ imports: [BatchLaneIndicatorComponent], providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService, useValue: { state: () => state.asReadonly(), watch: () => vi.fn() },
    }] }).compileComponents();
    const fixture = TestBed.createComponent(BatchLaneIndicatorComponent);
    fixture.componentRef.setInput('laneState', '4-auto-review');
    fixture.componentRef.setInput('projectScope', 'Demo');
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Batch: 0 waiting');
    expect(fixture.nativeElement.textContent).toContain('Per-task gates: 1 queued · 1 running');
  });
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
  it('counts waiting cards and live runs from current batch membership and phase', async () => {
    const pending = [1, 2, 3, 4].map(index => ({ taskKey: `AGT-${index}`, reviewAttemptId: `r-${index}`,
      resultSha: 'a'.repeat(40), waitMinutes: 229.23, risk: 0.2 }));
    const batch = { ...redHalvingState.batches[1], batchId: 'gate-host-01', phase: 'running',
      members: pending.map(item => ({ taskKey: item.taskKey, replay: 'applied', released: false })),
      runs: [{ batchRunId: 'run-1', host: 'gate-host-01', startedAtUtc: '2026-11-10T09:00:00Z' }] };
    const state = signal<BatchGateState | null>({ ...redHalvingState, pending, batches: [batch], members: {} });
    await TestBed.configureTestingModule({ imports: [BatchLaneIndicatorComponent], providers: [provideZonelessChangeDetection(), {
      provide: BatchGateService, useValue: { state: () => state.asReadonly(), watch: vi.fn(() => vi.fn()) },
    }] }).compileComponents();
    const fixture = TestBed.createComponent(BatchLaneIndicatorComponent);
    fixture.componentRef.setInput('laneState', '4-auto-review');
    fixture.componentRef.setInput('projectScope', 'Demo');
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Batch: 0 waiting');
    expect(fixture.nativeElement.textContent).toContain('1 running');
    for (const phase of ['abandoned', 'red', 'paused', 'published']) {
      state.set({ ...state()!, batches: [{ ...batch, phase }] });
      await fixture.whenStable();
      expect(fixture.nativeElement.textContent).toContain('0 running');
    }
  });
});
