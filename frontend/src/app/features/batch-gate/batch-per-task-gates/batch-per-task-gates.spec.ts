import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import type { BatchGateState } from '../../../models/batch-gate.model';
import { BatchPerTaskGatesComponent } from './batch-per-task-gates';

describe('BatchPerTaskGatesComponent', () => {
  async function render(gates: BatchGateState['perTaskGates']) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(BatchPerTaskGatesComponent);
    fixture.componentRef.setInput('gates', gates);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows a queued gate with its position and why no start estimate exists', async () => {
    const host = await render([{ taskKey: 'AGT-2', reviewAttemptId: 'ra-2', position: 2, total: 4, running: false,
      host: null, startedAtUtc: null, lastProgressAtUtc: '2026-11-16T10:00:00Z', slotCapacity: 3,
      startEstimateUnavailableReason: 'Full-suite duration is unknown.', reason: 'batch paused for a fresh gate' }]);
    const row = host.querySelector('[data-testid="batch-per-task-gate"]')!.textContent!.replace(/\s+/g, ' ');
    expect(row).toContain('AGT-2 · 2 of 4 · Queued');
    expect(row).toContain('Start estimate unavailable: Full-suite duration is unknown.');
  });

  it('renders nothing while no per-task gate exists', async () => {
    const host = await render([]);
    expect(host.querySelector('[data-testid="batch-per-task-gates"]')).toBeNull();
  });
});
