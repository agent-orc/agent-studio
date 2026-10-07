import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AuthService } from './auth.service';

describe('standalone Studio identity projection', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
    ] });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const user = { userId: 'owner-1', username: 'owner', displayName: 'Owner', role: 'owner',
    projectIds: ['demo'], disabled: false, mustChangePassword: false };

  it('opens an authenticated standalone session from the Task Server status shape', () => {
    auth.initialize();
    http.expectOne('/api/v1/studio/auth/status').flush({
      bootstrapRequired: false, authenticated: true, user,
    });
    expect(auth.studioAllowed()).toBe(true);
    expect(auth.status()?.user).toMatchObject({ id: 'owner-1', projects: ['demo'] });
    expect(auth.status()?.bootstrapCodeRequired).toBe(false);

    auth.login('owner', 'password').subscribe();
    http.expectOne('/api/v1/studio/auth/login').flush({
      sessionToken: 'cookie-only', csrfToken: 'cookie-only',
      status: { bootstrapRequired: false, authenticated: true, user },
    });
    expect(auth.networkedAuthenticated()).toBe(true);
  });

  it('sends the installer code and holds the owner recovery code until custody is acknowledged', () => {
    auth.initialize();
    http.expectOne('/api/v1/studio/auth/status').flush({
      bootstrapRequired: true, bootstrapCodeRequired: true, authenticated: false,
    });
    expect(auth.status()?.bootstrapCodeRequired).toBe(true);
    auth.bootstrap('owner', 'password', 'Owner', 'owner-code').subscribe();
    const request = http.expectOne('/api/v1/studio/auth/bootstrap');
    expect(request.request.body.bootstrapCode).toBe('owner-code');
    request.flush({
      sessionToken: 'cookie-only', csrfToken: 'cookie-only', recoveryCode: 'rcv_once',
      status: { bootstrapRequired: false, authenticated: true, user },
    });
    expect(auth.ownerRecoveryCode()).toBe('rcv_once');
    expect(auth.status()?.bootstrapCodeRequired).toBe(false);
    expect(auth.studioAllowed()).toBe(false);
    auth.acknowledgeRecoveryCode();
    expect(auth.studioAllowed()).toBe(true);
  });

  it('does not require an installer code when standalone bootstrap has no armed code', () => {
    auth.initialize();
    http.expectOne('/api/v1/studio/auth/status').flush({
      bootstrapRequired: true, bootstrapCodeRequired: false, authenticated: false,
    });
    expect(auth.status()?.bootstrapCodeRequired).toBe(false);
    auth.bootstrap('owner', 'password', 'Owner').subscribe();
    const request = http.expectOne('/api/v1/studio/auth/bootstrap');
    expect(request.request.body).not.toHaveProperty('bootstrapCode');
    request.flush({ status: { bootstrapRequired: false, authenticated: true, user } });
  });
});
