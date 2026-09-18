import { describe, expect, it } from 'vitest';
import type { RemoteHost } from './remote-host.model';
import { groupPhysicalHosts } from './physical-host-group';

describe('groupPhysicalHosts', () => {
  it('uses advertised host identity and the freshest machine telemetry once', () => {
    const coding = host('agent-runner-01', 'coding', 61, '2026-08-12T07:00:00Z');
    const review = host('agent-runner-01-review', 'review', 37, '2026-08-12T07:01:00Z');

    const group = expectSingle(groupPhysicalHosts([coding, review], false));

    expect(group.id).toBe('agent-runner-01');
    expect(group.roles.map(role => role.serviceRole)).toEqual(['coding', 'review']);
    expect(group.machine.stats?.cpuLoadPct).toBe(37);
    expect(group.machine.lastHeartbeatAt).toBe('2026-08-12T07:01:00Z');
  });

  it('hides retired roles by default and reveals them under the same machine', () => {
    const coding = host('coding', 'coding', 20, '2026-08-12T07:00:00Z');
    const retired = {
      ...host('review', 'review', 20, '2026-08-11T07:00:00Z'),
      status: 'retired' as const,
    };

    expect(expectSingle(groupPhysicalHosts([coding, retired], false)).roles.map(role => role.id))
      .toEqual(['coding']);
    expect(expectSingle(groupPhysicalHosts([coding, retired], true)).roles.map(role => role.id))
      .toEqual(['coding', 'review']);
  });

  it('keeps the runner name for snapshots without advertised host identity', () => {
    const legacy = {
      ...host('legacy-runner', 'coding', 20, '2026-08-12T07:00:00Z'),
      capacityHostId: null,
    };
    const group = expectSingle(groupPhysicalHosts([legacy], false));
    expect(group.id).toBe('runner:legacy-runner');
    expect(group.name).toBe('legacy-runner');
  });

  it('takes the aggregate release identity and warning from the worst-drift role', () => {
    const current = {
      ...host('coding', 'coding', 20, '2026-09-15T12:00:00Z'),
      releaseId: 'stable-release',
      release: {
        releaseId: 'stable-release', version: '0.3.0', commit: 'ccccccc3333',
        builtAt: '2026-09-11T08:00:00Z',
      },
      releaseDrift: drift('coding', 'current', null, false),
    } satisfies RemoteHost;
    const stale = {
      ...host('review', 'review', 20, '2026-09-15T11:59:00Z'),
      releaseId: 'stale-release',
      release: {
        releaseId: 'stale-release', version: '0.2.7', commit: 'bbbbbbb2222',
        builtAt: '2026-08-23T06:00:00Z',
      },
      releaseDrift: drift('review', 'behind', 458, true),
    } satisfies RemoteHost;

    const machine = expectSingle(groupPhysicalHosts([current, stale], false)).machine;

    expect(machine.releaseId).toBe('stale-release');
    expect(machine.release).toEqual(stale.release);
    expect(machine.releaseDrift).toBe(stale.releaseDrift);
  });
});

function drift(
  role: 'coding' | 'review',
  state: 'current' | 'behind',
  behindByHours: number | null,
  alarmDue: boolean,
) {
  return {
    runnerId: role,
    name: role,
    hostId: 'agent-runner-01',
    role,
    release: null,
    state,
    behindByHours,
    behindForHours: behindByHours,
    alarmDue,
    reason: state === 'behind' ? 'Older than Stable.' : 'Runs Stable.',
    lastSeenAt: '2026-09-15T12:00:00Z',
    heartbeatStale: false,
  } as const;
}

function host(
  id: string,
  serviceRole: RemoteHost['serviceRole'],
  cpuLoadPct: number,
  observedAt: string,
): RemoteHost {
  return {
    id,
    name: id,
    role: 'remote',
    serviceRole,
    address: null,
    clientId: id,
    capacityHostId: 'agent-runner-01',
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: observedAt,
    uptimeLabel: null,
    capabilities: [],
    cliQuotas: [],
    stats: {
      ramTotalMb: 1024,
      ramFreeMb: 512,
      cpuCores: 4,
      cpuModel: 'Test',
      cpuLoadPct,
      diskTotalGb: 10,
      diskFreeGb: 5,
    },
    telemetry: {
      clientId: id,
      window: '1h',
      findings: [],
      points: [{
        timestamp: observedAt,
        cpuPercent: cpuLoadPct,
        load1: 1,
        load5: 1,
        load15: 1,
        memoryUsedBytes: 512,
        memoryTotalBytes: 1024,
        swapInBytesPerSecond: 0,
        swapOutBytesPerSecond: 0,
        cpuStealPercent: 0,
        ioWaitPercent: 0,
        cpuCores: 4,
        activeSlots: 0,
      }],
    },
  };
}

function expectSingle(groups: ReturnType<typeof groupPhysicalHosts>) {
  expect(groups).toHaveLength(1);
  return groups[0];
}
