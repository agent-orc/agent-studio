import { describe, expect, it, beforeEach } from 'vitest';
import { provideHttpClient, HttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthSessionState, type AuthUser } from './auth.service';
import { shellReadInterceptor } from './shell-read.interceptor';
import { ShellAccessService } from './shell-access.service';

const user = (role: AuthUser['role']): AuthUser => ({
  id: 'user', username: 'user', displayName: 'User', role, projects: ['demo'],
  disabled: false, mustChangePassword: false,
});

describe('shell read access', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(withInterceptors([shellReadInterceptor])), provideHttpClientTesting(),
    ] });
  });

  for (const role of ['contributor', 'viewer'] as const) {
    it(`keeps ${role} board reads and refuses operator reads before the network`, () => {
      TestBed.inject(AuthSessionState).status.set({
        profile: 'networked', bootstrapRequired: false, authenticated: true, user: user(role),
      });
      const http = TestBed.inject(HttpClient);
      const pending = TestBed.inject(HttpTestingController);
      const access = TestBed.inject(ShellAccessService);
      expect(access.operator()).toBe(false);

      let boardRead = false;
      http.get('/api/v1/studio/board').subscribe(() => boardRead = true);
      pending.expectOne('/api/v1/studio/board').flush({});
      expect(boardRead).toBe(true);
      http.get('/api/projects').subscribe();
      pending.expectOne('/api/projects').flush([]);
      http.get('/api/v1/studio/runner/project:demo/orchestrator-chat').subscribe();
      pending.expectOne('/api/v1/studio/runner/project:demo/orchestrator-chat').flush([]);

      let deniedCompleted = false;
      http.get('/api/runner/status').subscribe({ complete: () => deniedCompleted = true });
      http.get('/api/v1/studio/runner/status').subscribe();
      http.get('/api/v1/workspaces').subscribe();
      http.get('/api/v1/management/remote-hosts').subscribe();
      http.get('/api/usage/cockpit').subscribe();
      pending.expectNone('/api/runner/status');
      pending.expectNone('/api/v1/studio/runner/status');
      pending.expectNone('/api/v1/workspaces');
      pending.expectNone('/api/v1/management/remote-hosts');
      pending.expectNone('/api/usage/cockpit');
      expect(deniedCompleted).toBe(true);
      pending.verify();
    });
  }

  it('lets an operator load runner status', () => {
    TestBed.inject(AuthSessionState).status.set({
      profile: 'networked', bootstrapRequired: false, authenticated: true, user: user('operator'),
    });
    const http = TestBed.inject(HttpClient);
    const pending = TestBed.inject(HttpTestingController);
    http.get('/api/runner/status').subscribe();
    pending.expectOne('/api/runner/status').flush({ projects: {} });
    pending.verify();
  });
});
