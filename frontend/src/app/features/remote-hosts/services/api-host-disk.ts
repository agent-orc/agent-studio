import type { HttpClient } from '@angular/common/http';
import type { WritableSignal } from '@angular/core';
import type { HostFilesystemUsage, RemoteHost } from '../models/remote-host.model';

interface DiskReading {
  id: string; host: string; observedAt: string; filesystems: readonly HostFilesystemUsage[];
  level: 'healthy' | 'warning' | 'critical'; reason: string;
}

/** Hydrates the measured API host only when the Execution Hosts view opens. */
export function hydrateApiHost(http: HttpClient, hosts: WritableSignal<RemoteHost[]>, local: boolean): void {
  http.get<{ host: string; observedAt: string; filesystems: readonly HostFilesystemUsage[];
    level: 'healthy' | 'warning' | 'critical'; reason: string; remoteHosts?: readonly DiskReading[] }>('/api/host/filesystems').subscribe({
    next: reading => {
      const id = local ? 'local' : 'api-host';
      hosts.update(hostsNow => {
        const currentHosts = hostsNow.filter(host => host.id !== (local ? 'api-host' : 'local'));
        const current = currentHosts.find(host => host.id === id);
        const base: RemoteHost = current ?? {
          id, name: local ? 'Local machine' : reading.host, role: 'local', serviceRole: 'local',
          address: null, clientId: 'api-host', status: 'online', os: 'API host',
          lastHeartbeatAt: reading.observedAt, uptimeLabel: null,
          capabilities: ['API', 'instance data'], cliQuotas: [], stats: null,
        };
        const updated: RemoteHost = {
          ...base,
          status: reading.level === 'healthy' ? 'online' : 'degraded',
          lastHeartbeatAt: reading.observedAt, filesystems: reading.filesystems,
          diskPressureReason: reading.level === 'healthy' ? null : reading.reason,
          liveDataState: 'ready',
        };
        const hydrated = current ? currentHosts.map(host => host.id === id ? updated : host) : [...currentHosts, updated];
        for (const remote of reading.remoteHosts ?? []) {
          const gate: RemoteHost = {
            id: remote.id, name: remote.host, role: 'remote', serviceRole: 'runner', address: remote.host,
            clientId: remote.id, status: remote.level === 'healthy' ? 'online' : 'degraded', os: 'Gate host',
            lastHeartbeatAt: remote.observedAt, uptimeLabel: null, capabilities: ['Gate'], cliQuotas: [], stats: null,
            filesystems: remote.filesystems, diskPressureReason: remote.level === 'healthy' ? null : remote.reason,
            liveDataState: 'ready',
          };
          const index = hydrated.findIndex(host => host.id === remote.id);
          if (index >= 0) hydrated[index] = gate;
          else hydrated.push(gate);
        }
        return hydrated;
      });
    },
    error: () => hosts.update(current => current.map(host => host.clientId === 'api-host'
      ? { ...host, liveDataState: 'error' } : host)),
  });
}
