import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AuthService, AuthSessionState, type AuthUser } from './auth.service';

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
    http.expectOne('/api/auth/status').flush({ profile: 'local', bootstrapRequired: false, authenticated: false });
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
    http.expectOne('/api/auth/status').flush({ profile: 'local', bootstrapRequired: false, authenticated: false });
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
    http.expectOne('/api/auth/status').flush({ profile: 'local', bootstrapRequired: false, authenticated: false });
    http.expectOne('/api/v1/studio/auth/status').flush({
      bootstrapRequired: true, bootstrapCodeRequired: false, authenticated: false,
    });
    expect(auth.status()?.bootstrapCodeRequired).toBe(false);
    auth.bootstrap('owner', 'password', 'Owner').subscribe();
    const request = http.expectOne('/api/v1/studio/auth/bootstrap');
    expect(request.request.body).not.toHaveProperty('bootstrapCode');
    request.flush({ status: { bootstrapRequired: false, authenticated: true, user } });
  });

  it('uses the monolith session for the networked Access page', () => {
    auth.initialize();
    http.expectOne('/api/auth/status').flush({
      profile: 'networked', bootstrapRequired: false, authenticated: false,
    });
    http.expectNone('/api/v1/studio/auth/status');
    auth.login('owner', 'password').subscribe();
    http.expectOne('/api/auth/login').flush({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false },
    });
    expect(auth.status()?.user?.id).toBe('usr_owner');
  });

  it('previews a networked invitation and offers one factor recommendation after acceptance', () => {
    const token = ['inv', 'example'].join('.');
    auth.previewInvitation(token).subscribe(preview => expect(preview.inviter).toBe('Robert'));
    http.expectOne(`/api/v1/studio/auth/invite/preview?token=${encodeURIComponent(token)}`).flush({
      username: 'carla', displayName: 'Carla', inviter: 'Robert', role: 'Product',
      projects: ['Website'], expiresAt: '2026-11-12T09:00:00Z', secondFactorRequired: false,
    });
    auth.acceptInvitation(token, 'new password long enough').subscribe();
    http.expectOne('/api/v1/studio/auth/invite/accept').flush({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_carla', username: 'carla', displayName: 'Carla', role: 'contributor',
        projects: ['PROJ-001'], disabled: false, mustChangePassword: false },
      secondFactorEnrollmentRequired: false,
    });
    expect(auth.factorRecommendation()).toBe(true);
    expect(auth.studioAllowed()).toBe(false);
    auth.dismissFactorRecommendation();
    expect(auth.studioAllowed()).toBe(true);
  });

  it('holds the restored Owner at the notice until it is acknowledged', () => {
    auth.initialize();
    http.expectOne('/api/auth/status').flush({
      profile: 'networked', bootstrapRequired: false, authenticated: false,
    });
    auth.login('owner', 'password').subscribe();
    http.expectOne('/api/auth/login').flush({
      profile: 'networked', bootstrapRequired: false, authenticated: true,
      user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false },
      restoreNotice: { archiveId: 'archive-1', archiveCreatedAt: '2026-11-09T06:00:00Z', restoredAt: '2026-11-10T06:00:00Z' },
    });
    expect(auth.studioAllowed()).toBe(false);
    auth.acknowledgeRestoreNotice('archive-1').subscribe();
    const request = http.expectOne('/api/auth/restore-notice/acknowledge');
    expect(request.request.body).toEqual({ archiveId: 'archive-1' });
    request.flush(null);
    expect(auth.studioAllowed()).toBe(true);
  });
});

describe('AGT-3668 session without a project', () => {
  const user = (role: AuthUser['role'], projects: string[]): AuthUser => ({
    id: 'usr-1', username: 'carla', displayName: 'Carla', role, projects, disabled: false, mustChangePassword: false,
  });

  function state(status: Parameters<AuthSessionState['status']['set']>[0]): AuthSessionState {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const session = TestBed.inject(AuthSessionState);
    session.status.set(status);
    return session;
  }

  it('is a Product session bound to a project, not one without a project', () => {
    const session = state({ profile: 'networked', bootstrapRequired: false, authenticated: true, user: user('contributor', ['PROJ-1']) });
    expect(session.productSession()).toBe(true);
    expect(session.withoutProject()).toBe(false);
  });

  it('reports a non-owner account whose last project binding was removed', () => {
    expect(state({ profile: 'networked', bootstrapRequired: false, authenticated: true, user: user('viewer', []) })
      .withoutProject()).toBe(true);
  });

  it('never reports an owner, a signed-out browser or the local profile', () => {
    expect(state({ profile: 'networked', bootstrapRequired: false, authenticated: true, user: user('owner', []) })
      .withoutProject()).toBe(false);
    TestBed.resetTestingModule();
    expect(state({ profile: 'networked', bootstrapRequired: false, authenticated: false, user: user('contributor', []) })
      .withoutProject()).toBe(false);
    TestBed.resetTestingModule();
    expect(state({ profile: 'local', bootstrapRequired: false, authenticated: false }).withoutProject()).toBe(false);
  });
});
