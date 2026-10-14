import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { buildIdentityInterceptor } from './build-identity.interceptor';
import { BUILD_SHA } from './build-identity';
import { BuildUpdateNoticeService, BUILD_SHA_HEADER } from './build-update-notice.service';
import { NotificationService } from './notification.service';

const frontendSha = 'a'.repeat(40);
const backendSha = 'b'.repeat(40);

describe('BuildUpdateNoticeService', () => {
  let http: HttpClient;
  let requests: HttpTestingController;
  let notice: BuildUpdateNoticeService;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([buildIdentityInterceptor])),
        provideHttpClientTesting(),
        { provide: BUILD_SHA, useValue: frontendSha },
      ],
    });
    http = TestBed.inject(HttpClient);
    requests = TestBed.inject(HttpTestingController);
    notice = TestBed.inject(BuildUpdateNoticeService);
    notifications = TestBed.inject(NotificationService);
  });

  function apiResponse(sha?: string): void {
    http.get('/api/environment').subscribe();
    requests.expectOne('/api/environment').flush({}, {
      headers: sha ? { [BUILD_SHA_HEADER]: sha } : {},
    });
  }

  it('shows no notice for matching identities', () => {
    apiResponse(frontendSha);
    expect(notifications.notifications()).toHaveLength(0);
  });

  it('shows one persistent reload notice for repeated mismatching API responses', () => {
    apiResponse(backendSha);
    apiResponse(backendSha);
    expect(notifications.notifications()).toHaveLength(1);
    expect(notifications.notifications()[0].message).toBe('Agent Studio was updated – reload to continue');
    expect(notifications.notifications()[0].durationMs).toBe(0);
    expect(notifications.notifications()[0].actions?.[0].label).toBe('Reload');
  });

  it('shows the notice again after a reconnect following dismissal', () => {
    apiResponse(backendSha);
    notifications.dismiss(notifications.notifications()[0].id);
    apiResponse(backendSha);
    expect(notifications.notifications()).toHaveLength(0);

    notice.checkAfterReconnect();
    requests.expectOne('/healthz').flush('ok', { headers: { [BUILD_SHA_HEADER]: backendSha } });
    expect(notifications.notifications()).toHaveLength(1);
  });

  it('ignores an absent server identity', () => {
    apiResponse();
    notice.checkAfterReconnect();
    requests.expectOne('/healthz').flush('ok');
    expect(notifications.notifications()).toHaveLength(0);
  });

  it('detects a mismatch on an API error response too', () => {
    http.get('/api/environment').subscribe({ error: () => undefined });
    requests.expectOne('/api/environment').flush('Unavailable', {
      status: 503,
      statusText: 'Unavailable',
      headers: { [BUILD_SHA_HEADER]: backendSha },
    });
    expect(notifications.notifications()).toHaveLength(1);
  });
});

describe('BuildUpdateNoticeService without a frontend identity', () => {
  it('shows nothing and skips the reconnect read', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([buildIdentityInterceptor])),
        provideHttpClientTesting(),
        { provide: BUILD_SHA, useValue: null },
      ],
    });
    const http = TestBed.inject(HttpClient);
    const requests = TestBed.inject(HttpTestingController);
    const notice = TestBed.inject(BuildUpdateNoticeService);
    const notifications = TestBed.inject(NotificationService);

    http.get('/api/environment').subscribe();
    requests.expectOne('/api/environment').flush({}, {
      headers: { [BUILD_SHA_HEADER]: backendSha },
    });
    notice.checkAfterReconnect();
    requests.expectNone('/healthz');
    expect(notifications.notifications()).toHaveLength(0);
  });
});
