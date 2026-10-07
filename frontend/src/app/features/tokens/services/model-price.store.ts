import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { TokenPriceBasis } from './cost-breakdown.service';

interface PriceResponse {
  items: { model: string; estimate: { modelKnown: boolean; priceBasis: TokenPriceBasis | null } }[];
}

/**
 * Current per-model list prices for the model picker (AGT-2903). Studio owns
 * no rates: each price is the `priceBasis` the TokenEconomy cost API returns
 * for a zero-token calculation at the current time. Unknown models stay null
 * and render no price.
 */
@Injectable({ providedIn: 'root' })
export class ModelPriceStore {
  private readonly http = inject(HttpClient);
  private readonly requested = new Set<string>();
  private readonly queued = new Set<string>();
  readonly prices = signal<ReadonlyMap<string, TokenPriceBasis | null>>(new Map());

  /** Queues unknown ids; every id queued in the same task is priced in one request. */
  ensure(modelIds: readonly string[]): void {
    const wasEmpty = this.queued.size === 0;
    for (const id of modelIds.map((value) => value.trim())) {
      if (id && !this.requested.has(id)) this.queued.add(id);
    }
    if (wasEmpty && this.queued.size > 0) queueMicrotask(() => this.flush());
  }

  private flush(): void {
    const missing = [...this.queued].slice(0, 100);
    this.queued.clear();
    if (missing.length === 0) return;
    missing.forEach((id) => this.requested.add(id));
    const items = missing.map((model) => ({
      model, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0,
    }));
    this.http.post<PriceResponse>('/api/token-pricing/calculate', { items }).subscribe({
      next: (response) => this.prices.update((current) => {
        const next = new Map(current);
        for (const item of response.items) {
          next.set(item.model, item.estimate.modelKnown ? item.estimate.priceBasis : null);
        }
        return next;
      }),
      // A failed lookup leaves prices unknown; allow a later picker open to retry.
      error: () => missing.forEach((id) => this.requested.delete(id)),
    });
  }
}

/** Compact "$in / $out per MTok" label, or null when the price is unknown. */
export function modelPriceLabel(price: TokenPriceBasis | null | undefined): string | null {
  if (!price) return null;
  return `$${formatRate(price.inputPerMillion)} / $${formatRate(price.outputPerMillion)}`;
}

function formatRate(value: number): string {
  return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(2)));
}
