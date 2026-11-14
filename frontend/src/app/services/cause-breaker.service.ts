import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, type Observable } from 'rxjs';

/** One card held behind a cause card (backend `CauseBreakerHeldCard`). */
export interface CauseBreakerHeldCard {
  taskKey: string;
  key: string;
  title?: string | null;
  projectName?: string | null;
  watchPath?: string | null;
  since: string;
  deliveredAt?: string | null;
  /** The card "Re-check now" releases: the newest waiting delivery. */
  recheckFirst: boolean;
}

/** The open breaker a cause card owns (backend `CauseBreakerCauseView`). */
export interface CauseBreakerCauseView {
  fingerprint: string;
  failureClass: string;
  summary: string;
  toolchain: string;
  causeKey?: string | null;
  openedAt?: string | null;
  probeTaskKey?: string | null;
  probeKey?: string | null;
  probeState?: 'running' | 'passed' | 'failed' | null;
  /** The breaker has a close reason and is only finishing its releases. */
  resolving: boolean;
  held: CauseBreakerHeldCard[];
}

/**
 * AGT-3617: the operator surface of the fleet-wide cause breaker. Before this
 * no UI called `/api/cause-breakers`, so recovery needed the API or deleting
 * the card whose delivery broke the review.
 */
@Injectable({ providedIn: 'root' })
export class CauseBreakerService {
  private readonly http = inject(HttpClient);

  /** The open breaker this card is the cause card of, or null. */
  forCause(causeKey: string): Observable<CauseBreakerCauseView | null> {
    return this.http
      .get<{ breaker: CauseBreakerCauseView | null }>(`/api/cause-breakers/by-cause/${encodeURIComponent(causeKey)}`)
      .pipe(map(response => response?.breaker ?? null));
  }

  /** Releases one held card as a re-check: the newest delivery, or the named card. */
  recheck(fingerprint: string, taskKey?: string): Observable<unknown> {
    return this.http.post(`/api/cause-breakers/${encodeURIComponent(fingerprint)}/probe`,
      taskKey ? { taskKey } : {});
  }
}
