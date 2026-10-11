import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BatchGateService } from './batch-gate.service';
import { clientIdInterceptor } from './client-id.interceptor';
import { batchSettings, redHalvingState } from '../features/batch-gate/testing/batch-fixtures';

describe('BatchGateService', () => {
  let service: BatchGateService;
  let http: HttpTestingController;
  const base = '/api/projects/Demo%20Project/batch-gate';
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([clientIdInterceptor])), provideHttpClientTesting()] });
    service = TestBed.inject(BatchGateService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => { http.verify(); vi.useRealTimers(); });

  it('shares one state poll per visible project and retains contract data', async () => {
    const stopA = service.watch('Demo Project');
    const stopB = service.watch('Demo Project');
    http.expectOne(`${base}/state?history=10`).flush(redHalvingState);
    await Promise.resolve();
    expect(http.match(`${base}/state?history=10`)).toHaveLength(0);
    expect(service.state('Demo Project')()?.batches[0].phase).toBe('halving');
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
    service.eject('Demo Project', 'AGT/4').subscribe();
    const eject = http.expectOne(`${base}/members/AGT%2F4/eject`);
    expect(eject.request.headers.get('X-Client-Id')).toBe('local-default');
    eject.flush({}, { status: 202, statusText: 'Accepted' });
  });
});
