import { describe, it, expect, beforeEach } from 'vitest';
import { signal } from '@angular/core';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { offlineGuardInterceptor, __resetOfflineGuardThrottle } from './offline-guard.interceptor';
import { ConnectionStatusService } from './connection-status.service';
import { NotificationService } from './notification.service';

function configure(offline: boolean) {
  const offlineSignal = signal(offline);
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([offlineGuardInterceptor])),
      provideHttpClientTesting(),
      { provide: ConnectionStatusService, useValue: { offline: offlineSignal } },
    ],
  });
  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    notify: TestBed.inject(NotificationService),
    offlineSignal,
  };
}

describe('offlineGuardInterceptor', () => {
  beforeEach(() => {
    __resetOfflineGuardThrottle();
    TestBed.resetTestingModule();
  });

  it('blocks a mutating /api request while offline and never hits the network', () => {
    const { http, httpMock, notify } = configure(true);
    let captured: HttpErrorResponse | null = null;

    http.post('/api/tasks/x/move', { targetState: '2-ready' }).subscribe({
      next: () => expect.unreachable('offline POST should not succeed'),
      error: (err: HttpErrorResponse) => (captured = err),
    });

    httpMock.expectNone('/api/tasks/x/move');
    expect(captured).toBeInstanceOf(HttpErrorResponse);
    expect(captured!.status).toBe(0);
    expect(notify.notifications().some((n) => n.kind === 'warning')).toBe(true);
  });

  it('lets GET requests through while offline so cached views can still refresh', () => {
    const { http, httpMock } = configure(true);
    let ok = false;

    http.get('/api/tasks').subscribe({ next: () => (ok = true) });
    httpMock.expectOne('/api/tasks').flush([]);

    expect(ok).toBe(true);
  });

  it('lets the read-only reference-status batch through while offline', () => {
    const { http, httpMock } = configure(true);
    let ok = false;

    http.post('/api/tasks/reference-status', { keys: ['AGT-1'] }).subscribe({ next: () => (ok = true) });
    httpMock.expectOne('/api/tasks/reference-status').flush({ items: [] });

    expect(ok).toBe(true);
  });

  it('lets the read-only TokenEconomy price lookup through while offline', () => {
    const { http, httpMock } = configure(true);
    let ok = false;

    http.post('/api/token-pricing/calculate', { items: [{ model: 'gpt-6-sol' }] })
      .subscribe({ next: () => (ok = true) });
    httpMock.expectOne('/api/token-pricing/calculate').flush({ items: [] });

    expect(ok).toBe(true);
  });

  it('lets mutating requests through when online', () => {
    const { http, httpMock } = configure(false);
    let ok = false;

    http.post('/api/tasks/x/move', {}).subscribe({ next: () => (ok = true) });
    httpMock.expectOne('/api/tasks/x/move').flush({});

    expect(ok).toBe(true);
  });

  it('does not block non-api absolute URLs while offline', () => {
    const { http, httpMock } = configure(true);
    let ok = false;

    http.post('https://third-party.example/thing', {}).subscribe({ next: () => (ok = true) });
    httpMock.expectOne('https://third-party.example/thing').flush({});

    expect(ok).toBe(true);
  });

  it('throttles the warning toast across a burst of blocked writes', () => {
    const { http, notify } = configure(true);

    for (let i = 0; i < 4; i++) {
      http.post(`/api/tasks/x/move-${i}`, {}).subscribe({ error: () => undefined });
    }

    expect(notify.notifications().filter((n) => n.kind === 'warning').length).toBe(1);
  });
});
