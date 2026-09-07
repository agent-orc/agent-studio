import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { ClientSummary } from '../../../models/task.model';
import type {
  HostActionKind,
  HostRampStrategy,
  HostTelemetrySeries,
  RemoteRunnerLinkHealth,
  RemoteRunnerReconnectResponse,
  RemoteHost,
  TaskServerTelemetrySnapshot,
  TaskServerRunnerCapabilitySnapshot,
} from '../models/remote-host.model';
import { seedRemoteHosts } from './remote-hosts.seed';
import { ProviderAuthStatusService } from './provider-auth-status.service';
import { NotificationService } from '../../../services/notification.service';

/**
 * Registry + action service for the Remote-Hosts page (AGT-1921).
 *
 * Host topology comes from the static configuration seed while liveness comes
 * from the real client registry. Reload hydrates each configured client id from
 * `GET /api/clients`, so runner polling updates LastSeen and status without a
 * second host-registry backend contract.
 *
 * Lifecycle actions call the persisted client API. No seeded value is treated
 * as live telemetry.
 */
@Injectable({ providedIn: 'root' })
export class RemoteHostsService {
  /** The registry, newest snapshot wins. */
  readonly hosts = signal<RemoteHost[]>([]);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly identityDiagnostics = signal<readonly ClientSummary[]>([]);

  private static readonly FRESH_CLIENT_MS = 90_000;
  private static readonly DEGRADED_CLIENT_MS = 5 * 60_000;
  /** Optional keeps direct-constructor pure tests and non-HTTP previews viable. */
  private readonly http = tryInjectHttpClient();
  private readonly providerAuth = tryInjectProviderAuthStatus();
  private readonly notifications = tryInjectNotificationService();
  private readonly notifiedLinkFailures = new Set<string>();

  /** Every mount revalidates live state; cached cards are never authoritative. */
  ensureLoaded(): void {
    this.reload();
  }

  /**
   * Refresh the compact live view without replacing a longer telemetry series
   * already loaded by the Execution Hosts page.
   */
  refresh(): void {
    if (this.hosts().length === 0) {
      this.hosts.set(seedRemoteHosts(Date.now()).map(host => ({
        ...host,
        liveDataState: 'loading',
        telemetryLoading: false,
      })));
    }
    this.hydrateClientRegistry('1h', true);
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    try {
      const current = this.hosts();
      this.hosts.set((current.length ? current : seedRemoteHosts(Date.now())).map(host => ({
        ...host,
        liveDataState: 'loading',
        telemetryLoading: false,
      })));
      this.log('loaded', { count: this.hosts().length });
      this.hydrateClientRegistry();
    } catch (e) {
      this.error.set('Failed to load the host registry.');
      this.log('load-failed', { message: (e as Error)?.message ?? 'unknown' });
      this.loading.set(false);
    }
  }

  private hydrateClientRegistry(
    telemetryWindow: '1h' | '14d' = '14d',
    preserveLongTelemetry = false,
  ): void {
    if (!this.http) {
      this.hosts.update(hosts => hosts.map(host => ({ ...host, liveDataState: 'error' })));
      this.loading.set(false);
      return;
    }
    const startedAt = performance.now();
    this.http.get<ClientSummary[]>('/api/clients').subscribe({
      next: clients => {
        this.identityDiagnostics.set((clients ?? []).filter(client => !!client.identityFileError));
        const byId = new Map((clients ?? []).map(client => [client.id, client]));
        const now = Date.now();
        this.hosts.update(hosts => {
          const projected = hosts.map(host => {
          const client = byId.get(host.clientId);
          if (!client) return {
            ...host,
            status: 'offline' as const,
            stats: null,
            liveDataState: 'ready' as const,
            telemetryLoading: false,
          };
          if (client.kind === 'retired') {
            return projectClient(host, client, 'retired');
          }
          const seenAt = client.lastSeenAt;
          const seenMs = seenAt ? Date.parse(seenAt) : Number.NaN;
          const status = !seenAt || Number.isNaN(seenMs)
            ? 'offline'
            : now - seenMs <= RemoteHostsService.FRESH_CLIENT_MS
              ? 'online'
              : now - seenMs <= RemoteHostsService.DEGRADED_CLIENT_MS
                ? 'degraded'
                : 'offline';
          const projectedHost = projectClient(host, client, client.drainRequestedAt ? 'draining' : status);
          return preserveLongTelemetry && host.telemetry?.window === '14d'
            ? { ...projectedHost, stats: host.stats, telemetry: host.telemetry }
            : projectedHost;
          });
          const known = new Set(projected.map(host => host.clientId));
          const discovered = (clients ?? [])
            .filter(client => !known.has(client.id) && isRunnerIdentity(client))
            .map(client => projectClient({
              id: client.id, name: client.displayName, role: 'remote', address: null, clientId: client.id,
              status: 'offline', os: 'Remote runner', lastHeartbeatAt: null, uptimeLabel: null,
              capabilities: [], cliQuotas: [], stats: null, liveDataState: 'ready',
            }, client, client.kind === 'retired' ? 'retired' : client.drainRequestedAt ? 'draining' : statusFor(client.lastSeenAt, now)));
          return [...projected, ...discovered];
        });
        this.loading.set(false);
        this.log('clients-hydrated', {
          clients: clients?.length ?? 0,
          durationMs: Math.round(performance.now() - startedAt),
        });
        for (const host of this.hosts().filter(host =>
          byId.has(host.clientId) && host.status !== 'retired' && !host.identityFileError)) {
          this.patch(host.id, current => preserveLongTelemetry && current.telemetry?.window === '14d'
            ? { ...current, telemetryLoading: true }
            : { ...current, stats: null, telemetry: null, telemetryLoading: true });
          this.hydrateTelemetry(host.id, host.clientId, telemetryWindow, preserveLongTelemetry);
        }
        this.hydrateCapabilityRegistry();
        this.hydrateLinkHealth();
      },
      error: error => {
        this.identityDiagnostics.set([]);
        this.hosts.update(hosts => hosts.map(host => ({ ...host, liveDataState: 'error' })));
        this.loading.set(false);
        this.error.set('Live host status is temporarily unavailable.');
        this.log('clients-hydrate-failed', {
          message: error?.message ?? 'unknown',
          durationMs: Math.round(performance.now() - startedAt),
        });
      },
    });
  }

  private hydrateCapabilityRegistry(): void {
    if (!this.http) return;
    this.http.get<TaskServerRunnerCapabilitySnapshot[]>('/api/v1/management/remote-hosts').subscribe({
      next: snapshots => {
        this.providerAuth?.ingest(snapshots ?? []);
        const now = Date.now();
        this.hosts.update(hosts => {
          const projected = [...hosts];
          for (const snapshot of snapshots ?? []) {
            const index = projected.findIndex(host =>
              host.clientId === snapshot.runnerId || host.id === snapshot.runnerId);
            const current = index >= 0 ? projected[index] : {
              id: snapshot.runnerId,
              name: snapshot.name,
              role: 'remote' as const,
              serviceRole: serviceRole(snapshot),
              address: null,
              clientId: snapshot.runnerId,
              status: 'offline' as const,
              os: 'Remote runner',
              lastHeartbeatAt: null,
              uptimeLabel: null,
              capabilities: [],
              cliQuotas: [],
              stats: null,
            };
            const lastSeenMs = Date.parse(snapshot.lastSeenAt);
            const heartbeatFresh = Number.isFinite(lastSeenMs)
              && now - lastSeenMs <= RemoteHostsService.DEGRADED_CLIENT_MS;
            const capabilityDegraded = snapshot.capabilities.some(capability =>
              !capability.isFresh || capability.healthState !== 'healthy' || capability.advertisedStatus !== 'ready');
            const hostDraining = snapshot.hostAdmission.admissionState !== 'open';
            const telemetryAt = snapshot.telemetry ? Date.parse(snapshot.telemetry.observedAt) : Number.NaN;
            const telemetryFresh = snapshot.telemetry && Number.isFinite(telemetryAt)
              && now - telemetryAt <= RemoteHostsService.DEGRADED_CLIENT_MS
              && heartbeatFresh;
            const status = current.identityFileError
              ? 'offline'
              : hostDraining
              ? 'draining'
              : !heartbeatFresh
                ? current.status
                : capabilityDegraded ? 'degraded' : current.status === 'offline' ? 'online' : current.status;
            const stats = telemetryFresh && snapshot.telemetry
              ? telemetryStats(snapshot.telemetry)
              : status === 'offline' ? null : current.stats;
            const gitPush = snapshot.capabilities.find(capability => capability.key === 'git:push');
            const gitWorkflowPush = snapshot.capabilities.find(
              capability => capability.key === 'git:workflow-push',
            );
            const gitPushStatus = gitPush && gitPush.advertisedStatus !== 'ready'
              ? 'read-only' as const
              : gitWorkflowPush?.advertisedStatus === 'ready-no-workflow-scope'
                ? 'ready-no-workflow-scope' as const
                : gitWorkflowPush?.advertisedStatus === 'ready' || gitPush?.advertisedStatus === 'ready'
                  ? 'ready' as const
                  : current.gitPushStatus;
            const next: RemoteHost = {
              ...current,
              name: snapshot.name,
              serviceRole: serviceRole(snapshot),
              status,
              lastHeartbeatAt: snapshot.lastSeenAt,
              releaseId: snapshot.runnerVersion,
              runnerInstanceId: snapshot.instanceId,
              runnerProtocolVersion: snapshot.protocolVersion,
              capabilityHealth: snapshot.capabilities,
              capabilities: snapshot.capabilities.map(capability =>
                capability.version ? `${capability.key} ${capability.version}` : capability.key),
              gitPushStatus,
              gitPushDetail: gitWorkflowPush?.detail ?? gitPush?.detail ?? current.gitPushDetail,
              gitPushCheckedAt:
                gitWorkflowPush?.advertisedAt ?? gitPush?.advertisedAt ?? current.gitPushCheckedAt,
              hostAdmission: snapshot.hostAdmission,
              capacityHostId: snapshot.hostId,
              runtimeCapacity: snapshot.runtimeCapacity ?? current.runtimeCapacity ?? null,
              effectiveMaxParallelism:
                snapshot.effectiveMaxParallelism !== undefined
                  ? snapshot.effectiveMaxParallelism
                  : current.effectiveMaxParallelism ?? null,
              roleMaxParallelism:
                snapshot.roleMaxParallelism !== undefined
                  ? snapshot.roleMaxParallelism
                  : current.roleMaxParallelism ?? null,
              runtimeCapacityAppliedAt:
                snapshot.runtimeCapacityAppliedAt !== undefined
                  ? snapshot.runtimeCapacityAppliedAt
                  : current.runtimeCapacityAppliedAt ?? null,
              runtimeCapacityAppliedVersion:
                snapshot.runtimeCapacityAppliedVersion !== undefined
                  ? snapshot.runtimeCapacityAppliedVersion
                  : current.runtimeCapacityAppliedVersion ?? null,
              projectPolicy: snapshot.projectPolicy !== undefined
                ? snapshot.projectPolicy
                : current.projectPolicy ?? null,
              taskServerConnection: snapshot.telemetry
                ? taskServerConnection(snapshot.telemetry)
                : current.taskServerConnection ?? null,
              activeTaskCount: snapshot.telemetry?.activeSlots ?? current.activeTaskCount,
              stats,
            };
            if (index >= 0) projected[index] = next;
            else projected.push(next);
          }
          return projected;
        });
        this.log('capabilities-hydrated', { runners: snapshots?.length ?? 0 });
      },
      error: error => this.log('capabilities-hydrate-failed', { message: error?.message ?? 'unknown' }),
    });
  }

  private hydrateLinkHealth(): void {
    if (!this.http) return;
    this.http.get<RemoteRunnerLinkHealth[]>('/api/v1/management/remote-hosts/link-health').subscribe({
      next: links => {
        this.providerAuth?.links.set(links ?? []);
        this.hosts.update(hosts => hosts.map(host => {
          const link = (links ?? []).find(item => item.runnerId.toLowerCase() === host.clientId.toLowerCase());
          if (!link) return host;
          const status = host.status === 'retired' || host.status === 'draining'
            ? host.status
            : link.linkState === 'down' ? 'offline'
              : link.linkState === 'stale' ? 'degraded'
                : host.status === 'offline' ? 'online' : host.status;
          return { ...host, status, runnerLink: link, lastHeartbeatAt: link.lastSnapshotAt ?? host.lastHeartbeatAt };
        }));
        for (const link of links ?? []) this.notifyLinkFailure(link);
      },
      error: error => this.log('link-health-hydrate-failed', { message: error?.message ?? 'unknown' }),
    });
  }

  private notifyLinkFailure(link: RemoteRunnerLinkHealth): void {
    const keeper = link.keeper;
    if (link.linkState !== 'down' || !link.readyCardsTargetHost || !keeper?.supported || !keeper.cause) {
      if (link.linkState === 'connected') this.notifiedLinkFailures.delete(link.runnerId);
      return;
    }
    if (this.notifiedLinkFailures.has(link.runnerId) || !this.notifications) return;
    this.notifiedLinkFailures.add(link.runnerId);
    const cause = keeperCauseLabel(keeper.cause);
    this.notifications.notify({
      kind: 'warning',
      title: `${link.name} link is down`,
      message: `${cause}. Ready cards targeting this host cannot be claimed.`,
      details: [`Last runner snapshot: ${link.lastSnapshotAt ?? 'never'}`, keeper.detail ?? 'No keeper detail reported.'],
      actions: [{
        label: 'Reconnect',
        testId: 'remote-host-reconnect-notification',
        primary: true,
        callback: () => this.reconnect(link.runnerId),
      }],
    });
  }

  private hydrateTelemetry(
    hostId: string,
    clientId: string,
    telemetryWindow: '1h' | '14d' = '14d',
    preserveLongTelemetry = false,
  ): void {
    if (!this.http) return;
    const startedAt = performance.now();
    this.http.get<HostTelemetrySeries>(
      `/api/clients/${encodeURIComponent(clientId)}/telemetry?window=${telemetryWindow}`,
    ).subscribe({
      next: telemetry => {
        this.patch(hostId, host => {
          const latest = telemetry.points.at(-1);
          const fresh = latest && Date.now() - Date.parse(latest.timestamp) <= RemoteHostsService.DEGRADED_CLIENT_MS
            && host.status !== 'offline' && host.status !== 'retired';
          const stats = fresh
            ? {
                cpuCores: latest.cpuCores || 0,
                cpuModel: 'reported by daemon',
                cpuLoadPct: latest.cpuPercent ?? 0,
                ramTotalMb: latest.memoryTotalBytes ? latest.memoryTotalBytes / 1024 / 1024 : 0,
                ramFreeMb: latest.memoryTotalBytes && latest.memoryUsedBytes !== null
                  ? (latest.memoryTotalBytes - latest.memoryUsedBytes) / 1024 / 1024
                  : 0,
                diskTotalGb: 0,
                diskFreeGb: 0,
            }
            : null;
          const nextTelemetry = preserveLongTelemetry && host.telemetry?.window === '14d'
            ? mergeRecentTelemetry(host.telemetry, telemetry)
            : telemetry;
          return { ...host, stats, telemetry: nextTelemetry, telemetryLoading: false };
        });
        this.log('telemetry-hydrated', { hostId, points: telemetry.points.length, findings: telemetry.findings.length,
          durationMs: Math.round(performance.now() - startedAt) });
      },
      error: error => {
        this.patch(hostId, host => ({ ...host, stats: null, telemetry: null, telemetryLoading: false }));
        this.log('telemetry-hydrate-failed', { hostId, message: error?.message ?? 'unknown',
          durationMs: Math.round(performance.now() - startedAt) });
      },
    });
  }

  /** Add the host produced by the UI-first setup wizard to this registry. */
  addProvisionedHost(name: string, address: string): void {
    const id = name.trim().toLowerCase().replace(/[^a-z0-9-]+/g, '-') || `runner-${Date.now()}`;
    const host: RemoteHost = {
      id,
      name: name.trim() || id,
      role: 'remote',
      address: address.trim(),
      clientId: id,
      status: 'idle',
      os: 'Ubuntu LTS',
      lastHeartbeatAt: new Date().toISOString(),
      uptimeLabel: 'just provisioned',
      capabilities: ['linux', 'git', 'node 22', 'dotnet 10', 'playwright'],
      cliQuotas: [],
      stats: null,
      liveDataState: 'ready',
    };
    this.hosts.update((hosts) => [...hosts.filter((item) => item.id !== id), host]);
    this.log('wizard-completed', { hostId: id, address: host.address });
  }

  reprobe(id: string): void {
    const host = this.hosts().find(item => item.id === id);
    if (!host || host.busyAction || !this.http) return;
    this.log('reprobe-requested', { hostId: id });
    this.patch(id, item => ({ ...item, busyAction: 'reprobe' }));
    this.http.post(`/api/clients/${encodeURIComponent(host.clientId)}/runner-project-preflights/invalidate`, {}).subscribe({
      next: () => this.reload(),
      error: error => this.actionFailed(id, error),
    });
  }

  reconnect(id: string): void {
    const host = this.hosts().find(item => item.id === id || item.clientId === id);
    if (!host || host.busyAction || !this.http) return;
    this.patch(host.id, item => ({ ...item, busyAction: 'reconnect' }));
    this.http.post<RemoteRunnerReconnectResponse>(
      `/api/v1/management/remote-hosts/${encodeURIComponent(host.clientId)}/reconnect`,
      {},
    ).subscribe({
      next: result => {
        this.patch(host.id, item => ({ ...item, busyAction: null }));
        if (result.succeeded) {
          this.notifications?.success(
            `${result.detail} Current runner link state: ${result.linkState}; snapshot age: ${formatSnapshotAge(result.nextSnapshotAgeSeconds)}.`,
            `${host.name} reconnect started`,
          );
          this.reload();
        }
      },
      error: error => this.actionFailed(host.id, error),
    });
  }

  /** Ask a host to finish current work and stop taking more. */
  drain(id: string): void {
    this.postAction(id, 'drain', `/api/clients/${encodeURIComponent(this.clientId(id))}/drain`);
  }

  /** Drain immediately and retire only after the daemon reports zero active slots. */
  retire(id: string): void {
    this.postAction(id, 'retire', `/api/clients/${encodeURIComponent(this.clientId(id))}/retire`);
  }

  revive(id: string): void {
    this.postAction(id, 'revive', `/api/clients/${encodeURIComponent(this.clientId(id))}/revive`);
  }

  /**
   * Persist the host's central capacity targets. The record is owned either by
   * the standalone Task Server (versioned, optimistic concurrency) or by the
   * monolith's client identity (unversioned, version 0); the write goes to
   * whichever server produced the record this row is showing.
   *
   * A host with no published ceiling at all is a valid starting point, not an
   * error: the client-identity route creates the record, so the host row can
   * offer a "set a ceiling" form instead of a dead end.
   */
  setCapacity(
    id: string,
    maxParallelism: number,
    targetLoadPercent: number,
    rampStrategy: HostRampStrategy,
  ): void {
    const current = this.hosts().find(host => host.id === id);
    if (!current || current.busyAction || !this.http) return;
    const capacity = current.runtimeCapacity;

    const hostId = current.capacityHostId;
    if (!capacity || !hostId || capacity.version < 1) {
      this.setClientCapacity(current, maxParallelism, targetLoadPercent, rampStrategy);
      return;
    }

    this.patch(id, host => ({ ...host, busyAction: 'capacity' }));
    this.http.put<NonNullable<RemoteHost['runtimeCapacity']>>(
      `/api/v1/hosts/${encodeURIComponent(hostId)}/runtime-capacity`,
      {
        maxParallelism,
        targetLoadPercent,
        rampStrategy,
        expectedVersion: capacity.version,
      },
    ).subscribe({
      next: updated => {
        this.hosts.update(hosts => hosts.map(host =>
          host.capacityHostId === hostId
            ? {
                ...host,
                runtimeCapacity: updated,
                busyAction: host.id === id ? null : host.busyAction,
              }
            : host));
        this.log('capacity-saved', {
          hostId,
          maxParallelism,
          targetLoadPercent,
          rampStrategy,
        });
      },
      error: error => this.actionFailed(id, error),
    });
  }

  /** Monolith write path: capacity lives on the host's client identity. */
  private setClientCapacity(
    current: RemoteHost,
    maxParallelism: number,
    targetLoadPercent: number,
    rampStrategy: HostRampStrategy,
  ): void {
    if (!this.http) return;
    this.patch(current.id, host => ({ ...host, busyAction: 'capacity' }));
    this.http.put<ClientSummary>(
      `/api/clients/${encodeURIComponent(current.clientId)}/runner-capacity`,
      { maxParallelism, targetLoadPercent, rampStrategy },
    ).subscribe({
      next: client => {
        this.patch(current.id, host => ({
          ...host,
          ...clientCapacity(host, client),
          availableSlots: client.runnerAvailableSlots ?? host.availableSlots,
          busyAction: null,
        }));
        this.log('capacity-saved', {
          hostId: current.clientId,
          maxParallelism,
          targetLoadPercent,
          rampStrategy,
        });
      },
      error: error => this.actionFailed(current.id, error),
    });
  }

  /** Persist host-to-project claim admission in the Task Server. */
  setProjectPolicy(
    id: string,
    allowAllProjects: boolean,
    allowedProjectIds: readonly string[],
    expectedVersion: number,
  ): void {
    const current = this.hosts().find(host => host.id === id);
    const hostId = current?.capacityHostId;
    if (!current || current.busyAction || !hostId || !this.http) return;
    this.patch(id, host => ({ ...host, busyAction: 'project-policy' }));
    this.http.put<NonNullable<RemoteHost['projectPolicy']>>(
      `/api/v1/hosts/${encodeURIComponent(hostId)}/project-policy`,
      { allowAllProjects, allowedProjectIds, expectedVersion },
    ).subscribe({
      next: updated => {
        this.hosts.update(hosts => hosts.map(host =>
          host.capacityHostId === hostId
            ? {
                ...host,
                projectPolicy: updated,
                busyAction: host.id === id ? null : host.busyAction,
              }
            : host));
        this.log('project-policy-saved', {
          hostId,
          allowAllProjects,
          allowedProjectIds,
        });
      },
      error: error => this.actionFailed(id, error),
    });
  }

  permanentlyDelete(id: string): void {
    const host = this.hosts().find(item => item.id === id);
    if (!host || !this.http) return;
    this.patch(id, item => ({ ...item, busyAction: 'delete' }));
    this.http.delete(`/api/clients/${encodeURIComponent(host.clientId)}/permanent`).subscribe({
      next: () => this.hosts.update(items => items.filter(item => item.id !== id)),
      error: error => this.actionFailed(id, error),
    });
  }

  /**
   * Apply an action optimistically: flag the row busy, then commit the change
   * after a short simulated delay. Concurrent actions on the same host are
   * ignored while one is in flight.
   */
  private postAction(id: string, kind: HostActionKind, url: string): void {
    const current = this.hosts().find((h) => h.id === id);
    if (!current || current.busyAction || !this.http) return;

    this.log('action', { kind, hostId: id });
    this.patch(id, (host) => ({ ...host, busyAction: kind }));

    this.http.post<ClientSummary>(url, {}).subscribe({
      next: () => {
        this.patch(id, host => ({ ...host, busyAction: null }));
        this.log('action-applied', { kind, hostId: id });
        this.reload();
      },
      error: error => this.actionFailed(id, error),
    });
  }

  private actionFailed(id: string, error: unknown): void {
    this.patch(id, host => ({ ...host, busyAction: null }));
    this.error.set('The host lifecycle change could not be saved. Nothing was changed.');
    this.log('action-failed', { hostId: id, message: (error as { message?: string })?.message ?? 'unknown' });
  }

  private clientId(id: string): string { return this.hosts().find(host => host.id === id)?.clientId ?? id; }

  private patch(id: string, apply: (host: RemoteHost) => RemoteHost): void {
    this.hosts.update((list) => list.map((h) => (h.id === id ? apply(h) : h)));
  }

  private log(event: string, detail: Record<string, unknown>): void {
    // Stable event names so the browser log reads as a domain feed while the
    // real backend command surface is still being built.
    console.info(`[remote-hosts] ${event}`, { event: `remote-host.${event}`, ...detail });
  }
}

function telemetryStats(telemetry: TaskServerTelemetrySnapshot): NonNullable<RemoteHost['stats']> {
  return {
    ramTotalMb: (telemetry.memoryTotalBytes ?? 0) / 1024 / 1024,
    ramFreeMb: Math.max(0, ((telemetry.memoryTotalBytes ?? 0) - (telemetry.memoryUsedBytes ?? 0)) / 1024 / 1024),
    cpuCores: telemetry.cpuCores,
    cpuModel: 'reported by daemon',
    cpuLoadPct: telemetry.cpuPercent ?? 0,
    diskTotalGb: (telemetry.diskTotalBytes ?? 0) / 1024 / 1024 / 1024,
    diskFreeGb: (telemetry.diskFreeBytes ?? 0) / 1024 / 1024 / 1024,
  };
}

function taskServerConnection(
  telemetry: TaskServerTelemetrySnapshot,
): NonNullable<RemoteHost['taskServerConnection']> {
  return {
    status: telemetry.taskServerConnectionStatus ?? 'unknown',
    observedAt: telemetry.taskServerConnectionObservedAt ?? null,
    failureStartedAt: telemetry.taskServerConnectionFailureStartedAt ?? null,
    consecutiveFailures: telemetry.taskServerConnectionConsecutiveFailures ?? 0,
    escalatedAt: telemetry.taskServerConnectionEscalatedAt ?? null,
    lastError: telemetry.taskServerConnectionLastError ?? null,
    lastRecoveredAt: telemetry.taskServerConnectionLastRecoveredAt ?? null,
  };
}

function statusFor(lastSeenAt: string | null, now: number): RemoteHost['status'] {
  const seen = lastSeenAt ? Date.parse(lastSeenAt) : Number.NaN;
  if (Number.isNaN(seen) || now - seen > 5 * 60_000) return 'offline';
  return now - seen <= 90_000 ? 'online' : 'degraded';
}

function projectClient(host: RemoteHost, client: ClientSummary, status: RemoteHost['status']): RemoteHost {
  return {
    ...host, status, lastHeartbeatAt: client.lastSeenAt, stats: null, telemetry: null,
    liveDataState: 'ready', telemetryLoading: status !== 'retired' && !client.identityFileError,
    identityFileError: client.identityFileError ?? null,
    identityRestoreHint: client.identityRestoreHint ?? null,
    gitPushStatus: client.runnerGitStatus ?? null, gitPushDetail: client.runnerGitDetail ?? null,
    gitPushCheckedAt: client.runnerGitCheckedAt ?? null,
    projectPreflights: client.runnerProjectPreflights ?? [],
    daemonState: status === 'offline' || status === 'retired' ? 'stopped' : client.runnerDaemonState ?? 'running',
    lastClaimAt: client.runnerLastClaimAt ?? null, activeTaskCount: client.runnerActiveSlots ?? 0,
    availableSlots: client.runnerAvailableSlots ?? 0, retireRequestedAt: client.retireRequestedAt ?? null,
    ...clientCapacity(host, client),
  };
}

/**
 * The monolith keeps host capacity on the client identity. Project it into the
 * same shape the standalone Task Server returns so the host row has one capacity
 * concept regardless of which server answers. Version 0 marks the unversioned
 * client-identity record; {@link RemoteHostsService.setCapacity} uses it to pick
 * the write route. The Task Server projection runs afterwards and wins when it
 * carries a record of its own.
 *
 * A record that already came from the Task Server (version >= 1) is never
 * replaced here. The /api/clients poll repeats on every reload, so overwriting
 * it would silently demote the row to version 0 and send the next save to the
 * monolith route - losing the optimistic-concurrency check. Only the
 * daemon-reported adoption telemetry is merged in from the client identity.
 */
function clientCapacity(host: RemoteHost, client: ClientSummary): Partial<RemoteHost> {
  const taskServerOwned = (host.runtimeCapacity?.version ?? 0) >= 1;
  if (taskServerOwned) {
    return {
      runtimeCapacity: host.runtimeCapacity,
      effectiveMaxParallelism: client.runnerEffectiveMaxParallelism ?? host.effectiveMaxParallelism ?? null,
      runtimeCapacityAppliedAt:
        client.runnerEffectiveMaxParallelismAppliedAt ?? host.runtimeCapacityAppliedAt ?? null,
      runtimeCapacityAppliedVersion: host.runtimeCapacityAppliedVersion ?? null,
    };
  }
  if (client.runnerDesiredMaxParallelism === null
    || client.runnerDesiredMaxParallelism === undefined) {
    return {
      runtimeCapacity: host.runtimeCapacity ?? null,
      effectiveMaxParallelism: client.runnerEffectiveMaxParallelism ?? host.effectiveMaxParallelism ?? null,
    };
  }
  return {
    runtimeCapacity: {
      hostId: client.id,
      maxParallelism: client.runnerDesiredMaxParallelism,
      targetLoadPercent: client.runnerTargetLoadPercent ?? 80,
      rampStrategy: client.runnerRampStrategy ?? 'balanced',
      version: 0,
      updatedAt: client.runnerCapacityUpdatedAt ?? client.lastSeenAt ?? '',
    },
    effectiveMaxParallelism: client.runnerEffectiveMaxParallelism ?? null,
    runtimeCapacityAppliedAt: client.runnerEffectiveMaxParallelismAppliedAt ?? null,
    runtimeCapacityAppliedVersion: null,
  };
}

function isRunnerIdentity(client: ClientSummary): boolean {
  return client.kind === 'retired' || !!client.runnerGitStatus || /runner|host/i.test(`${client.id} ${client.displayName}`);
}

function serviceRole(snapshot: TaskServerRunnerCapabilitySnapshot): RemoteHost['serviceRole'] {
  if (snapshot.capabilities.some(capability => capability.key === 'executor:review')) return 'review';
  if (snapshot.capabilities.some(capability => capability.key === 'executor:coding')) return 'coding';
  return /review/i.test(`${snapshot.runnerId} ${snapshot.name}`) ? 'review' : 'runner';
}

function mergeRecentTelemetry(
  existing: HostTelemetrySeries,
  recent: HostTelemetrySeries,
): HostTelemetrySeries {
  const points = new Map(
    [...existing.points, ...recent.points].map(point => [point.timestamp, point]),
  );
  const findings = new Map(
    [...existing.findings, ...recent.findings]
      .map(finding => [
        `${finding.kind}:${finding.isActive === false ? 'history' : 'active'}`,
        finding,
      ]),
  );
  return {
    ...existing,
    points: [...points.values()].sort((a, b) => a.timestamp.localeCompare(b.timestamp)),
    findings: [...findings.values()],
  };
}

function tryInjectHttpClient(): HttpClient | null {
  try {
    return inject(HttpClient, { optional: true });
  } catch {
    return null;
  }
}

function tryInjectProviderAuthStatus(): ProviderAuthStatusService | null {
  try {
    return inject(ProviderAuthStatusService, { optional: true });
  } catch {
    return null;
  }
}

function tryInjectNotificationService(): NotificationService | null {
  try {
    return inject(NotificationService, { optional: true });
  } catch {
    return null;
  }
}

function keeperCauseLabel(cause: NonNullable<RemoteRunnerLinkHealth['keeper']>['cause']): string {
  switch (cause) {
    case 'task-disabled': return 'The tunnel keeper Scheduled Task is disabled';
    case 'not-running': return 'The tunnel keeper Scheduled Task is not running';
    case 'ssh-not-running': return 'The tunnel keeper has no SSH reverse-forward process';
    case 'probe-failing': return 'The tunnel keeper functional probe is failing';
    default: return 'The tunnel keeper is unhealthy';
  }
}

function formatSnapshotAge(seconds: number | null): string {
  if (seconds === null) return 'not available';
  if (seconds < 60) return `${Math.round(seconds)} seconds`;
  return `${Math.round(seconds / 60)} minutes`;
}
