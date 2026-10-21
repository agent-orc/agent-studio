import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { ReviewEffectivenessSummary } from '../models/review-effectiveness.model';

/** Client for the review-effectiveness ledger (AGT-3269). */
@Injectable({ providedIn: 'root' })
export class ReviewEffectivenessApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/review-effectiveness';

  getSummary(days: number) {
    return this.http.get<ReviewEffectivenessSummary>(this.baseUrl, {
      params: new HttpParams().set('days', days),
    });
  }

  /** Token Economy evidence export for the same window; opened as a download link. */
  exportUrl(days: number): string {
    return `${this.baseUrl}/export?days=${encodeURIComponent(days)}`;
  }
}
