import { describe, it, expect, beforeEach } from 'vitest';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  GIT_STATE_AT_HEADER,
  GIT_STATE_STALE_HEADER,
  gitStateStampInterceptor,
} from './git-state-stamp.interceptor';
import { GitStateStampService } from './git-state-stamp.service';

function configure() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([gitStateStampInterceptor])),
      provideHttpClientTesting(),
    ],
  });
  return {
    http: TestBed.inject(HttpClient),
    httpMock: TestBed.inject(HttpTestingController),
    stamps: TestBed.inject(GitStateStampService),
  };
}

describe('gitStateStampInterceptor', () => {
  beforeEach(() => TestBed.resetTestingModule());

  it('publishes the freshness stamp from a board response without any call site reading headers', () => {
    const { http, httpMock, stamps } = configure();
    expect(stamps.known()).toBe(false);

    http.get('/api/tasks/grouped').subscribe();
    httpMock.expectOne('/api/tasks/grouped').flush({}, {
      headers: {
        [GIT_STATE_AT_HEADER]: '2026-09-07T09:00:00.0000000Z',
        [GIT_STATE_STALE_HEADER]: 'false',
      },
    });

    expect(stamps.known()).toBe(true);
    expect(stamps.stamp()!.at!.toISOString()).toBe('2026-09-07T09:00:00.000Z');
    expect(stamps.stamp()!.stale).toBe(false);
  });

  it('marks the board stale while the index has a change it has not folded in yet', () => {
    const { http, httpMock, stamps } = configure();

    http.get('/api/tasks').subscribe();
    httpMock.expectOne('/api/tasks').flush([], {
      headers: {
        [GIT_STATE_AT_HEADER]: '2026-09-07T09:00:00.0000000Z',
        [GIT_STATE_STALE_HEADER]: 'true',
      },
    });

    expect(stamps.stamp()!.stale).toBe(true);
  });

  it('leaves the last known stamp alone for responses that carry none', () => {
    const { http, httpMock, stamps } = configure();

    http.get('/api/tasks/grouped').subscribe();
    httpMock.expectOne('/api/tasks/grouped').flush({}, {
      headers: { [GIT_STATE_STALE_HEADER]: 'false' },
    });
    http.get('/api/cli/status').subscribe();
    httpMock.expectOne('/api/cli/status').flush({});

    expect(stamps.known()).toBe(true);
    expect(stamps.stamp()!.stale).toBe(false);
    expect(stamps.stamp()!.at).toBeNull();
  });

  it('ignores an unparseable capture time rather than showing an invalid date', () => {
    const { http, httpMock, stamps } = configure();

    http.get('/api/git/inventory?project=demo').subscribe();
    httpMock.expectOne('/api/git/inventory?project=demo').flush({}, {
      headers: {
        [GIT_STATE_AT_HEADER]: 'not-a-timestamp',
        [GIT_STATE_STALE_HEADER]: 'true',
      },
    });

    expect(stamps.stamp()!.at).toBeNull();
    expect(stamps.stamp()!.stale).toBe(true);
  });
});
