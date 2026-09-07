import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { WatcherContingentSnapshot, WatcherStatus } from '../models/watcher.model';

/** Read and decision client for `/api/watcher` (AGT-2721). */
@Injectable({ providedIn: 'root' })
export class WatcherApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/watcher';

  getStatus() {
    return this.http.get<WatcherStatus>(`${this.baseUrl}/status`);
  }

  /**
   * Budget, usage per window, and the count of cases that were found but not
   * drafted because the contingent ran out. Read from the store rather than
   * from the last sweep snapshot, so it stays truthful before the first sweep.
   */
  getContingent() {
    return this.http.get<WatcherContingentSnapshot>(`${this.baseUrl}/contingent`);
  }
}
