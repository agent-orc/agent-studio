import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { BackupActivity } from '../models/backup-activity.model';

const BACKUP_AUDIT = '/api/auth/audit/backups';
/** Newest backup records the view reads per request. */
export const BACKUP_ACTIVITY_LIMIT = 100;

type LoadState = 'idle' | 'loading' | 'ready' | 'denied' | 'error';

/**
 * Owner-only backup audit records. The server refuses everyone else, and a
 * server without the route has none; both hide the section instead of
 * showing an error.
 */
@Injectable({ providedIn: 'root' })
export class BackupActivityService {
  private readonly http = inject(HttpClient);
  private request = 0;

  readonly activity = signal<BackupActivity | null>(null);
  readonly state = signal<LoadState>('idle');
  readonly error = signal<string | null>(null);

  async load(): Promise<void> {
    const request = ++this.request;
    this.state.set('loading');
    this.error.set(null);
    try {
      const params = new HttpParams().set('limit', BACKUP_ACTIVITY_LIMIT);
      const activity = await firstValueFrom(this.http.get<BackupActivity>(BACKUP_AUDIT, { params }));
      if (request !== this.request) return;
      this.activity.set(activity);
      this.state.set('ready');
    } catch (failure: unknown) {
      if (request !== this.request) return;
      const status = failure instanceof HttpErrorResponse ? failure.status : 0;
      if (status === 401 || status === 403 || status === 404) {
        this.activity.set(null);
        this.state.set('denied');
        return;
      }
      this.state.set('error');
      this.error.set('Backup activity could not be loaded.');
    }
  }
}
