import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthSessionState } from './auth.service';
import { LocalOwnerSessionService } from './local-owner-session.service';

describe('LocalOwnerSessionService', () => {
  let state: AuthSessionState;
  let service: LocalOwnerSessionService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    state = TestBed.inject(AuthSessionState);
    service = TestBed.inject(LocalOwnerSessionService);
  });

  it('holds an action of a local session without sign-in until the owner signs in', () => {
    state.status.set({ profile: 'local', bootstrapRequired: true, authenticated: false });
    const action = vi.fn();
    service.run('Production approval needs the owner account.', action);
    expect(action).not.toHaveBeenCalled();
    expect(service.request()?.purpose).toBe('Production approval needs the owner account.');
    service.complete();
    expect(action).toHaveBeenCalledTimes(1);
    expect(service.request()).toBeNull();
  });

  it('drops a held action on cancel', () => {
    state.status.set({ profile: 'local', bootstrapRequired: false, authenticated: false });
    const action = vi.fn();
    service.run('Needs the owner.', action);
    service.cancel();
    service.complete();
    expect(action).not.toHaveBeenCalled();
  });

  it.each([
    { profile: 'local', bootstrapRequired: false, authenticated: true },
    { profile: 'networked', bootstrapRequired: false, authenticated: true },
  ] as const)('runs at once with a signed-in session (%o)', status => {
    state.status.set(status);
    const action = vi.fn();
    service.run('Needs the owner.', action);
    expect(action).toHaveBeenCalledTimes(1);
    expect(service.request()).toBeNull();
  });
});
