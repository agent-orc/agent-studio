import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthSessionState } from './auth.service';
import { sessionSecurityInterceptor } from './session-security.interceptor';

describe('sessionSecurityInterceptor', () => {
  beforeEach(() => TestBed.resetTestingModule());

  function configure() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionSecurityInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    return {
      http: TestBed.inject(HttpClient),
      requests: TestBed.inject(HttpTestingController),
      auth: TestBed.inject(AuthSessionState),
    };
  }

  it('returns a networked Studio session to the login gate after an API 401', () => {
    const { http, requests, auth } = configure();
    auth.status.set({
      profile: 'networked',
      bootstrapRequired: false,
      authenticated: true,
      user: {
        id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false,
      },
    });

    http.get('/api/tasks').subscribe({ error: () => undefined });
    requests.expectOne('/api/tasks').flush(
      { error: 'authentication-required' },
      { status: 401, statusText: 'Unauthorized' },
    );

    expect(auth.status()?.authenticated).toBe(false);
    expect(auth.studioAllowed()).toBe(false);
  });

  it('does not reinterpret a local-profile 401 as a server-session expiry', () => {
    const { http, requests, auth } = configure();
    auth.status.set({ profile: 'local', bootstrapRequired: false, authenticated: false });

    http.get('/api/tasks').subscribe({ error: () => undefined });
    requests.expectOne('/api/tasks').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(auth.status()?.profile).toBe('local');
    expect(auth.studioAllowed()).toBe(true);
  });

  it('keeps first-owner bootstrap open after a rejected installer code', () => {
    const { http, requests, auth } = configure();
    auth.status.set({
      profile: 'networked', bootstrapRequired: true,
      bootstrapCodeRequired: true, authenticated: false,
    });

    http.post('/api/v1/studio/auth/bootstrap', { bootstrapCode: 'wrong' })
      .subscribe({ error: () => undefined });
    requests.expectOne('/api/v1/studio/auth/bootstrap').flush(
      { error: 'owner-bootstrap-code-invalid' },
      { status: 401, statusText: 'Unauthorized' },
    );

    expect(auth.status()?.bootstrapRequired).toBe(true);
    expect(auth.status()?.bootstrapCodeRequired).toBe(true);
  });
});
