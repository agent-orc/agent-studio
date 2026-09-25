import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectBranchSweepComponent } from './project-branch-sweep.component';
import {
  BRANCH_SWEEP_BATCH_SIZE,
  batchPercent,
  toBatches,
  toExecutionItems,
} from './branch-sweep-batches';
import type { BranchSweepCandidate, BranchSweepReport } from '../../../git';

function candidate(overrides: Partial<BranchSweepCandidate> = {}): BranchSweepCandidate {
  return {
    ref: 'task/DEM-1',
    class: 'task',
    taskKey: 'DEM-1',
    taskState: '7-archive',
    tipSha: 'a'.repeat(40),
    tipShortSha: 'aaaaaaa',
    tipCommittedAtUtc: '2025-01-01T00:00:00Z',
    ageDays: 400,
    containedInMain: true,
    containedInDevelop: true,
    referencedByOpenCard: false,
    decision: 'Delete',
    eligible: true,
    reason: 'Eligible for deletion; deletion policy met.',
    ...overrides,
  };
}

function reportFixture(candidates: BranchSweepCandidate[]): BranchSweepReport {
  return {
    project: 'Demo',
    repositoryPath: '/repo/demo',
    mode: 'report-only',
    startedAtUtc: '2026-09-14T12:00:00Z',
    completedAtUtc: '2026-09-14T12:00:05Z',
    windows: { taskDays: 7, salvageDays: 14, quarantineDays: 30, abandonedDays: 90 },
    refsBefore: candidates.length,
    refsAfter: candidates.length,
    totals: [{ class: 'task', total: candidates.length, eligible: candidates.filter(c => c.eligible).length, kept: candidates.length, deleted: 0 }],
    ageHistogram: [{ label: '365+ days', refs: candidates.length }],
    candidates,
    deletions: [],
    error: null,
  };
}

function setup() {
  TestBed.configureTestingModule({
    imports: [ProjectBranchSweepComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(ProjectBranchSweepComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.detectChanges();
  const httpCtrl = TestBed.inject(HttpTestingController);
  return { fixture, httpCtrl, root: fixture.nativeElement as HTMLElement };
}

async function open(
  context: ReturnType<typeof setup>,
  candidates: BranchSweepCandidate[] = [candidate()],
) {
  context.root.querySelector<HTMLButtonElement>('[data-testid="branch-sweep-toggle"]')!.click();
  context.fixture.detectChanges();
  context.httpCtrl.expectOne(r => r.url === '/api/git/branch-sweep/settings')
    .flush({ project: 'Demo', mode: 'report-only', windows: {}, defaults: {} });
  await context.fixture.whenStable();
  context.httpCtrl.expectOne(r => r.url === '/api/git/branch-sweep/latest')
    .flush(reportFixture(candidates));
  await context.fixture.whenStable();
  context.fixture.detectChanges();
}

describe('branch-sweep batching', () => {
  it('splits confirmed refs into batches of at most the push limit', () => {
    const batches = toBatches(Array.from({ length: 250 }, (_, index) => index));

    expect(batches).toHaveLength(3);
    expect(batches.map(batch => batch.length)).toEqual([BRANCH_SWEEP_BATCH_SIZE, BRANCH_SWEEP_BATCH_SIZE, 50]);
    expect(batches.flat()).toHaveLength(250);
  });

  it('maps candidates onto the execute payload with the tip the operator saw', () => {
    expect(toExecutionItems([candidate({ ref: 'task/DEM-2', tipSha: 'b'.repeat(40) })]))
      .toEqual([{ ref: 'task/DEM-2', tipSha: 'b'.repeat(40) }]);
  });

  it('reports an empty run as complete rather than dividing by zero', () => {
    expect(batchPercent({ total: 0, processed: 0, deleted: 0, kept: 0, batches: 0, currentBatch: 0, stopped: false }))
      .toBe(100);
    expect(batchPercent({ total: 200, processed: 100, deleted: 100, kept: 0, batches: 2, currentBatch: 2, stopped: false }))
      .toBe(50);
  });
});

describe('ProjectBranchSweepComponent', () => {
  it('loads the stored report and its mode on first open', async () => {
    const context = setup();
    await open(context);

    expect(context.root.querySelector('[data-testid="branch-sweep-mode"]')!.textContent)
      .toContain('report-only');
    expect(context.root.querySelectorAll('[data-testid="branch-sweep-candidates"] li')).toHaveLength(1);
    context.httpCtrl.verify();
  });

  it('shows the empty state when the project has never been swept', async () => {
    const context = setup();
    context.root.querySelector<HTMLButtonElement>('[data-testid="branch-sweep-toggle"]')!.click();
    context.fixture.detectChanges();
    context.httpCtrl.expectOne(r => r.url === '/api/git/branch-sweep/settings')
      .flush({ project: 'Demo', mode: 'report-only', windows: {}, defaults: {} });
    await context.fixture.whenStable();
    context.httpCtrl.expectOne(r => r.url === '/api/git/branch-sweep/latest').flush(null);
    await context.fixture.whenStable();
    context.fixture.detectChanges();

    expect(context.root.querySelector('[data-testid="branch-sweep-empty"]')).not.toBeNull();
  });

  it('only offers refs the policy marked eligible', async () => {
    const context = setup();
    await open(context, [
      candidate({ ref: 'task/DEM-1', eligible: true }),
      candidate({ ref: 'task/DEM-2', eligible: false, decision: 'NotMergedIntoDevelop', taskState: '3-progress' }),
    ]);

    // Default view lists eligible refs only; nothing is pre-selected, because
    // this panel deletes remote refs across every namespace.
    const rows = context.root.querySelectorAll('[data-testid="branch-sweep-candidates"] li');
    expect(rows).toHaveLength(1);
    expect(context.root.querySelector<HTMLButtonElement>('[data-testid="branch-sweep-delete"]')!.disabled)
      .toBe(true);

    context.root.querySelector<HTMLButtonElement>('[data-testid="branch-sweep-reclaim-all"]')!.textContent
      ?.includes('(1)');
  });

  it('keeps the wrapped confirmation group inside a 280px panel', async () => {
    const context = setup();
    await open(context);
    const sweep = context.root.querySelector<HTMLElement>('.swp')!;
    const confirm = context.root.querySelector<HTMLElement>('.swp__confirm')!;
    sweep.style.width = '280px';
    Object.defineProperty(sweep, 'getBoundingClientRect', { value: () => ({ left: 0, right: 280, width: 280 }) });
    Object.defineProperty(confirm, 'getBoundingClientRect', { value: () => ({ left: 8, right: 272, width: 264 }) });
    expect(confirm.getBoundingClientRect().right).toBeLessThanOrEqual(sweep.getBoundingClientRect().right);
  });

  it('deletes the confirmed selection in batches and re-classifies afterwards', async () => {
    const context = setup();
    await open(context, [candidate({ ref: 'task/DEM-1' }), candidate({ ref: 'task/DEM-3' })]);

    context.fixture.componentInstance.selectAllEligible();
    context.fixture.componentInstance.requestDelete();
    context.fixture.detectChanges();
    context.root.querySelector<HTMLButtonElement>('[data-testid="branch-sweep-confirm"]')!.click();
    await context.fixture.whenStable();

    const execute = context.httpCtrl.expectOne(r =>
      r.method === 'POST' && r.url === '/api/git/branch-sweep/execute');
    expect(execute.request.body.items).toEqual([
      { ref: 'task/DEM-1', tipSha: 'a'.repeat(40) },
      { ref: 'task/DEM-3', tipSha: 'a'.repeat(40) },
    ]);
    execute.flush({ project: 'Demo', isRepo: true, deletedCount: 2, keptCount: 0, actions: [], error: null });
    await context.fixture.whenStable();

    // The spent plan is replaced by a fresh read-only classification.
    context.httpCtrl.expectOne(r => r.url === '/api/git/branch-sweep/plan').flush(reportFixture([]));
    await context.fixture.whenStable();
    context.fixture.detectChanges();

    expect(context.fixture.componentInstance.progress().deleted).toBe(2);
    context.httpCtrl.verify();
  });

  it('persists a mode change through the settings endpoint', async () => {
    const context = setup();
    await open(context);

    context.root.querySelector<HTMLButtonElement>('[data-testid="branch-sweep-mode-reclaim"]')!.click();
    await context.fixture.whenStable();
    const put = context.httpCtrl.expectOne(r =>
      r.method === 'PUT' && r.url === '/api/git/branch-sweep/settings');
    expect(put.request.body).toEqual({ mode: 'reclaim' });
    put.flush({ project: 'Demo', mode: 'reclaim', windows: {}, defaults: {} });
    await context.fixture.whenStable();
    context.fixture.detectChanges();

    expect(context.root.querySelector('[data-testid="branch-sweep-mode"]')!.textContent)
      .toContain('reclaim');
    context.httpCtrl.verify();
  });
});
