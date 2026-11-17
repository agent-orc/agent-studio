import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import type { BatchGateState } from '../../../models/batch-gate.model';
import { BatchLaneAdmissionComponent } from './batch-lane-admission';

describe('BatchLaneAdmissionComponent', () => {
  async function render(lane: BatchGateState['laneAdmission'], refusals: BatchGateState['suiteRefusals']) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(BatchLaneAdmissionComponent);
    fixture.componentRef.setInput('lane', lane);
    fixture.componentRef.setInput('refusals', refusals);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the waiting suite and the refused per-card suite with their reasons', async () => {
    const host = await render(
      { batchGates: 1, companionSuites: 1, limit: 1, waiting: [{ runId: 'run-9', gateId: 'gate-9', cards: ['WEB-40'],
        sinceUtc: '2026-11-16T10:00:00Z', reason: 'one other full suite already shares the gate host' }] },
      [{ taskKey: 'AGT-2', reviewAttemptId: 'ra-2', rule: 'batch-partner', reason: 'AGT-2 is a member of batch-7',
        refusedAtUtc: '2026-11-16T10:01:00Z' }]);
    expect(host.querySelector('[data-testid="batch-lane-waiting"]')!.textContent).toContain('one other full suite already shares the gate host');
    expect(host.querySelector('[data-testid="batch-suite-refusal"]')!.textContent).toContain('AGT-2 is a member of batch-7');
  });

  it('renders nothing while no suite waits and none was refused', async () => {
    const host = await render({ batchGates: 0, companionSuites: 0, limit: 1, waiting: [] }, []);
    expect(host.querySelector('[data-testid="batch-lane-admission"]')).toBeNull();
  });
});
