import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';

import type { OperatorCapacityResponse } from '../models/operator-capacity.model';

/**
 * Reader for `GET /api/operator/capacity`. The usage detail asks it only while
 * the capacity section is on screen; a failed read keeps the last good answer
 * and marks it failed.
 */
@Injectable({ providedIn: 'root' })
export class OperatorCapacityService {
  private readonly http = inject(HttpClient);
  private readonly responseSignal = signal<OperatorCapacityResponse | null>(null);
  readonly response = this.responseSignal.asReadonly();
  private readonly failedSignal = signal(false);
  readonly failed = this.failedSignal.asReadonly();
  private inflight: Subscription | null = null;

  refresh(): void {
    this.inflight?.unsubscribe();
    this.inflight = this.http.get<OperatorCapacityResponse>('/api/operator/capacity').subscribe({
      next: response => {
        this.responseSignal.set(response);
        this.failedSignal.set(false);
      },
      error: () => this.failedSignal.set(true),
    });
  }
}
