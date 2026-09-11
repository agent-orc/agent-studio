import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { tap } from 'rxjs';
import type {
  FullBackupSummary,
  ListFullBackupsResponse,
  RestoreFullBackupResult,
  RetentionApplyResult,
  RetentionArchiveManifest,
  RetentionPlan,
  RetentionPolicy,
  RetentionRunDetail,
  RetentionRunSummary,
  RetentionSchedule,
  UpdateRetentionPolicyRequest,
  VerifyFullBackupResult,
} from '../models/retention.model';

const MANAGEMENT = '/api/v1/management';
const RETENTION = `${MANAGEMENT}/retention`;

@Injectable({ providedIn: 'root' })
export class RetentionService {
  private readonly http = inject(HttpClient);
  readonly restoredTaskId = signal<string | null>(null);

  getWorkspacePolicy() {
    return this.http.get<RetentionPolicy>(`${RETENTION}/policy`);
  }

  updateWorkspacePolicy(request: UpdateRetentionPolicyRequest) {
    return this.http.put<RetentionPolicy>(`${RETENTION}/policy`, request);
  }

  getProjectPolicy(projectId: string) {
    return this.http.get<RetentionPolicy>(
      `${RETENTION}/policy/projects/${encodeURIComponent(projectId)}`,
    );
  }

  updateProjectPolicy(projectId: string, request: UpdateRetentionPolicyRequest) {
    return this.http.put<RetentionPolicy>(
      `${RETENTION}/policy/projects/${encodeURIComponent(projectId)}`,
      request,
    );
  }

  resetProjectPolicy(projectId: string, expectedVersion: number) {
    const params = new HttpParams().set('expectedVersion', expectedVersion);
    return this.http.delete<{ deleted: boolean }>(
      `${RETENTION}/policy/projects/${encodeURIComponent(projectId)}`,
      { params },
    );
  }

  plan(project?: string) {
    return this.http.post<RetentionPlan>(`${RETENTION}/plan`, { project: project ?? null });
  }

  apply(project?: string) {
    return this.http.post<RetentionApplyResult>(`${RETENTION}/apply`, { project: project ?? null });
  }

  listRuns() {
    return this.http.get<RetentionRunSummary[]>(`${RETENTION}/runs`);
  }

  getSchedule() {
    return this.http.get<RetentionSchedule>(`${RETENTION}/schedule`);
  }

  getRun(runId: string) {
    return this.http.get<RetentionRunDetail>(`${RETENTION}/runs/${encodeURIComponent(runId)}`);
  }

  getManifest(taskId: string) {
    return this.http.get<RetentionArchiveManifest>(
      `${RETENTION}/archive/${encodeURIComponent(taskId)}`,
    );
  }

  restoreTask(taskId: string) {
    return this.http.post<{ restored: boolean; taskId: string }>(
      `${RETENTION}/archive/${encodeURIComponent(taskId)}/restore`,
      {},
    ).pipe(tap(result => {
      if (result.restored) this.restoredTaskId.set(taskId);
    }));
  }

  listFullBackups() {
    return this.http.get<ListFullBackupsResponse>(`${MANAGEMENT}/backups/full`);
  }

  createFullBackup() {
    return this.http.post<FullBackupSummary>(`${MANAGEMENT}/backups/full`, {});
  }

  verifyFullBackup(backupId: string) {
    return this.http.post<VerifyFullBackupResult>(
      `${MANAGEMENT}/backups/full/${encodeURIComponent(backupId)}/verify`,
      {},
    );
  }

  restoreFullBackup(backupId: string) {
    return this.http.post<RestoreFullBackupResult>(
      `${MANAGEMENT}/backups/full/${encodeURIComponent(backupId)}/restore`,
      { backupId },
    );
  }
}
