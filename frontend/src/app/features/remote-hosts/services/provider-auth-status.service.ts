import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { Observable, filter, map, switchMap, take, tap, timer, timeout } from 'rxjs';
import { NotificationService } from '../../../services/notification.service';
import {
  providerAuthBadgesForSnapshot,
  type ProviderAuthBadge,
  type ProviderAuthProvisioningRequest,
  type ProviderAuthProvisioningResponse,
} from '../models/provider-auth.model';
import type { RemoteRunnerLinkHealth, TaskServerRunnerCapabilitySnapshot } from '../models/remote-host.model';

@Injectable({ providedIn: 'root' })
export class ProviderAuthStatusService implements OnDestroy {
  private static readonly RefreshMs = 60_000;
  private readonly http = inject(HttpClient, { optional: true });
  private readonly notifications = inject(NotificationService);
  private readonly snapshots = signal<readonly TaskServerRunnerCapabilitySnapshot[]>([]);
  readonly links = signal<readonly RemoteRunnerLinkHealth[]>([]);
  private timer: ReturnType<typeof setInterval> | null = null;
  private previous = new Map<string, ProviderAuthBadge>();
  private expiryWarnings = new Set<string>();

  readonly loaded = signal(false);
  readonly statuses = computed(() => this.snapshots()
    .flatMap(snapshot => providerAuthBadgesForSnapshot(snapshot, Date.now())));

  start(): void {
    if (this.timer || !this.http) return;
    this.refresh();
    this.timer = setInterval(() => this.refresh(), ProviderAuthStatusService.RefreshMs);
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  ngOnDestroy(): void {
    this.stop();
  }

  refresh(): void {
    if (!this.http) return;
    this.http.get<TaskServerRunnerCapabilitySnapshot[]>('/api/v1/management/remote-hosts').subscribe({
      next: snapshots => {
        this.ingest(snapshots ?? []);
        this.refreshLinks();
      },
      error: () => undefined,
    });
  }

  private refreshLinks(): void {
    this.http?.get<RemoteRunnerLinkHealth[]>('/api/v1/management/remote-hosts/link-health').subscribe({
      next: links => this.links.set(links ?? []),
      error: () => undefined,
    });
  }

  ingest(snapshots: readonly TaskServerRunnerCapabilitySnapshot[]): void {
    const wasLoaded = this.loaded();
    this.snapshots.set(snapshots);
    const next = new Map(this.statuses().map(status => [status.id, status]));
    if (wasLoaded) {
      for (const [id, current] of next) {
        const prior = this.previous.get(id);
        if (prior?.state === 'ok'
          && current.state === 'unavailable'
          && current.consecutiveFailures >= 2
          && (!current.signal || current.signal === 'signed-out')) {
          this.notifications.warning(
            `${current.providerLabel} authentication changed from OK to unavailable on ${current.hostName}. Ready cards assigned to this host are waiting. ${current.detail}`,
            `${current.providerLabel} sign-in required`,
          );
        }
      }
    }
    for (const current of next.values()) {
      if (!current.expiresSoon || !current.expiresAt || !current.expiryLabel) continue;
      const warningKey = `${current.id}:${current.expiresAt}`;
      if (this.expiryWarnings.has(warningKey)) continue;
      this.expiryWarnings.add(warningKey);
      this.notifications.warning(
        `${current.providerLabel} authentication on ${current.hostName} ${current.expiryLabel.toLowerCase()}. Renew it before Ready cards are held.`,
        `${current.providerLabel} authentication expires soon`,
      );
    }
    this.previous = next;
    this.loaded.set(true);
  }

  provision(request: ProviderAuthProvisioningRequest): Observable<ProviderAuthProvisioningResponse> {
    if (!this.http) throw new Error('Provider-auth provisioning requires the Studio HTTP client.');
    return this.http.post<ProviderAuthProvisioningResponse>(
      '/api/v1/management/remote-hosts/provider-auth',
      request,
    );
  }

  waitForFreshProbe(
    provider: string,
    aliases: readonly string[],
    baselineAdvertisedAt: string | null,
    timeoutMs = 75_000,
  ): Observable<ProviderAuthBadge> {
    if (!this.http) throw new Error('Provider-auth verification requires the Studio HTTP client.');
    const baseline = baselineAdvertisedAt ? Date.parse(baselineAdvertisedAt) : Number.NEGATIVE_INFINITY;
    const normalizedAliases = new Set(aliases.filter(Boolean).map(alias => alias.toLowerCase()));
    return timer(0, 2_000).pipe(
      switchMap(() => this.http!.get<TaskServerRunnerCapabilitySnapshot[]>('/api/v1/management/remote-hosts')),
      tap(snapshots => this.ingest(snapshots ?? [])),
      map(() => this.statuses().find(status =>
        status.provider === provider
        && status.aliases.some(alias => normalizedAliases.has(alias.toLowerCase()))
        && (status.advertisedAt ? Date.parse(status.advertisedAt) > baseline : false))),
      filter((status): status is ProviderAuthBadge => !!status),
      take(1),
      timeout({ first: timeoutMs }),
    );
  }
}
