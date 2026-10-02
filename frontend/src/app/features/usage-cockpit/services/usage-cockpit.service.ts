import { HttpClient, HttpParams } from '@angular/common/http';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';

import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { UsageCockpitResponse } from '../models/usage-cockpit.model';

/** Shared refresh cadence while the page is visible (Dossier "Data contract"). */
export const USAGE_COCKPIT_REFRESH_MS = 30_000;

/**
 * One shared `GET /api/usage/cockpit` reader for the header (HUC-S4). Polls
 * every 30 seconds while visible, refreshes on return, keeps the last good
 * snapshot on failure (the chips turn stale from each source's own TTL) and
 * drops responses for a workspace that is no longer selected.
 */
@Injectable({ providedIn: 'root' })
export class UsageCockpitService {
  private readonly http = inject(HttpClient);

  private readonly snapshotSignal = signal<UsageCockpitResponse | null>(null);
  readonly snapshot = this.snapshotSignal.asReadonly();

  private workspaceId: string | null = null;
  private timer: VisibleIntervalHandle | null = null;
  private inflight: Subscription | null = null;
  private consumers = 0;
  private readonly onVisible = () => {
    if (!document.hidden) this.refresh();
  };

  /** Starts polling for the caller's lifetime. */
  connect(destroyRef: DestroyRef): void {
    if (this.consumers++ === 0) {
      this.refresh();
      this.timer = setVisibleInterval(() => this.refresh(), USAGE_COCKPIT_REFRESH_MS);
      document.addEventListener('visibilitychange', this.onVisible);
    }
    destroyRef.onDestroy(() => {
      if (--this.consumers > 0) return;
      clearVisibleInterval(this.timer);
      this.timer = null;
      this.inflight?.unsubscribe();
      document.removeEventListener('visibilitychange', this.onVisible);
    });
  }

  setWorkspace(workspaceId: string | null): void {
    if (workspaceId === this.workspaceId) return;
    this.workspaceId = workspaceId;
    this.snapshotSignal.set(null);
    if (this.consumers > 0) this.refresh();
  }

  refresh(): void {
    const requested = this.workspaceId;
    let params = new HttpParams();
    if (requested) params = params.set('workspaceId', requested);
    this.inflight?.unsubscribe();
    this.inflight = this.http.get<UsageCockpitResponse>('/api/usage/cockpit', { params }).subscribe({
      next: snapshot => {
        if (requested !== this.workspaceId) return;
        if (requested && snapshot.workspaceId && snapshot.workspaceId !== requested) return;
        this.snapshotSignal.set(snapshot);
      },
      // Keep the last good snapshot; each chip ages it into its stale state.
      error: () => undefined,
    });
  }
}
