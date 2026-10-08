import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { CredentialView } from '../models/credential-view.model';

@Injectable({ providedIn: 'root' })
export class CredentialViewsService {
  private readonly http = inject(HttpClient, { optional: true });
  readonly views = signal<readonly CredentialView[]>([]);
  readonly error = signal(false);

  refresh(): void {
    this.http?.get<CredentialView[]>('/api/v1/management/credential-views').subscribe({
      next: views => { this.views.set(views ?? []); this.error.set(false); },
      error: () => { this.views.set([]); this.error.set(true); },
    });
  }
}
