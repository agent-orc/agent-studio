import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../../services/auth.service';
import { getPasskey } from '../../../services/webauthn';
import type { TerminalSignInConfirmation, TerminalSignInLookup } from '../models/terminal-sign-in.model';
import type { GrantStepUp } from './standing-grants.service';

const SIGN_INS = '/api/auth/iam/operator-sign-ins';

export interface TerminalSignInChoice {
  code: string;
  operatorAgentId: string;
  classes: readonly string[];
  lifetimeHours: number;
}

/**
 * The Owner's side of a terminal sign-in (ES-3): look up an entered code,
 * confirm it after a fresh step-up, or deny it. The server counts wrong codes
 * and locks code entry after five within 15 minutes; its messages are shown
 * as they come.
 */
@Injectable({ providedIn: 'root' })
export class TerminalSignInService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);

  readonly lookup = signal<TerminalSignInLookup | null>(null);
  readonly confirmed = signal<TerminalSignInConfirmation | null>(null);
  readonly denied = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** The server's refusal code, for example `signin-locked`. */
  readonly errorCode = signal<string | null>(null);

  reset(): void {
    this.lookup.set(null);
    this.confirmed.set(null);
    this.denied.set(false);
    this.error.set(null);
    this.errorCode.set(null);
  }

  async find(code: string): Promise<TerminalSignInLookup | null> {
    return this.run('The code could not be checked.', async () => {
      const found = await firstValueFrom(this.http.post<TerminalSignInLookup>(`${SIGN_INS}/lookup`, { code }));
      this.lookup.set(found);
      return found;
    }, () => this.lookup.set(null));
  }

  async confirm(choice: TerminalSignInChoice, stepUp: GrantStepUp): Promise<TerminalSignInConfirmation | null> {
    return this.run('The sign-in was not confirmed.', async () => {
      await this.verify(stepUp);
      const confirmed = await firstValueFrom(this.http.post<TerminalSignInConfirmation>(`${SIGN_INS}/confirm`, choice));
      this.confirmed.set(confirmed);
      return confirmed;
    });
  }

  async deny(code: string): Promise<boolean> {
    return (await this.run('The sign-in was not denied.', async () => {
      await firstValueFrom(this.http.post(`${SIGN_INS}/deny`, { code }));
      this.denied.set(true);
      return true;
    })) ?? false;
  }

  private async run<T>(fallback: string, action: () => Promise<T>, onError?: () => void): Promise<T | null> {
    this.busy.set(true);
    this.error.set(null);
    this.errorCode.set(null);
    try {
      return await action();
    } catch (failure: unknown) {
      const body = failure instanceof HttpErrorResponse ? failure.error as { error?: unknown; message?: unknown } | null : null;
      this.errorCode.set(typeof body?.error === 'string' ? body.error : null);
      this.error.set(typeof body?.message === 'string' && body.message ? body.message
        : failure instanceof HttpErrorResponse && failure.status === 0 ? 'Studio is reconnecting; try again.' : fallback);
      onError?.();
      return null;
    } finally {
      this.busy.set(false);
    }
  }

  private async verify(stepUp: GrantStepUp): Promise<void> {
    if ('passkey' in stepUp) {
      const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
      await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
    } else {
      await firstValueFrom(this.auth.stepUp(stepUp.code));
    }
  }
}
