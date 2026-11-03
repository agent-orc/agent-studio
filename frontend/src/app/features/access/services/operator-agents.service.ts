import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom, type Observable } from 'rxjs';
import type {
  AuditIntegrity,
  OneTimeOperatorSession,
  OperatorActivity,
  OperatorAgent,
  OperatorSession,
} from '../models/operator-agent.model';

const OPERATORS = '/api/auth/iam/operator-agents';
const AUDIT = '/api/auth/audit';
/** Newest entries the activity view reads per request. */
export const OPERATOR_ACTIVITY_LIMIT = 200;

type LoadState = 'idle' | 'loading' | 'ready' | 'denied' | 'error';

/**
 * Owner-only operator agents and their audit trail. Every read and mutation is
 * refused for anyone but the Owner, so a refusal hides the operator sections
 * instead of showing an error.
 */
@Injectable({ providedIn: 'root' })
export class OperatorAgentsService {
  private readonly http = inject(HttpClient);
  private activityRequest = 0;

  readonly agents = signal<readonly OperatorAgent[]>([]);
  readonly state = signal<LoadState>('idle');
  /** True once the server has answered as for an Owner; a refusal or a missing route clears it. */
  readonly available = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly activity = signal<OperatorActivity | null>(null);
  readonly activityState = signal<LoadState>('idle');
  readonly activityError = signal<string | null>(null);
  /** The session the activity view is narrowed to; null shows every session. */
  readonly sessionFilter = signal<string | null>(null);
  readonly verifying = signal(false);

  async load(): Promise<void> {
    this.state.set('loading');
    this.error.set(null);
    try {
      this.agents.set(await firstValueFrom(this.http.get<OperatorAgent[]>(OPERATORS)));
      this.available.set(true);
      this.state.set('ready');
    } catch (failure: unknown) {
      if (this.refused(failure)) return;
      this.state.set('error');
      this.error.set(message(failure, 'Operator agents could not be loaded.'));
    }
  }

  async loadActivity(): Promise<void> {
    const request = ++this.activityRequest;
    const sessionId = this.sessionFilter();
    let params = new HttpParams().set('limit', OPERATOR_ACTIVITY_LIMIT);
    if (sessionId) params = params.set('sessionId', sessionId);
    this.activityState.set('loading');
    this.activityError.set(null);
    try {
      const activity = await firstValueFrom(this.http.get<OperatorActivity>(`${AUDIT}/operator-activity`, { params }));
      if (request !== this.activityRequest) return;
      this.activity.set(activity);
      this.activityState.set('ready');
    } catch (failure: unknown) {
      if (request !== this.activityRequest || this.refused(failure)) return;
      this.activityState.set('error');
      this.activityError.set(message(failure, 'Operator activity could not be loaded.'));
    }
  }

  filterSession(sessionId: string | null): Promise<void> {
    this.sessionFilter.set(sessionId);
    return this.loadActivity();
  }

  /** Re-walks the hash chain on the server and shows the fresh result. */
  async verify(): Promise<AuditIntegrity | null> {
    this.verifying.set(true);
    this.activityError.set(null);
    try {
      const integrity = await firstValueFrom(this.http.post<AuditIntegrity>(`${AUDIT}/verify`, {}));
      const activity = this.activity();
      if (activity) this.activity.set({ ...activity, integrity });
      return integrity;
    } catch (failure: unknown) {
      if (!this.refused(failure, false)) this.activityError.set(message(failure, 'The audit chain could not be verified.'));
      return null;
    } finally {
      this.verifying.set(false);
    }
  }

  create(name: string, description: string): Promise<OperatorAgent | null> {
    return this.mutate(this.http.post<OperatorAgent>(OPERATORS, description ? { name, description } : { name }));
  }

  /** Disables the agent; the server revokes every session with it. */
  disable(agentId: string): Promise<unknown> {
    return this.mutate(this.http.delete(`${OPERATORS}/${encodeURIComponent(agentId)}`));
  }

  startSession(agentId: string, name: string, lifetimeHours: number): Promise<OneTimeOperatorSession | null> {
    return this.mutate(this.http.post<OneTimeOperatorSession>(
      `${OPERATORS}/${encodeURIComponent(agentId)}/sessions`, name ? { name, lifetimeHours } : { lifetimeHours }));
  }

  renewSession(agentId: string, sessionId: string, lifetimeHours: number): Promise<OperatorSession | null> {
    return this.mutate(this.http.post<OperatorSession>(
      `${OPERATORS}/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(sessionId)}/renew`, { lifetimeHours }));
  }

  revokeSession(agentId: string, sessionId: string): Promise<unknown> {
    return this.mutate(this.http.delete(
      `${OPERATORS}/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(sessionId)}`));
  }

  /** Runs one mutation, then reloads the agents and an already open activity view. */
  private async mutate<T>(request: Observable<T>): Promise<T | null> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await firstValueFrom(request);
      await Promise.all([this.load(), this.activityState() === 'idle' ? Promise.resolve() : this.loadActivity()]);
      return result;
    } catch (failure: unknown) {
      if (!this.refused(failure, false)) this.error.set(message(failure, 'The change could not be saved.'));
      return null;
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * A refusal clears every cached value and hides the sections. A read that
   * finds no route means this server has no operator agents; a mutation that
   * finds no record is an ordinary error.
   */
  private refused(failure: unknown, missingHides = true): boolean {
    const status = failure instanceof HttpErrorResponse ? failure.status : 0;
    if (status !== 401 && status !== 403 && !(missingHides && status === 404)) return false;
    this.agents.set([]);
    this.activity.set(null);
    this.available.set(false);
    this.state.set('denied');
    this.activityState.set('denied');
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
