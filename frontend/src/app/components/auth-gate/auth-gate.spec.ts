import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, throwError } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { AUTH_GATE_RECONNECTING, AuthGateComponent, authGateFailure } from './auth-gate';
import { AuthService, AuthStatus } from '../../services/auth.service';

const enrollment: AuthStatus = {
  profile: 'networked', bootstrapRequired: false, authenticated: true, secondFactorEnrollmentRequired: true,
  user: { id: 'usr_owner', username: 'owner', displayName: 'Owner', role: 'owner',
    projects: [], disabled: false, mustChangePassword: false },
};

// The error the offline guard raises instead of sending a request.
const offlineBlocked = () => new HttpErrorResponse({
  status: 0, statusText: 'Backend offline', url: '/api/auth/totp/confirm',
  error: { error: 'Backend offline — action blocked until the connection returns.' },
});

function mount(confirmTotp: (code: string) => unknown) {
  TestBed.configureTestingModule({
    imports: [AuthGateComponent],
    providers: [provideZonelessChangeDetection(), { provide: AuthService, useValue: {
      status: signal(enrollment), loading: signal(false), ownerRecoveryCode: signal(null), confirmTotp,
    } }],
  });
  const gate = TestBed.createComponent(AuthGateComponent).componentInstance;
  gate.totpSecret.set('SECRET');
  return gate;
}

describe('authGateFailure', () => {
  it('says Studio is reconnecting when the request never reached the server', () => {
    expect(authGateFailure(offlineBlocked(), 'Authenticator code is invalid.')).toBe(AUTH_GATE_RECONNECTING);
    expect(authGateFailure(new HttpErrorResponse({ status: 0 }), 'Passkey setup failed.')).toBe(AUTH_GATE_RECONNECTING);
  });

  it('keeps the server message, then the fallback, for an answered request', () => {
    expect(authGateFailure(new HttpErrorResponse({ status: 400, error: { message: 'The authenticator code is invalid.' } }),
      'Authenticator code is invalid.')).toBe('The authenticator code is invalid.');
    expect(authGateFailure(new HttpErrorResponse({ status: 500 }), 'Sign-in failed.')).toBe('Sign-in failed.');
    expect(authGateFailure(new Error('The passkey prompt was dismissed.'), 'Passkey setup failed.'))
      .toBe('The passkey prompt was dismissed.');
  });
});

describe('AuthGateComponent second-factor enrollment', () => {
  it('reports an offline-blocked confirmation as reconnecting, not as a wrong code', () => {
    const gate = mount(() => throwError(offlineBlocked));
    gate.secondFactorCode = '123456';
    gate.submit();
    expect(gate.error()).toBe(AUTH_GATE_RECONNECTING);
  });

  it('clears the previous error as soon as the next attempt starts', () => {
    const attempts: Subject<{ recoveryCodes: string[] }>[] = [];
    const gate = mount(() => { const attempt = new Subject<{ recoveryCodes: string[] }>(); attempts.push(attempt); return attempt; });
    gate.secondFactorCode = '000000';
    gate.submit();
    attempts[0].error(new HttpErrorResponse({ status: 400, error: { message: 'The authenticator code is invalid.' } }));
    expect(gate.error()).toBe('The authenticator code is invalid.');

    gate.secondFactorCode = '123456';
    gate.submit();
    expect(gate.error()).toBe('');
    attempts[1].next({ recoveryCodes: ['code-1'] });
    expect(gate.recoveryCodes()).toEqual(['code-1']);
  });
});
