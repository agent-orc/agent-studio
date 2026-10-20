import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { accessOverview } from '../testing/access-fixtures';
import { AccessService } from './access.service';

describe('AccessService', () => {
  let service: AccessService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AccessService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  async function flushLoad(): Promise<void> {
    http.expectOne('/api/auth/iam').flush(accessOverview());
    http.expectOne('/api/projects').flush([{ id: 'PROJ-001', displayName: 'Website', shortCode: 'WEB' }]);
    http.expectOne('/api/auth/runner-enrollments').flush([]);
    http.expectOne('/api/auth/runners').flush([{ id: 'runner_1', name: 'coding-host', createdAt: '2026-10-20T09:00:00Z', credentials: [] }]);
    await Promise.resolve();
    await Promise.resolve();
  }

  it('loads the overview and the project names', async () => {
    const loading = service.load();
    await flushLoad();
    await loading;
    expect(service.state()).toBe('ready');
    expect(service.projects()).toEqual([{ id: 'PROJ-001', displayName: 'Website' }]);
    expect(service.pendingInvitations().has('usr_dev')).toBe(true);
    expect(service.runners().map(runner => runner.id)).toEqual(['runner_1']);
  });

  it('reports a denied overview without an error dump', async () => {
    const loading = service.load();
    http.expectOne('/api/auth/iam').flush({ error: 'permission-denied' }, { status: 403, statusText: 'Forbidden' });
    http.expectOne('/api/projects').flush([]);
    http.expectOne('/api/auth/runner-enrollments').flush({ error: 'permission-denied' }, { status: 403, statusText: 'Forbidden' });
    http.expectOne('/api/auth/runners').flush([]);
    await loading;
    expect(service.state()).toBe('denied');
    expect(service.error()).toBe('Your account cannot view access settings.');
  });

  it('invites through the existing invitation route with explicit bindings and reloads', async () => {
    const grant = { role: 'developer', scope: { kind: 'project' as const, project: 'PROJ-001' } };
    const invited = service.invite('new.user', 'New User', [grant]);
    const request = http.expectOne('/api/auth/invitations');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ username: 'new.user', displayName: 'New User', role: '', projects: null, bindings: [grant] });
    request.flush({ user: { username: 'new.user' }, signInLink: '/?invite=inv.x', expiresAt: '2026-10-21T09:00:00Z' });
    await Promise.resolve();
    await flushLoad();
    expect((await invited)?.signInLink).toBe('/?invite=inv.x');
  });

  it('disables a user with a body that cannot rewrite role bindings', async () => {
    const disabling = service.setUserDisabled('usr_dev', true);
    const request = http.expectOne('/api/auth/users/usr_dev');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ disabled: true });
    request.flush({});
    await Promise.resolve();
    await flushLoad();
    await disabling;
  });

  it('keeps the server message when a mutation is refused', async () => {
    const adding = service.addBinding('user', 'usr_owner', 'owner', { kind: 'project', project: 'PROJ-001' });
    http.expectOne('/api/auth/iam/bindings').flush(
      { error: 'invalid-scope', message: 'Owner cannot be bound to a project scope.' }, { status: 400, statusText: 'Bad Request' });
    expect(await adding).toBeNull();
    expect(service.error()).toBe('Owner cannot be bound to a project scope.');
    expect(service.busy()).toBe(false);
  });
});
