import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  ManagementActionKind,
  ManagementActionResult,
  ArchiveTargetStatus,
  RetentionPlan,
  TaskServerStatus,
} from '../models/task-server.model';
import { isLocalUrl } from '../models/task-server.model';
import { AuthSessionState } from '../../../services/auth.service';

export interface TaskServerUnavailable {
  reason: string;
  signInRequired: boolean;
}

interface ApiStatus {
  server: { id: string; url: string; version: string; protocolMinimum: string; protocolMaximum: string; uptimeSeconds: number };
  health: { state: 'healthy' | 'degraded' | 'maintenance'; ready: boolean };
  store: { sizeBytes: number; projectCount: number; taskCount: number; archivedTaskCount: number; eventCount: number; artifactCount: number; identityCount: number };
  evidence: { state: string; eventFiles: number; artifactFiles: number; lastWriteAt: string | null };
  maintenance: TaskServerStatus['maintenance'];
  migrations: TaskServerStatus['migrations'];
  runners: readonly {
    id: string; displayName: string; state: string; lastUsedAt: string | null;
    activeSlots: number; drainRequested: boolean; retireRequested: boolean;
  }[];
  backups: TaskServerStatus['backups'];
  security: TaskServerStatus['security'];
}

interface ApiCommandResult {
  commandId: string; kind: ManagementActionKind; dryRun: boolean; state: string;
  matched: number; affected: number; summary: string; completedAt: string;
  detail?: {
    runnerId?: string;
    credentialId?: string;
    secret?: string;
    enrollmentCode?: string;
  } | null;
}

@Injectable({ providedIn: 'root' })
export class TaskServerService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  readonly status = signal<TaskServerStatus | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly unavailable = signal<TaskServerUnavailable | null>(null);
  readonly busyAction = signal<ManagementActionKind | null>(null);
  readonly archiveTarget = signal<ArchiveTargetStatus | null>(null);
  readonly archiveDeletionPlan = signal<RetentionPlan | null>(null);
  readonly archiveBusy = signal(false);
  readonly recentResults = computed<readonly ManagementActionResult[]>(() => this.status()?.recentResults ?? []);
  private loaded = false;

  ensureLoaded(): void { if (!this.loaded) void this.reload(); }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    this.unavailable.set(null);
    try {
      const api = await firstValueFrom(this.http.get<ApiStatus>('/api/v1/management/status'));
      const prior = this.status()?.recentResults ?? [];
      this.status.set(this.mapStatus(api, prior));
      this.loaded = true;
    } catch (error: unknown) {
      const unavailable = this.describeUnavailable(error);
      this.status.set(null);
      this.unavailable.set(unavailable);
      this.error.set(unavailable.reason);
    } finally {
      this.loading.set(false);
    }
  }

  async previewArchiveDeletion(): Promise<void> {
    if (this.archiveBusy()) return;
    this.archiveBusy.set(true);
    this.error.set(null);
    try {
      const plan = await firstValueFrom(this.http.post<RetentionPlan>(
        '/api/v1/management/retention/plan', { confirmColdDelete: false }));
      this.archiveDeletionPlan.set({ ...plan, actions: plan.actions.filter(action => action.kind === 'DeleteCold') });
    } catch (error: unknown) {
      this.error.set(this.message(error, 'The archive deletion preview failed.'));
    } finally {
      this.archiveBusy.set(false);
    }
  }

  async confirmArchiveDeletion(): Promise<void> {
    if (this.archiveBusy()) return;
    this.archiveBusy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.http.post('/api/v1/management/retention/apply', { confirmColdDelete: true }));
      this.archiveDeletionPlan.set(null);
      await this.reload();
    } catch (error: unknown) {
      this.error.set(this.message(error, 'The confirmed archive deletion failed.'));
    } finally {
      this.archiveBusy.set(false);
    }
  }

  cancelArchiveDeletion(): void { this.archiveDeletionPlan.set(null); }

  async loadArchiveTarget(): Promise<void> {
    try {
      this.archiveTarget.set(await firstValueFrom(
        this.http.get<ArchiveTargetStatus>('/api/v1/management/retention/target')));
    } catch {
      // Compatibility while the local file-tree control plane is still active.
      this.archiveTarget.set(null);
    }
  }

  private message(error: unknown, fallback: string): string {
    return error instanceof HttpErrorResponse
      ? error.error?.message ?? error.error?.error ?? fallback
      : fallback;
  }

  requestSignIn(): void {
    this.auth.expireNetworkedSession();
  }

  async runAction(kind: ManagementActionKind, confirmed = false, runnerId?: string, runnerName?: string): Promise<void> {
    if (this.busyAction()) return;
    this.busyAction.set(kind);
    this.error.set(null);
    try {
      const idempotencyKey = crypto.randomUUID();
      const body = {
        kind,
        dryRun: !confirmed,
        confirmation: confirmed ? kind : null,
        idempotencyKey,
        runnerId: runnerId ?? null,
        runnerName: runnerName ?? null,
      };
      const result = await firstValueFrom(this.http.post<ApiCommandResult>(
        '/api/v1/management/commands', body,
        { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) },
      ));
      const snapshot = this.status();
      if (snapshot) {
        const mapped: ManagementActionResult = {
          kind: result.kind,
          ranAt: result.completedAt,
          summary: result.summary,
          affected: result.affected,
          matched: result.matched,
          dryRun: result.dryRun,
          commandId: result.commandId,
          state: result.state,
          targetId: result.detail?.runnerId ?? runnerId ?? runnerName ?? null,
          credentialId: result.detail?.credentialId ?? null,
          secret: result.detail?.secret ?? null,
          enrollmentCode: result.detail?.enrollmentCode ?? null,
        };
        this.status.set({ ...snapshot, recentResults: [mapped, ...snapshot.recentResults].slice(0, 12) });
      }
      if (confirmed) await this.reload();
    } catch (error: unknown) {
      const code = error instanceof HttpErrorResponse
        ? error.error?.message ?? error.error?.error
        : null;
      this.error.set(code || `The ${kind} command failed. Inspect the durable management audit before retrying.`);
    } finally {
      this.busyAction.set(null);
    }
  }

  private mapStatus(api: ApiStatus, recentResults: readonly ManagementActionResult[]): TaskServerStatus {
    return {
      connection: {
        id: api.server.id,
        url: api.server.url,
        phase: isLocalUrl(api.server.url) ? 'local' : 'central',
        health: api.health.state,
        version: api.server.version,
        uptimeLabel: formatUptime(api.server.uptimeSeconds),
        protocolMinimum: api.server.protocolMinimum,
        protocolMaximum: api.server.protocolMaximum,
        ready: api.health.ready,
        authMode: api.security.available
          ? 'Authenticated session / scoped Runner credential'
          : 'Local loopback profile; X-Client-Id is attribution only',
      },
      store: { ...api.store, root: 'Server-owned data directory (path is not exposed)' },
      evidence: { ...api.evidence },
      clients: api.runners.map(runner => ({
        id: runner.id,
        displayName: runner.displayName,
        emoji: null,
        kind: runner.state === 'retired' || runner.state === 'revoked' ? 'retired' : 'agent-instance',
        lastSeenAt: runner.lastUsedAt,
        ownedTaskCount: runner.activeSlots,
        managementState: runner.state,
      })),
      maintenance: api.maintenance,
      migrations: api.migrations,
      backups: api.backups,
      security: api.security,
      recentResults,
    };
  }

  private describeUnavailable(error: unknown): TaskServerUnavailable {
    if (error instanceof HttpErrorResponse) {
      const apiMessage = typeof error.error?.message === 'string' ? error.error.message : null;
      const loginUrl = typeof error.error?.loginUrl === 'string' ? error.error.loginUrl : null;
      const networked = this.auth.status()?.profile === 'networked' || loginUrl === '/api/auth/login';
      if (error.status === 401 && networked) {
        return {
          reason: apiMessage ?? 'Sign in with an owner or operator account to manage the Task Server.',
          signInRequired: true,
        };
      }
      if (error.status === 403) {
        return {
          reason: apiMessage ?? 'Your signed-in account does not have the owner or operator role required for Task Server management.',
          signInRequired: false,
        };
      }
      if (error.status === 401) {
        return {
          reason: apiMessage ?? 'The local Task Server rejected the loopback local-default operator identity.',
          signInRequired: false,
        };
      }
      if (error.status === 0) {
        return {
          reason: 'The Task Server could not be reached. Check that the local service is running and reload this panel.',
          signInRequired: false,
        };
      }
      if (apiMessage) return { reason: apiMessage, signInRequired: false };
    }
    return {
      reason: 'The Task Server management API did not return a usable status. Reload the panel or inspect the server logs.',
      signInRequired: false,
    };
  }
}

function formatUptime(totalSeconds: number): string {
  const days = Math.floor(totalSeconds / 86_400);
  const hours = Math.floor((totalSeconds % 86_400) / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  return days ? `${days}d ${hours}h` : hours ? `${hours}h ${minutes}m` : `${minutes}m`;
}
