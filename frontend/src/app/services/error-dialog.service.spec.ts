import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { beforeEach, describe, expect, it } from 'vitest';
import { AuthSessionState, type AuthUser } from './auth.service';
import { ErrorDialogService } from './error-dialog.service';

describe('ErrorDialogService for an account without a project (AGT-3668)', () => {
  const user = (projects: string[]): AuthUser => ({
    id: 'usr-1', username: 'dana', displayName: 'Dana', role: 'viewer', projects, disabled: false, mustChangePassword: false,
  });
  const refused = new HttpErrorResponse({ status: 403, url: '/api/watch-paths', error: { error: 'permission-denied' } });

  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }));

  function shown(projects: string[], error: unknown): boolean {
    TestBed.inject(AuthSessionState).status.set({ profile: 'networked', bootstrapRequired: false, authenticated: true, user: user(projects) });
    const dialogs = TestBed.inject(ErrorDialogService);
    dialogs.show(error, { title: 'Failed to load projects' });
    return dialogs.activeError() !== null;
  }

  it('shows no dialog for a refused read; the shell sentence explains', () => {
    expect(shown([], refused)).toBe(false);
  });

  it('still shows other failures and every refusal to an account with a project', () => {
    expect(shown([], new HttpErrorResponse({ status: 500, url: '/api/watch-paths' }))).toBe(true);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    expect(shown(['PROJ-1'], refused)).toBe(true);
  });
});
