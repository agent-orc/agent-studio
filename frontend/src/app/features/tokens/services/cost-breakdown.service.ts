import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

export interface CostBreakdownRequestItem {
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  recordedAt?: string | null;
  label?: string | null;
}

export interface TokenPriceBasis {
  inputPerMillion: number;
  outputPerMillion: number;
  cacheReadPerMillion: number;
  cacheWritePerMillion: number;
  currency: string;
  validFrom: string;
  source: string | null;
  note: string | null;
  unconfirmed: boolean;
}

export interface CostBreakdownResultItem extends CostBreakdownRequestItem {
  calculatedAt: string;
  estimate: {
    inputUsd: number;
    outputUsd: number;
    cacheReadUsd: number;
    cacheWriteUsd: number;
    total: number;
    modelId: string;
    modelKnown: boolean;
    status: string;
    priceBasis: TokenPriceBasis | null;
    pricedInputTokens?: number;
  };
}

interface CostBreakdownResponse {
  provider: string;
  items: CostBreakdownResultItem[];
}

@Injectable({ providedIn: 'root' })
export class CostBreakdownService {
  private readonly http = inject(HttpClient);
  readonly open = signal(false);
  readonly title = signal('Cost calculation');
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly provider = signal('TokenEconomy');
  readonly items = signal<CostBreakdownResultItem[]>([]);

  show(items: CostBreakdownRequestItem[], title = 'Cost calculation'): void {
    if (items.length === 0) return;
    this.title.set(title);
    this.open.set(true);
    this.loading.set(true);
    this.error.set(null);
    this.items.set([]);
    this.http.post<CostBreakdownResponse>('/api/token-pricing/calculate', { items }).subscribe({
      next: response => {
        this.provider.set(response.provider);
        this.items.set(response.items);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('The price calculation could not be loaded.');
        this.loading.set(false);
      },
    });
  }

  close(): void {
    this.open.set(false);
  }
}
