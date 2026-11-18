import type { RunHostStatus } from '../../../models/run-host.model';
import type { RemoteHost } from './remote-host.model';

/**
 * A run host sends no claim heartbeat: its liveness is its last deploy claim,
 * report or run-target poll, which the server already turned into a state.
 */
export function projectRunHost(current: RemoteHost | undefined, host: RunHostStatus): RemoteHost {
  const status = current?.status === 'retired' ? 'retired'
    : current?.status === 'draining' ? 'draining'
      : host.state === 'reachable' ? 'online' : 'offline';
  return {
    ...(current ?? {
      id: host.id, role: 'remote', address: null, clientId: host.id, os: 'Run host',
      uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
    }),
    name: host.name,
    serviceRole: 'deploy',
    status,
    lastHeartbeatAt: host.lastSeenAt,
    lastClaimAt: host.lastClaimAt,
    lastReportAt: host.lastReportAt,
    daemonState: status === 'online' ? 'running' : 'stopped',
    liveDataState: 'ready',
  };
}
