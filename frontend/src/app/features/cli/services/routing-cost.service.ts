import { shareReplay } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

export interface PricedRouteModel {
  cli: string; model: string; label: string; thinkingLevels: string[];
  price: { inputPerMillion: number; outputPerMillion: number } | null;
}
export interface RoutingCostEstimate {
  receipts: number; actualUsd: number | null; alternativeUsd: number | null;
  savingUsd: number | null; savingPercent: number | null; projectedWeeklySavingUsd: number | null;
  unpricedActual: number; unpricedAlternative: number; coverage: string;
  qualityCaveat: string; correctnessFloor: string;
}
@Injectable({ providedIn: 'root' })
export class RoutingCostService {
  private readonly http = inject(HttpClient);
  private readonly modelOptions = this.http.get<{ models: PricedRouteModel[] }>('/api/token-pricing/route-models').pipe(shareReplay({ bufferSize: 1, refCount: false }));
  models() { return this.modelOptions; }
  estimate(from: string, to: string, consumer: string, alternativeModel: string, economyMode?: boolean) {
    return this.http.post<RoutingCostEstimate>('/api/token-pricing/what-if',
      { from, to, consumer, alternativeModel, economyMode });
  }
}
