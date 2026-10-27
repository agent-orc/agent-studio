import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import type { StallInterventionRow, StallInterventionsResponse } from '../models/stall-intervention.model';

/**
 * AGT-3207: the AI Operator's stall interventions with their receipts. The
 * host view shows the latest receipt next to each open stall and lets a
 * person undo a reversible intervention.
 */
@Injectable({ providedIn: 'root' })
export class StallInterventionsService {
  private readonly http = inject(HttpClient);
  private readonly _rows = signal<StallInterventionRow[]>([]);
  private readonly _error = signal<string | null>(null);

  readonly rows = this._rows.asReadonly();
  readonly error = this._error.asReadonly();
  readonly latestBySubject = computed(() => {
    const latest = new Map<string, StallInterventionRow>();
    for (const row of this._rows()) {
      if (!latest.has(row.receipt.subject)) latest.set(row.receipt.subject, row);
    }
    return latest;
  });

  refresh(): void {
    this.http.get<StallInterventionsResponse>('/api/watcher/interventions?limit=50').subscribe({
      next: response => this._rows.set(Array.isArray(response?.interventions) ? response.interventions : []),
      error: () => this._rows.set([]),
    });
  }

  undo(id: string): void {
    this._error.set(null);
    this.http.post(`/api/watcher/interventions/${encodeURIComponent(id)}/undo`, {}).subscribe({
      next: () => this.refresh(),
      error: error => this._error.set(error?.error?.error ?? 'Undo failed.'),
    });
  }
}
