import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthSessionState, type AuthUser } from './auth.service';
import { ConnectionStatusService } from './connection-status.service';
import { JobsHubClient } from './jobs-hub-client.service';

describe('ConnectionStatusService', () => {
  const user = (role: AuthUser['role'], projects: string[]): AuthUser => ({
    id: 'usr-1', username: 'dana', displayName: 'Dana', role, projects, disabled: false, mustChangePassword: false,
  });

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [
      provideZonelessChangeDetection(),
      { provide: JobsHubClient, useValue: { connected: signal(false) } },
    ] });
  });
  afterEach(() => vi.useRealTimers());

  function offlineAfterGraceFor(account: AuthUser): boolean {
    TestBed.inject(AuthSessionState).status.set({ profile: 'networked', bootstrapRequired: false, authenticated: true, user: account });
    const status = TestBed.inject(ConnectionStatusService);
    TestBed.tick();
    vi.advanceTimersByTime(5_000);
    return status.offline();
  }

  it('declares a refused socket offline for an account with a project', () => {
    expect(offlineAfterGraceFor(user('contributor', ['PROJ-1']))).toBe(true);
  });

  it('does not report an outage to an account without a project (AGT-3668)', () => {
    expect(offlineAfterGraceFor(user('viewer', []))).toBe(false);
  });
});
