import { Injectable, computed, signal } from '@angular/core';

/**
 * How fresh the git-derived state behind the board is.
 *
 * The backend computes merge signals, integration verdicts and repository
 * inventories in one background index (AGT-2726) and answers requests from its
 * last completed capture. Every such response carries `X-Git-State-At` and
 * `X-Git-State-Stale`; {@link gitStateStampInterceptor} funnels them here so the
 * board can say how old the picture is without any call site changing.
 */
export interface GitStateStamp {
  /** Capture time of the oldest repository the response covered. */
  readonly at: Date | null;
  /** A change is already known but not folded into that capture yet. */
  readonly stale: boolean;
}

@Injectable({ providedIn: 'root' })
export class GitStateStampService {
  private readonly _stamp = signal<GitStateStamp | null>(null);

  /** The stamp from the most recent response that carried one, or null. */
  readonly stamp = this._stamp.asReadonly();

  /** True once a response has reported a stamp, so the UI can stay silent before that. */
  readonly known = computed(() => this._stamp() !== null);

  record(at: string | null, stale: string | null): void {
    if (at === null && stale === null) return;
    const parsed = at ? new Date(at) : null;
    this._stamp.set({
      at: parsed && !Number.isNaN(parsed.getTime()) ? parsed : null,
      stale: stale === 'true',
    });
  }
}
