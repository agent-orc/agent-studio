import type { RemoteHost } from '../models/remote-host.model';

/**
 * The local machine is intrinsic to a local profile. Remote runners are
 * discovered from the client and capability registries after they report in.
 */
export function seedRemoteHosts(_nowMs: number, profile = 'local'): RemoteHost[] {
  void _nowMs;
  if (profile !== 'local') return [];
  return [
    {
      id: 'local',
      name: 'Local machine',
      role: 'local',
      serviceRole: 'local',
      address: null,
      clientId: 'local-default',
      status: 'offline',
      os: 'This machine',
      lastHeartbeatAt: null,
      uptimeLabel: null,
      capabilities: ['local', 'default execution host'],
      cliQuotas: [],
      stats: null,
    },
  ];
}
