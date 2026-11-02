import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { AuthService } from '../../../../services/auth.service';

export interface QualityStudioStatus {
  connected: boolean;
  repositoryId: string | null;
  state: string;
  reason: string | null;
}

@Injectable()
export class QualityStudioStatusService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private request?: Subscription;
  private projectName = '';

  readonly status = signal<QualityStudioStatus | null>(null);
  readonly busy = signal(false);
  readonly canRegister = computed(() =>
    this.auth.status()?.profile === 'local' || this.auth.status()?.user?.role === 'owner');
  readonly visible = computed(() => this.status()?.connected === true || this.canRegister());

  load(projectName: string): void {
    this.request?.unsubscribe();
    this.projectName = projectName;
    this.status.set(null);
    this.request = this.http.get<QualityStudioStatus>(this.url()).subscribe({
      next: status => this.status.set(status),
      error: () => this.status.set({ connected: false, repositoryId: null,
        state: 'not-registered', reason: 'Quality Studio status is unavailable.' }),
    });
  }

  reconcile(): void {
    if (!this.canRegister() || this.busy()) return;
    this.busy.set(true);
    this.http.post<QualityStudioStatus>(`${this.url()}/reconcile`, {}).subscribe({
      next: status => {
        this.status.set(status);
        this.busy.set(false);
      },
      error: () => {
        this.status.update(status => ({
          connected: status?.connected ?? true, repositoryId: status?.repositoryId ?? null,
          state: 'not-registered', reason: 'Registration request failed.',
        }));
        this.busy.set(false);
      },
    });
  }

  private url(): string {
    return `/api/projects/${encodeURIComponent(this.projectName)}/quality-studio`;
  }
}
