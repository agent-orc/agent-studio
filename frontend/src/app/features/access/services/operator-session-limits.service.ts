import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../../services/auth.service';
import { getPasskey } from '../../../services/webauthn';

export interface OperatorSessionLimits {
  defaultLifetimeHours: number;
  ownerLifetimeHours: number | null;
  adminLifetimeHours: number | null;
  idleHours: number;
}
export const SESSION_LIFETIMES = [8, 24, 168, 720, 0] as const;
export function lifetimeLabel(hours: number | null | undefined): string {
  return hours === 0 || hours == null ? 'Until revoked' : hours === 168 ? '7 days' : hours === 720 ? '30 days' : `${hours} hours`;
}
const LIMITS = '/api/auth/iam/session-limits';

@Injectable({ providedIn: 'root' })
export class OperatorSessionLimitsService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly limits = signal<OperatorSessionLimits | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  async load(): Promise<void> {
    try { this.limits.set(await firstValueFrom(this.http.get<OperatorSessionLimits>(LIMITS))); }
    catch { this.error.set('Session limits could not be loaded.'); }
  }

  async save(limits: OperatorSessionLimits, factor: string, agentId?: string, hours?: number | null): Promise<boolean> {
    this.busy.set(true); this.error.set(null);
    try {
      if (factor.trim()) await firstValueFrom(this.auth.stepUp(factor.trim()));
      else {
        const options = await firstValueFrom(this.auth.passkeyStepUpOptions());
        await firstValueFrom(this.auth.stepUp(undefined, await getPasskey(options)));
      }
      const route = agentId ? `/api/auth/iam/operator-agents/${encodeURIComponent(agentId)}/lifetime-limit` : LIMITS;
      await firstValueFrom(this.http.put(route, agentId ? { lifetimeHours: hours } : limits));
      await this.load();
      return true;
    } catch (error: unknown) {
      this.error.set(error instanceof HttpErrorResponse && typeof error.error?.message === 'string'
        ? error.error.message : 'Session limits could not be saved.');
      return false;
    } finally { this.busy.set(false); }
  }
}
