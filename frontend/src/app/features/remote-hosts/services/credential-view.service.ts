import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { CredentialView } from '../models/credential-view.model';

/**
 * Loads the role-scoped credential view (I8). The Task Server resolves the role
 * and authorizes every action; this service never infers one locally.
 */
@Injectable({ providedIn: 'root' })
export class CredentialViewService {
  private readonly http = inject(HttpClient, { optional: true });
  readonly view = signal<CredentialView | null>(null);
  readonly state = signal<'idle' | 'loading' | 'ready' | 'unavailable'>('idle');

  ensureLoaded(): void {
    if (this.state() === 'idle') this.load();
  }

  load(): void {
    if (this.state() === 'loading') return;
    if (!this.http) {
      this.state.set('unavailable');
      return;
    }
    this.state.set('loading');
    this.http.get<CredentialView>('/api/v1/management/credential-view').subscribe({
      next: view => {
        this.view.set(view);
        this.state.set('ready');
      },
      error: () => {
        this.view.set(null);
        this.state.set('unavailable');
      },
    });
  }
}
