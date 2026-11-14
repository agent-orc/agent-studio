import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BatchGateService, isBatchGateState } from './batch-gate.service';
import { batchPublishedSha, batchReplayLabel, batchRiskLabel, batchRunStatus, batchRunEvidenceUrl } from '../models/batch-gate.model';
import { clientIdInterceptor } from './client-id.interceptor';
import { backendShapedState, batchSettings, redHalvingState } from '../features/batch-gate/testing/batch-fixtures';

describe('BatchGateService', () => {
  it('shows missing verdicts as interrupted outside a running batch and links run artifacts', () => {
    const batch = redHalvingState.batches[0];
    const run = { batchRunId: 'run-1', host: 'runner', startedAtUtc: '2026-11-10T00:00:00Z' };
    expect(batchRunStatus(batch, run)).toBe('interrupted');
    expect(batchRunStatus({ ...batch, phase: 'abandoned' }, run)).toBe('interrupted');
    expect(batchRunStatus({ ...batch, phase: 'published' }, run)).toBe('interrupted');
    expect(batchRunStatus({ ...batch, phase: 'running' }, run)).toBe('running');
    expect(batchRunEvidenceUrl('Demo Project', 'batch 1', 'run 1', 'log'))
      .toBe('/api/projects/Demo%20Project/batch-gate/run-log?batchId=batch%201&runId=run%201');
  });
  let service: BatchGateService;
  let http: HttpTestingController;
  const base = '/api/projects/Demo%20Project/batch-gate';
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([clientIdInterceptor])), provideHttpClientTesting()] });
    service = TestBed.inject(BatchGateService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => { vi.useRealTimers(); http.verify(); });

  it('shares one state poll per visible project and retains contract data', async () => {
    const stopA = service.watch('Demo Project');
    const stopB = service.watch('Demo Project');
    http.expectOne(`${base}/state?history=10`).flush(redHalvingState);
    await Promise.resolve();
    expect(http.match(`${base}/state?history=10`)).toHaveLength(0);
    expect(service.state('Demo Project')()?.batches[0].phase).toBe('red');
    expect(service.state('Demo Project')()?.batches[1].parentBatchId).toBe('batch-7');
    expect(service.state('Demo Project')()?.members['AGT-4'].status).toBe('fallback');
    expect(service.state('Demo Project')()?.plan.advisor?.verdict).toBe('veto');
    stopA(); stopB();
  });

  it('uses the exact settings and action routes with client attribution', () => {
    service.getSettings('Demo Project').subscribe();
    http.expectOne(base).flush(batchSettings);
    service.putSettings('Demo Project', batchSettings.settings).subscribe();
    const put = http.expectOne(base);
    expect(put.request.body).toEqual(batchSettings.settings);
    expect(put.request.headers.get('X-Client-Id')).toBe('local-default');
    put.flush(batchSettings);
    service.closeNow('Demo Project').subscribe();
    const close = http.expectOne(`${base}/close-now`);
    expect(close.request.headers.get('X-Client-Id')).toBe('local-default');
    close.flush({}, { status: 202, statusText: 'Accepted' });
    service.eject('Demo Project', 'AGT/4', 'Inspect separately').subscribe();
    const eject = http.expectOne(`${base}/members/AGT%2F4/eject`);
    expect(eject.request.headers.get('X-Client-Id')).toBe('local-default');
    expect(eject.request.body).toEqual({ reason: 'Inspect separately' });
    eject.flush({}, { status: 202, statusText: 'Accepted' });
  });

  it('polls at 15 seconds and stops when the last consumer leaves', async () => {
    vi.useFakeTimers();
    const stop = service.watch('Demo Project');
    http.expectOne(`${base}/state?history=10`).flush(redHalvingState);
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(15_000);
    http.expectOne(`${base}/state?history=10`).flush(redHalvingState);
    await Promise.resolve();
    stop();
    await vi.advanceTimersByTimeAsync(30_000);
    expect(http.match(`${base}/state?history=10`)).toHaveLength(0);
  });

  it.each([404, 405])('hides unsupported endpoint %i and stops polling', async status => {
    vi.useFakeTimers();
    const stop = service.watch('Demo Project');
    http.expectOne(`${base}/state?history=10`).flush({}, { status, statusText: 'Unavailable' });
    await Promise.resolve();
    expect(service.unavailable('Demo Project')()).toBe(true);
    expect(service.state('Demo Project')()).toBeNull();
    await vi.advanceTimersByTimeAsync(45_000);
    expect(http.match(`${base}/state?history=10`)).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(255_000);
    http.expectOne(`${base}/state?history=10`).flush(redHalvingState);
    await Promise.resolve();
    expect(service.unavailable('Demo Project')()).toBe(false);
    stop();
  });

  it('rejects a successful but malformed catch-all response', async () => {
    const stop = service.watch('Demo Project');
    http.expectOne(`${base}/state?history=10`).flush([]);
    await Promise.resolve();
    expect(service.unavailable('Demo Project')()).toBe(true);
    expect(service.state('Demo Project')()).toBeNull();
    stop();
  });

  it('rejects malformed settings without exposing them to the form', () => {
    let failed = false;
    service.getSettings('Demo Project').subscribe({ error: () => { failed = true; } });
    http.expectOne(base).flush([]);
    expect(failed).toBe(true);
  });

  it('keeps a disabled state safe for consumers', async () => {
    const stop = service.watch('Demo Project');
    http.expectOne(`${base}/state?history=10`).flush({ ...redHalvingState, plan: { ...redHalvingState.plan, decision: 'disabled' } });
    await Promise.resolve();
    expect(service.state('Demo Project')()?.plan.decision).toBe('disabled');
    stop();
  });

  it('pauses in a hidden tab and resumes with one request', async () => {
    vi.useFakeTimers();
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    const stop = service.watch('Demo Project');
    http.expectOne(`${base}/state?history=10`).flush(redHalvingState);
    await Promise.resolve();
    hidden.mockReturnValue(true);
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(30_000);
    expect(http.match(`${base}/state?history=10`)).toHaveLength(0);
    hidden.mockReturnValue(false);
    document.dispatchEvent(new Event('visibilitychange'));
    http.expectOne(`${base}/state?history=10`).flush(redHalvingState);
    await Promise.resolve();
    stop();
    hidden.mockRestore();
  });
});

describe('batch state from the AGT-3052 backend', () => {
  it('accepts numeric risk, replay objects and explicit nulls', () => {
    expect(isBatchGateState(JSON.parse(JSON.stringify(backendShapedState)))).toBe(true);
  });

  it('accepts a review fix wait as a batch member state', () => {
    expect(isBatchGateState({ ...backendShapedState,
      members: { 'CARD-7': { status: 'review-fix', reason: 'actionable concern',
        reasonDetail: 'Fix round 1 of 1 must finish before batch admission.' } },
      reviewFixWaits: [{ taskKey: 'CARD-7', reviewAttemptId: 'review-7', round: 1,
        maximum: 1, aspects: ['code-quality'] }],
    })).toBe(true);
  });

  it('renders risk as a percentage, the replay outcome and the verified published SHA', () => {
    expect(batchRiskLabel(0.35)).toBe('35 %');
    expect(batchRiskLabel('high')).toBe('high');
    expect(batchReplayLabel({ outcome: 'applied' })).toBe('applied');
    expect(batchReplayLabel(null)).toBe('not replayed');
    expect(batchPublishedSha(backendShapedState.batches[0].publication)).toBe('c'.repeat(40));
  });
});
