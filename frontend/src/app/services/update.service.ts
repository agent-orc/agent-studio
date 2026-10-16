import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { RollbackRequest, RollbackResponse, TriggerRequest, TriggerResponse, UpdateHistoryEntry, UpdateStatus } from '../models/update-service.model';
import { PublicDemoModeService } from './public-demo-mode.service';

export function resolveUpdateServiceUrl(href: string, configured?: string): string | null {
  if (configured) return configured;
  const url = new URL(href);
  if (url.protocol === 'https:') return null;
  return `${url.protocol}//${url.hostname}:5039`;
}

/**
 * Talks to the standalone UpdateService (default port 5039). The endpoint
 * is *not* proxied through ng-serve: the whole point of the separate
 * process is that it stays reachable while the main backend (port 5031)
 * restarts. We therefore hit it via an absolute URL that matches the
 * caller's hostname and a configurable port.
 *
 * Polling cadence is dynamic: 30 s when idle, 2 s when an update is in
 * flight. The faster cadence kicks in automatically the moment a status
 * read reports `isRunning=true` and the moment a trigger response says
 * the orchestrator is on a non-idle phase.
 */
@Injectable({ providedIn: 'root' })
export class UpdateClientService {
  private readonly http = inject(HttpClient);
  private readonly publicDemo = inject(PublicDemoModeService);

  /** Resolves the base URL for the UpdateService (host of the FE, port 5039). */
  private readonly baseUrl = (() => {
    if (typeof window === 'undefined') return null;
    // A hosted HTTPS Studio does not expose the machine-local UpdateService.
    // An operator can opt in with an explicit same-origin or HTTPS endpoint.
    const configured = (window as Window & { __AGENT_STUDIO_UPDATE_SERVICE_URL__?: string })
      .__AGENT_STUDIO_UPDATE_SERVICE_URL__;
    return resolveUpdateServiceUrl(window.location.href, configured);
  })();

  readonly enabled = this.baseUrl !== null;

  /** Last status snapshot from /update/status; null until first poll completes. */
  readonly status = signal<UpdateStatus | null>(null);

  /** True when the UpdateService itself was unreachable on the last poll. */
  readonly serviceUnreachable = signal(false);

  /** Convenience: any phase that means the orchestrator is mid-update. */
  readonly isRunning = computed(() => this.status()?.isRunning ?? false);

  /** Convenience: how far behind origin/main we are; 0 when unknown / synced. */
  readonly behindBy = computed(() => this.status()?.behindBy ?? 0);

  /** Loaded backend build identity; static VERSION is only the pre-probe fallback. */
  readonly productVersion = computed(() => this.status()?.productVersion ?? '');

  /** Stable checkout HEAD retained for backwards compatibility. */
  readonly headLocal = computed(() => this.status()?.headLocal ?? '');

  /**
   * UI feature gate: while an update is running, mutations should be
   * blocked at the call site (job create / move / mode change) so the
   * banner and the actual behaviour stay aligned.
   */
  readonly mutationsBlocked = computed(() => this.isRunning() || this.publicDemo.readOnly());

  /** Whether the Update Center drawer is currently open. */
  readonly centerOpen = signal(false);
  openCenter(): void { this.centerOpen.set(true); }
  closeCenter(): void { this.centerOpen.set(false); }

  private pollTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Start polling immediately; refresh cadence whenever phase changes.
    if (this.enabled) this.scheduleNextPoll(0);
    effect(() => {
      // touch isRunning so the effect re-fires on phase transitions
      void this.isRunning();
    });
  }

  /** Manually trigger an update. Resolves with the trigger response. */
  async trigger(reason: string | null = null, force = false): Promise<TriggerResponse> {
    if (!this.baseUrl) throw new Error('Update Service is not configured.');
    const body: TriggerRequest = { reason: reason ?? undefined, force };
    const resp = await firstValueFrom(
      this.http.post<TriggerResponse>(`${this.baseUrl}/update/trigger`, body)
    );
    // Bump cadence and refresh now so the banner reflects the new phase
    // before the next normal poll fires.
    this.refreshNow();
    return resp;
  }

  /** Force an immediate status refresh (used after a trigger). */
  async refreshNow(): Promise<void> {
    if (!this.baseUrl) return;
    await this.pollOnce();
    this.scheduleNextPoll(this.isRunning() ? 2_000 : 30_000);
  }

  /** Latest history entries; never auto-polled to keep traffic small. */
  async readHistory(max = 20): Promise<UpdateHistoryEntry[]> {
    if (!this.baseUrl) return [];
    return await firstValueFrom(
      this.http.get<UpdateHistoryEntry[]>(`${this.baseUrl}/update/history?max=${max}`)
    );
  }

  /**
   * ADR-0031 manual rollback. Replays phases 5+6+7 against the SHA recorded
   * in the named run's pre-snapshot. Operator-driven; no automatic call.
   */
  async rollback(runId: string): Promise<RollbackResponse> {
    if (!this.baseUrl) throw new Error('Update Service is not configured.');
    const body: RollbackRequest = { runId };
    const resp = await firstValueFrom(
      this.http.post<RollbackResponse>(`${this.baseUrl}/update/rollback`, body)
    );
    this.refreshNow();
    return resp;
  }

  // ─── internals ──────────────────────────────────────────────────────────

  private scheduleNextPoll(delayMs: number): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = setTimeout(() => {
      this.pollOnce()
        .catch(() => { /* status() already reflects the failure */ })
        .finally(() => this.scheduleNextPoll(this.isRunning() ? 2_000 : 30_000));
    }, delayMs);
  }

  private async pollOnce(): Promise<void> {
    if (!this.baseUrl) return;
    try {
      const next = await firstValueFrom(this.http.get<UpdateStatus>(`${this.baseUrl}/update/status`));
      this.status.set(next);
      this.serviceUnreachable.set(false);
    } catch {
      // Don't blank out the last-known status; a transient failure is more
      // useful as "stale snapshot + unreachable flag" than as a null UI.
      this.serviceUnreachable.set(true);
    }
  }
}
