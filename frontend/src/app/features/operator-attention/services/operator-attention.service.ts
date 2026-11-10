import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { groupAttention, type OperatorAttentionItem, type OperatorAttentionResponse } from '../models/operator-attention.model';

const POLL_INTERVAL_MS = 15_000;

/**
 * The attention list (AGT-3576): the open facts of the operator decision
 * stream, read as one current list. The header count is the length of the
 * same array the list renders, so the two cannot disagree (R3).
 */
@Injectable({ providedIn: 'root' })
export class OperatorAttentionService {
  private readonly http = inject(HttpClient);
  private readonly _items = signal<readonly OperatorAttentionItem[]>([]);
  private readonly _loaded = signal(false);
  private readonly _error = signal(false);
  private timer: ReturnType<typeof setInterval> | null = null;
  private inFlight = false;

  readonly items = this._items.asReadonly();
  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();
  readonly count = computed(() => this._items().length);
  readonly groups = computed(() => groupAttention(this._items()));

  start(): void {
    if (this.timer !== null) return;
    this.refresh();
    this.timer = setInterval(() => {
      if (!document.hidden) this.refresh();
    }, POLL_INTERVAL_MS);
  }

  stop(): void {
    if (this.timer === null) return;
    clearInterval(this.timer);
    this.timer = null;
  }

  refresh(): void {
    if (this.inFlight) return;
    this.inFlight = true;
    this.http.get<OperatorAttentionResponse>('/api/operator/attention').subscribe({
      next: response => {
        this.inFlight = false;
        const items = Array.isArray(response?.items) ? response.items : [];
        if (!sameRows(this._items(), items)) this._items.set(items);
        this._loaded.set(true);
        this._error.set(false);
      },
      error: () => {
        this.inFlight = false;
        this._error.set(true);
      },
    });
  }
}

function sameRows(current: readonly OperatorAttentionItem[], next: readonly OperatorAttentionItem[]): boolean {
  return current.length === next.length
    && current.every((item, index) => item.id === next[index].id && item.reason === next[index].reason);
}
