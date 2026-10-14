import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { take } from 'rxjs';
import { NotificationService } from './notification.service';
import { BUILD_SHA } from './build-identity';

export const BUILD_SHA_HEADER = 'X-Agent-Studio-Commit';

@Injectable({ providedIn: 'root' })
export class BuildUpdateNoticeService {
  private readonly http = inject(HttpClient);
  private readonly notifications = inject(NotificationService);
  private readonly frontendSha = inject(BUILD_SHA);
  private noticeId: number | null = null;
  private dismissedForSha: string | null = null;
  private serverSha: string | null = null;

  /** Called by the API response interceptor. Missing identities are dev mode. */
  observe(serverSha: string | null): void {
    if (!this.frontendSha || !serverSha || !/^[0-9a-f]{40}$/.test(serverSha)) return;
    if (serverSha === this.frontendSha) {
      if (this.noticeId !== null) this.notifications.dismiss(this.noticeId);
      this.noticeId = null;
      this.dismissedForSha = null;
      this.serverSha = serverSha;
      return;
    }
    if (serverSha !== this.serverSha) this.dismissedForSha = null;
    this.serverSha = serverSha;
    if (this.noticeId !== null) {
      if (this.notifications.notifications().some(n => n.id === this.noticeId)) return;
      this.noticeId = null;
      this.dismissedForSha = serverSha;
    }
    if (this.dismissedForSha === serverSha) return;
    this.noticeId = this.notifications.notify({
      kind: 'info',
      message: 'Agent Studio was updated – reload to continue',
      durationMs: 0,
      actions: [{ label: 'Reload', testId: 'build-update-reload', primary: true,
        callback: () => window.location.reload() }],
    });
  }

  /** One cheap read after each SignalR connect, including a backend restart. */
  checkAfterReconnect(): void {
    if (!this.frontendSha) return;
    if (this.noticeId !== null
        && !this.notifications.notifications().some(n => n.id === this.noticeId)) {
      this.noticeId = null;
    }
    this.dismissedForSha = null;
    this.http.get('/healthz', { observe: 'response', responseType: 'text' })
      .pipe(take(1))
      .subscribe({
        next: response => this.observe(response.headers.get(BUILD_SHA_HEADER)),
        error: error => console.debug('Build identity check failed after reconnect', error),
      });
  }
}
