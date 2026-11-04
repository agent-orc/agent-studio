import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom, type Observable } from 'rxjs';
import { AuthService } from '../../../services/auth.service';
import { getPasskey } from '../../../services/webauthn';
import type { CreateStandingGrant, OperatorCatalogue, StandingGrant } from '../models/standing-grant.model';

const CATALOGUE = '/api/auth/iam/operator-catalogue';
const GRANTS = '/api/auth/iam/standing-grants';

type LoadState = 'idle' | 'loading' | 'ready' | 'denied' | 'error';

/** A fresh second factor for one grant change: an authenticator or recovery code, or a passkey. */
export type GrantStepUp = { code: string } | { passkey: true };

/**
 * The operator operation catalogue and the Owner's standing grants (SM-4).
 * Issuing or renewing a grant needs a fresh step-up, which this service runs
 * right before the change; revoking needs none. A refusal hides the section.
 */
@Injectable({ providedIn: 'root' })
export class StandingGrantsService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);

  readonly catalogue = signal<OperatorCatalogue | null>(null);
  readonly grants = signal<readonly StandingGrant[]>([]);
  readonly state = signal<LoadState>('idle');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  async load(): Promise<void> {
    this.state.set('loading');
    this.error.set(null);
    try {
      const [catalogue, grants] = await Promise.all([
        firstValueFrom(this.http.get<OperatorCatalogue>(CATALOGUE)),
        firstValueFrom(this.http.get<StandingGrant[]>(GRANTS)),
      ]);
      this.catalogue.set(catalogue);
      this.grants.set(grants);
      this.state.set('ready');
    } catch (failure: unknown) {
      if (this.refused(failure)) return;
      this.state.set('error');
      this.error.set(message(failure, 'Standing grants could not be loaded.'));
    }
  }

  issue(grant: CreateStandingGrant, stepUp: GrantStepUp): Promise<StandingGrant | null> {
    return this.mutate(() => this.http.post<StandingGrant>(GRANTS, grant), stepUp);
  }

  renew(grantId: string, expiresInDays: number, stepUp: GrantStepUp): Promise<StandingGrant | null> {
    return this.mutate(() => this.http.post<StandingGrant>(
      `${GRANTS}/${encodeURIComponent(grantId)}/renew`, { expiresInDays }), stepUp);
  }

  /** Revocation takes authority away and needs no second factor. */
  revoke(grantId: string, reason: string): Promise<StandingGrant | null> {
    return this.mutate(() => this.http.post<StandingGrant>(
      `${GRANTS}/${encodeURIComponent(grantId)}/revoke`, reason ? { reason } : {}), null);
  }

  private async mutate<T>(request: () => Observable<T>, stepUp: GrantStepUp | null): Promise<T | null> {
    this.busy.set(true);
    this.error.set(null);
    try {
      if (stepUp) await this.verify(stepUp);
      const result = await firstValueFrom(request());
      await this.load();
      return result;
    } catch (failure: unknown) {
      if (!this.refused(failure, false)) this.error.set(message(failure, 'The grant could not be saved.'));
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

  /** A read refusal or a missing route hides the section; a refused change (step-up, owner) is an ordinary error. */
  private refused(failure: unknown, hide = true): boolean {
    const status = failure instanceof HttpErrorResponse ? failure.status : 0;
    if (!hide || (status !== 401 && status !== 403 && status !== 404)) return false;
    this.catalogue.set(null);
    this.grants.set([]);
    this.state.set('denied');
    return true;
  }
}

function message(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { message?: unknown } | null;
    if (body && typeof body.message === 'string' && body.message) return body.message;
  }
  return fallback;
}
