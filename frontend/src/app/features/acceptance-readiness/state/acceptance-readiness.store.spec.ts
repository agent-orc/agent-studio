import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of, throwError } from 'rxjs';
import { AcceptanceReadinessStore } from './acceptance-readiness.store';
import { TaskService } from '../../../services/task.service';
import type { BatchMoveJobResponse } from '../../../models/task.model';
import type { AcceptanceReadinessReport } from '../models/acceptance-readiness.model';
import { readinessItem, reviewCard } from '../testing/acceptance-readiness.fixtures';

function batch(results: BatchMoveJobResponse['results']): BatchMoveJobResponse {
  return {
    id: 'batch-1', status: 'completed', total: results.length, completed: results.length,
    succeeded: results.filter(result => result.status === 'moved').length,
    failed: results.filter(result => result.status !== 'moved').length,
    results, message: null, createdAt: '', startedAt: '', finishedAt: '',
    metrics: {
      totalDurationMs: 0, itemMoveDurationMs: 0, laneLockAcquisitions: 0, laneLockWaitMs: 0, laneLockHeldMs: 0,
      scannerInvalidations: 0, scannerRefreshes: 0, scannerRefreshMs: 0, gitProcesses: 0, gitProcessMs: 0,
    },
  };
}

describe('AcceptanceReadinessStore', () => {
  const tasks = {
    startBatchMove: vi.fn(),
    getBatchMove: vi.fn(),
    continueJob: vi.fn(),
    refresh: vi.fn(),
  };
  let store: AcceptanceReadinessStore;
  let http: HttpTestingController;
  const cards = [reviewCard('a'), reviewCard('b'), reviewCard('c')];

  beforeEach(() => {
    Object.values(tasks).forEach(mock => mock.mockReset());
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: tasks },
      ],
    });
    store = TestBed.inject(AcceptanceReadinessStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function answer(report: Partial<AcceptanceReadinessReport>): Promise<void> {
    const request = http.expectOne('/api/tasks/acceptance-readiness');
    request.flush({ checkedAtUtc: '2026-11-09T10:00:00Z', counts: {}, items: [], ...report });
    await Promise.resolve();
  }

  async function check(): Promise<void> {
    const done = store.refresh(cards);
    await answer({
      items: [readinessItem('a', 'ready'), readinessItem('b', 'review-concerns'), readinessItem('c', 'ready')],
    });
    await done;
  }

  it('asks the dry run for every Human Review card and groups the lane by its answer', async () => {
    const done = store.refresh([...cards, reviewCard('elsewhere', { state: '6-completed' })]);
    const request = http.expectOne('/api/tasks/acceptance-readiness');
    expect(request.request.method).toBe('POST');
    expect(request.request.body.items).toEqual(cards.map(card => ({ jobId: card.id, watchPath: card.watchPath })));
    request.flush({
      checkedAtUtc: '2026-11-09T10:00:00Z', counts: {},
      items: [readinessItem('a', 'ready'), readinessItem('b', 'review-concerns'), readinessItem('c', 'ready')],
    });
    await done;

    expect(store.checked()).toBe(true);
    expect(store.groupsFor(cards)?.map(group => [group.kind, group.jobs.length])).toEqual([['ready', 2], ['review-concerns', 1]]);
    expect(store.itemFor(cards[1])?.concern).toBe('The lane hides the reason.');
  });

  it('keeps the board order and names the failure when the check fails', async () => {
    const done = store.refresh(cards);
    http.expectOne('/api/tasks/acceptance-readiness').flush({ error: 'boom', message: 'Checks unavailable.' },
      { status: 500, statusText: 'Server Error' });
    await done;

    expect(store.groupsFor(cards)).toBeNull();
    expect(store.error()).toBe('Checks unavailable.');
  });

  it('accepts ready cards through the batch move and reports each result', async () => {
    await check();
    tasks.startBatchMove.mockReturnValue(of(batch([])));
    tasks.getBatchMove.mockReturnValue(of(batch([
      { index: 0, jobId: 'a', status: 'moved', message: null, durationMs: 1 },
      { index: 1, jobId: 'c', status: 'integration-failed', message: 'Acceptance refused: integrated-unverified.', durationMs: 1 },
    ])));

    const done = store.acceptReady(cards, cards);
    await vi.waitFor(() => expect(store.results().length).toBe(2));
    await answer({ items: [readinessItem('b', 'review-concerns'), readinessItem('c', 'integrated-unverified')] });
    await done;

    expect(tasks.startBatchMove).toHaveBeenCalledWith([
      { jobId: 'a', watchPath: '/tmp/watch', targetState: '6-completed' },
      { jobId: 'c', watchPath: '/tmp/watch', targetState: '6-completed' },
    ]);
    expect(store.results()).toEqual([
      { jobId: 'a', title: 'Card a', accepted: true, message: 'Moved to Accepted' },
      { jobId: 'c', title: 'Card c', accepted: false, message: 'Acceptance refused: integrated-unverified.' },
    ]);
    expect(store.itemFor(cards[2])?.readiness).toBe('integrated-unverified');
    expect(store.accepting()).toBe(false);
  });

  it('sends nothing when no shown card is ready', async () => {
    await check();
    await store.acceptReady([cards[1]], cards);
    expect(tasks.startBatchMove).not.toHaveBeenCalled();
  });

  it('reports every card as refused when the batch cannot start', async () => {
    await check();
    tasks.startBatchMove.mockReturnValue(throwError(() => ({ error: { message: 'attention-lease-required' } })));

    const done = store.acceptReady(cards, cards);
    await vi.waitFor(() => expect(store.results().length).toBe(2));
    await answer({ items: [] });
    await done;

    expect(store.results().every(result => !result.accepted && result.message === 'attention-lease-required')).toBe(true);
  });

  it('sends a round only after confirmation, with the edited prompt and the result ref', async () => {
    await check();
    store.openRound(cards[0]);
    expect(store.roundDraft()).toBeNull();

    store.openRound(cards[1]);
    expect(store.roundDraft()?.item.round?.baseRef).toBe('refs/heads/runner/r1/AGT-1');
    expect(tasks.continueJob).not.toHaveBeenCalled();

    tasks.continueJob.mockReturnValue(of({ status: 'queued' }));
    const sent = await store.sendRound('  Fix the reason line.  ', 'refs/heads/runner/r1/AGT-1');

    expect(sent).toBe(true);
    expect(tasks.continueJob).toHaveBeenCalledWith('b', 'Fix the reason line.', '/tmp/watch',
      undefined, undefined, undefined, undefined, undefined,
      'Close the gap that refused the acceptance of this card.', 'refs/heads/runner/r1/AGT-1');
    expect(store.roundDraft()).toBeNull();
  });

  it('keeps the draft open and shows the refusal when the continue route refuses', async () => {
    await check();
    store.openRound(cards[1]);
    tasks.continueJob.mockReturnValue(throwError(() => ({ error: { error: 'base-ref-not-on-card', message: 'base-ref-not-on-card: not a result ref of this card.' } })));

    expect(await store.sendRound('Fix it.', 'refs/heads/other')).toBe(false);
    expect(store.roundDraft()).not.toBeNull();
    expect(store.roundError()).toBe('base-ref-not-on-card: not a result ref of this card.');
  });
});
