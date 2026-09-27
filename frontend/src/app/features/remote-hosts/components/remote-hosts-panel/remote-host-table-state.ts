import { signal } from '@angular/core';
import type { RemoteHost } from '../../models/remote-host.model';
import type { PhysicalHostGroup } from '../../models/physical-host-group';

export type RemoteHostSortKey = 'name' | 'status' | 'slots' | 'load' | 'activity' | 'release';
export type RemoteHostSortDirection = 'asc' | 'desc';

interface PersistedRemoteHostTableState {
  sortKey: RemoteHostSortKey;
  direction: RemoteHostSortDirection;
  expandedHostIds: readonly string[];
}

export const REMOTE_HOST_TABLE_SESSION_KEY = 'atp.execution-hosts.table.v1';

const SORT_KEYS = new Set<RemoteHostSortKey>([
  'name',
  'status',
  'slots',
  'load',
  'activity',
  'release',
]);
const COLLATOR = new Intl.Collator('en-US', { numeric: true, sensitivity: 'base' });

export class RemoteHostTableState {
  readonly sortKey = signal<RemoteHostSortKey>('name');
  readonly direction = signal<RemoteHostSortDirection>('asc');
  readonly expandedHostIds = signal<readonly string[]>([]);

  hydrate(): void {
    if (typeof window === 'undefined') return;
    try {
      const parsed = JSON.parse(
        window.sessionStorage.getItem(REMOTE_HOST_TABLE_SESSION_KEY) ?? 'null',
      ) as Partial<PersistedRemoteHostTableState> | null;
      if (!parsed || !isSortKey(parsed.sortKey)) return;
      this.sortKey.set(parsed.sortKey);
      this.direction.set(parsed.direction === 'desc' ? 'desc' : 'asc');
      this.expandedHostIds.set(
        Array.isArray(parsed.expandedHostIds)
          ? parsed.expandedHostIds.filter((id): id is string => typeof id === 'string')
          : [],
      );
    } catch {
      // Session storage may contain stale data or be unavailable in private contexts.
    }
  }

  selectSort(key: RemoteHostSortKey): void {
    if (this.sortKey() === key) {
      this.direction.update(value => value === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortKey.set(key);
      this.direction.set(defaultDirection(key));
    }
    this.persist();
  }

  isExpanded(hostId: string): boolean {
    return this.expandedHostIds().includes(hostId);
  }

  setExpanded(hostId: string, expanded: boolean): void {
    this.expandedHostIds.update(ids => expanded
      ? ids.includes(hostId) ? ids : [...ids, hostId]
      : ids.filter(id => id !== hostId));
    this.persist();
  }

  sort(
    hosts: readonly PhysicalHostGroup[],
    occupiedSlots: (host: RemoteHost) => number,
  ): PhysicalHostGroup[] {
    const key = this.sortKey();
    const direction = this.direction() === 'asc' ? 1 : -1;
    return hosts
      .map((host, index) => ({ host, index }))
      .sort((left, right) => {
        const compared = compareHosts(left.host, right.host, key, occupiedSlots);
        return compared === 0 ? left.index - right.index : compared * direction;
      })
      .map(entry => entry.host);
  }

  private persist(): void {
    if (typeof window === 'undefined') return;
    try {
      const state: PersistedRemoteHostTableState = {
        sortKey: this.sortKey(),
        direction: this.direction(),
        expandedHostIds: this.expandedHostIds(),
      };
      window.sessionStorage.setItem(REMOTE_HOST_TABLE_SESSION_KEY, JSON.stringify(state));
    } catch {
      // The table remains usable when storage is disabled.
    }
  }
}

function compareHosts(
  left: PhysicalHostGroup,
  right: PhysicalHostGroup,
  key: RemoteHostSortKey,
  occupiedSlots: (host: RemoteHost) => number,
): number {
  const leftMachine = left.machine;
  const rightMachine = right.machine;
  switch (key) {
    case 'status': return COLLATOR.compare(leftMachine.status, rightMachine.status);
    case 'slots': return totalSlots(left, occupiedSlots) - totalSlots(right, occupiedSlots);
    case 'load': return load(leftMachine) - load(rightMachine);
    case 'activity': return timestamp(leftMachine.lastHeartbeatAt) - timestamp(rightMachine.lastHeartbeatAt);
    case 'release': return COLLATOR.compare(leftMachine.releaseId ?? '', rightMachine.releaseId ?? '');
    case 'name': return COLLATOR.compare(left.name, right.name);
  }
}

function totalSlots(
  group: PhysicalHostGroup,
  occupiedSlots: (host: RemoteHost) => number,
): number {
  return group.roles.reduce((total, role) => total + occupiedSlots(role), 0);
}

function load(host: RemoteHost): number {
  return host.stats?.cpuLoadPct ?? -1;
}

function timestamp(value: string | null): number {
  if (!value) return 0;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function defaultDirection(key: RemoteHostSortKey): RemoteHostSortDirection {
  return key === 'slots' || key === 'load' || key === 'activity' ? 'desc' : 'asc';
}

function isSortKey(value: unknown): value is RemoteHostSortKey {
  return typeof value === 'string' && SORT_KEYS.has(value as RemoteHostSortKey);
}
