import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { BatchGateState } from '../../../models/batch-gate.model';
import { BatchGateService } from '../../../services/batch-gate.service';
import { isolatedRedState, redHalvingState } from '../testing/batch-fixtures';
import { BatchDetailComponent } from './batch-detail';

async function mount(initial: BatchGateState | null, task: Partial<TaskInfo>) {
  const state = signal<BatchGateState | null>(initial);
  const unavailable = signal(false);
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), {
    provide: BatchGateService,
    useValue: { state: () => state.asReadonly(), unavailable: () => unavailable.asReadonly(), watch: () => vi.fn() },
  }] });
  const fixture = TestBed.createComponent(BatchDetailComponent);
  fixture.componentRef.setInput('task', { projectName: 'Demo', ...task } as TaskInfo);
  await fixture.whenStable();
  return { fixture, state, unavailable, host: fixture.nativeElement as HTMLElement };
}

describe('BatchDetailComponent', () => {
  it('renders status, batch id, position and phase for a member found by task id after the other keys', async () => {
    const { host } = await mount(redHalvingState, { taskKey: '/tmp/demo::AGT-2', key: 'OTHER', id: 'AGT-2' });
    const section = host.querySelector('[data-testid="task-batch-verification"]');
    expect(section).not.toBeNull();
    const summary = host.querySelector('[data-testid="task-batch-summary"]')!.textContent!;
    expect(summary).toContain('in-batch');
    expect(summary).toContain('Batch batch-8');
    expect(summary).toContain('1 of 1');
    expect(summary).toContain('running');
    expect(section!.textContent).toContain('Phase: running');
    expect(host.querySelector('[data-testid="task-batch-open-panel"]')!.classList).toContain('btn--ghost');
    expect(host.querySelector('[data-testid="task-batch-open-panel"]')!.classList).toContain('studio-button');
  });

  it('counts a queued member', async () => {
    const { host } = await mount(redHalvingState, { id: 'AGT-1' });
    expect(host.querySelector('[data-testid="task-batch-summary"]')!.textContent).toContain('pending');
  });

  it('explains why a card waits for a concern fix before batch admission', async () => {
    const wait = { status: 'review-fix' as const, reason: 'actionable review concern',
      reasonDetail: 'Review review-7 found a behavior gap in code-quality. Fix round 1 of 1 must finish before batch admission.' };
    const { host } = await mount({ ...redHalvingState,
      members: { ...redHalvingState.members, 'CARD-7': wait },
      reviewFixWaits: [{ taskKey: 'CARD-7', reviewAttemptId: 'review-7', round: 1, maximum: 1, aspects: ['code-quality'] }],
    }, { key: 'CARD-7', id: 'CARD-7' });
    expect(host.querySelector('[data-testid="task-batch-review-fix"]')?.textContent).toContain('before batch admission');
  });

  it('states the paused batch reason and the red and green outcomes', async () => {
    const { host } = await mount({ ...redHalvingState, members: { ...redHalvingState.members,
      'AGT-9': { status: 'fallback', batchId: 'batch-9', phase: 'abandoned',
        position: 2, size: 4, fallbackRule: 'batch-paused',
        fallbackEvent: 'batch batch-9 paused: fresh gate verdict required' },
    } }, { id: 'AGT-9' });
    expect(host.querySelector('[data-testid="task-batch-fallback-reason"]')?.textContent)
      .toContain('batch batch-9 paused: fresh gate verdict required');
    expect(host.querySelector('[data-testid="task-batch-fallback-outcome"]')?.textContent)
      .toContain('Red escalates the card; green integrates it.');
  });

  it('shows the push wait of a member', async () => {
    const { fixture, state, host } = await mount(redHalvingState, { id: 'AGT-2' });
    state.set({ ...redHalvingState, pushWait: {
      jobId: 'AGT-2', approvedSha: 'a'.repeat(40),
      sinceUtc: '2026-11-03T01:05:36Z', ageMinutes: 4.5, ageAlert: true,
      reason: 'Approved integration push awaits restart recovery.',
    } });
    await fixture.whenStable();
    expect(host.querySelector('[data-testid="task-integration-push-wait"]')!.textContent).toContain('pending for 4.5 min');
  });

  it('renders no DOM for a card that is not a batch member', async () => {
    const { host } = await mount(redHalvingState, { taskKey: '/tmp/demo::AGT-3403', key: 'AGT-3403', id: 'AGT-3403' });
    expect(host.children.length).toBe(0);
    expect(host.textContent!.trim()).toBe('');
  });

  it('renders no DOM while the state is loading or unavailable', async () => {
    const { fixture, unavailable, host } = await mount(null, { id: 'AGT-2' });
    expect(host.children.length).toBe(0);
    expect(host.textContent!.trim()).toBe('');
    unavailable.set(true);
    await fixture.whenStable();
    expect(host.children.length).toBe(0);
  });

  it('renders no DOM when the gate is disabled, even for a listed member', async () => {
    const { host } = await mount({ ...redHalvingState, plan: { ...redHalvingState.plan, decision: 'disabled' } }, { id: 'AGT-2' });
    expect(host.children.length).toBe(0);
  });
  it('says which batch proved the card red and lists the failing tests', async () => {
    const { host } = await mount(isolatedRedState, { taskKey: '/tmp/demo::AGT-2', key: 'AGT-2', id: 'AGT-2' });
    const proven = host.querySelector('[data-testid="task-batch-proven-red"]')!;
    expect(proven.querySelector('[data-testid="task-batch-proven-red-status"]')!.textContent!.trim()).toBe('Proven red in batch-14');
    expect([...proven.querySelectorAll('[data-testid="batch-proven-red-test"]')].map(test => test.textContent))
      .toEqual(['Checkout.Tests.TotalsIncludeShipping', 'Checkout.Tests.EmptyCartHasNoTotal']);
    expect(proven.querySelector('[data-testid="batch-proven-red-evidence"]')).not.toBeNull();
  });

  it('marks no proven-green member red and lists failing pre-check tests', async () => {
    const green = await mount(isolatedRedState, { id: 'AGT-1' });
    expect(green.host.querySelector('[data-testid="task-batch-proven-red"]')).toBeNull();
    TestBed.resetTestingModule();
    const preCheck = { outcome: 'red' as const, resultSha: 'a'.repeat(40), baseSha: 'b'.repeat(40), gateProfileDigest: 'digest',
      reason: 'Build or tests failed', failedTests: ['Product.Tests.One', 'Product.Tests.Two'], completedAtUtc: '2026-11-09T09:00:00Z' };
    const red = await mount({ ...redHalvingState, members: { ...redHalvingState.members,
      'AGT-9': { status: 'pre-check-red', preCheck } } }, { id: 'AGT-9' });
    expect([...red.host.querySelectorAll('[data-testid="task-batch-pre-check-tests"] li')].map(item => item.textContent))
      .toEqual(['Product.Tests.One', 'Product.Tests.Two']);
  });

  it('explains an interrupted red parent and the member wait', async () => {
    const parent = { ...redHalvingState.batches[0], phase: 'paused', phaseReason: 'isolation interrupted',
      phaseAtUtc: '2026-11-10T03:12:00Z' };
    const state = { ...redHalvingState, batches: [parent], members: {
      ...redHalvingState.members, 'AGT-2': { status: 'pending' as const, batchId: parent.batchId },
    } };
    const { host } = await mount(state, { id: 'AGT-2' });
    expect(host.querySelector('[data-testid="task-batch-isolation-state"]')?.textContent)
      .toContain('Ended without a verdict at 2026-11-10T03:12:00Z');
    expect(host.querySelector('[data-testid="task-batch-isolation-state"]')?.textContent)
      .toContain('Waiting for: The next batch plan after interrupted isolation');
  });

  it('shows when a live isolation flight began', async () => {
    const parent = { ...redHalvingState.batches[0], isolationActive: true,
      phaseAtUtc: '2026-11-10T03:10:00Z' };
    const { host } = await mount({ ...redHalvingState, batches: [parent], members: {
      ...redHalvingState.members, 'AGT-2': { status: 'in-batch' as const, batchId: parent.batchId, phase: 'red' },
    } }, { id: 'AGT-2' });
    expect(host.querySelector('[data-testid="task-batch-isolation-state"]')?.textContent)
      .toContain('Isolating since 2026-11-10T03:10:00Z');
  });

  it('keeps the interruption visible after the member enters a new batch', async () => {
    const old = { ...redHalvingState.batches[0], phase: 'paused',
      phaseReason: 'isolation interrupted', phaseAtUtc: '2026-11-10T03:12:00Z' };
    const next = { ...redHalvingState.batches[1], batchId: 'batch-next', parentBatchId: null, phase: 'formed' };
    const state = { ...redHalvingState, batches: [next, old], members: {
      ...redHalvingState.members, 'AGT-2': { status: 'in-batch' as const, batchId: 'batch-next', phase: 'formed' },
    } };
    const { host } = await mount(state, { id: 'AGT-2' });
    expect(host.querySelector('[data-testid="task-batch-isolation-state"]')?.textContent)
      .toContain('Ended without a verdict at 2026-11-10T03:12:00Z');
    expect(host.querySelector('[data-testid="task-batch-isolation-state"]')?.textContent)
      .toContain('Waiting for: Batch batch-next verification');
  });
});
