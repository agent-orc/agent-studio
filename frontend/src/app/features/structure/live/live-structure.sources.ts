import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { catchError, forkJoin, of } from 'rxjs';

/**
 * Optional reads next to the structure itself (AGT-4024). Each one is an API the
 * Operations area already uses; the shapes below name only the fields this view
 * shows. A read the caller may not make, or that fails, leaves `null`, and the
 * view then says "not observed" for what it would have supplied.
 */

/** `GET /api/operator/backups`: the read-only backup status. */
export interface LiveBackupOverview {
  state: 'ok' | 'warning' | 'failing' | 'not-configured' | 'disabled';
  configured: boolean;
  running: boolean;
  intervalHours: number;
  retentionCount: number;
  nextBackupAt: string | null;
  lastGood: { id: string; createdAt: string; sizeBytes: number; fileCount: number; verification: string; offHost: string } | null;
  lastGoodAgeHours: number | null;
  stale: boolean;
  lastRun: { at: string; outcome: string; error: string | null } | null;
  lastVerification: { at: string; outcome: string } | null;
  offHost: { kind: string | null; state: 'not-configured' | 'invalid' | 'idle' | 'current' | 'pending' | 'failed';
    lastUploadedAt: string | null; lastError: string | null };
  warnings: readonly { code: string; severity: 'warning' | 'danger'; message: string }[];
  generatedAt: string;
}

/** `GET /api/v1/management/remote-hosts`: one entry per runner process. */
export interface LiveRunnerSnapshot {
  runnerId: string;
  name: string;
  hostId: string;
  runnerVersion: string;
  status: string;
  registeredAt: string;
  lastSeenAt: string;
  restartedAt?: string | null;
  reviewRestarts?: readonly { restartedAt: string; reviewsLost: number }[] | null;
  release?: { version?: string | null; commit?: string | null; builtAt?: string | null; imageRef?: string | null } | null;
  installedClis?: readonly { name: string; version: string; targetVersion?: string | null; isBelowTarget: boolean }[] | null;
}

/** Gate workers of `GET /api/v1/management/environment`. */
export interface LiveGateWorker { id: string; name: string; slots: number; busy: number; lastHeartbeat: string; currentRun: string | null; }
export interface LiveEnvironmentSnapshot {
  gateWorkers?: readonly LiveGateWorker[] | null;
  taskServer?: { version: string; commit: string | null } | null;
  engine?: { state: string; lastContact: string | null } | null;
}

export interface LiveSources {
  backup: LiveBackupOverview | null;
  runners: readonly LiveRunnerSnapshot[] | null;
  environment: LiveEnvironmentSnapshot | null;
}
export const NO_SOURCES: LiveSources = { backup: null, runners: null, environment: null };

const isBackup = (value: unknown): value is LiveBackupOverview =>
  !!value && typeof value === 'object' && typeof (value as LiveBackupOverview).state === 'string' && !!(value as LiveBackupOverview).offHost;

@Injectable({ providedIn: 'root' })
export class LiveStructureSourcesService {
  private readonly http = inject(HttpClient);
  readonly sources = signal<LiveSources>(NO_SOURCES);
  /** True once the first read has answered, so "not observed" is not shown while it is still loading. */
  readonly settled = signal(false);
  private reading = false;

  refresh(): void {
    if (this.reading) return;
    this.reading = true;
    forkJoin({
      backup: this.http.get<unknown>('/api/operator/backups').pipe(catchError(() => of(null))),
      runners: this.http.get<LiveRunnerSnapshot[]>('/api/v1/management/remote-hosts').pipe(catchError(() => of(null))),
      environment: this.http.get<LiveEnvironmentSnapshot>('/api/v1/management/environment').pipe(catchError(() => of(null))),
    }).subscribe(result => {
      this.reading = false;
      this.sources.set({
        backup: isBackup(result.backup) ? result.backup : null,
        runners: Array.isArray(result.runners) ? result.runners : null,
        environment: result.environment && typeof result.environment === 'object' ? result.environment : null,
      });
      this.settled.set(true);
    });
  }
}
