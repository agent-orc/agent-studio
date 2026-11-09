import { describe, expect, it } from 'vitest';
import {
  clampPct,
  diskUsedPct,
  formatDisk,
  formatMemory,
  hostRoleLabel,
  hostStatusLabel,
  hostStatusTone,
  meterTone,
  ramUsedPct,
  relativeHeartbeat,
  taskServerRouteDetail,
  taskServerRouteStatus,
  type HostSystemStats,
  type RemoteHost,
} from './remote-host.model';

const stats: HostSystemStats = {
  ramTotalMb: 62 * 1024,
  ramFreeMb: 38 * 1024,
  cpuCores: 8,
  cpuModel: 'Xeon',
  cpuLoadPct: 54,
  diskTotalGb: 240,
  diskFreeGb: 96,
};

describe('remote-host.model helpers', () => {
  it('formats memory as GB and disk as whole GB, with a dash for unknowns', () => {
    expect(formatMemory(41 * 1024)).toBe('41.0 GB');
    expect(formatMemory(null)).toBe('-');
    expect(formatDisk(180)).toBe('180 GB');
    expect(formatDisk(undefined)).toBe('-');
  });

  it('clamps percentages into 0-100', () => {
    expect(clampPct(-5)).toBe(0);
    expect(clampPct(140)).toBe(100);
    expect(clampPct(63)).toBe(63);
    expect(clampPct(null)).toBe(0);
  });

  it('maps utilisation to a tone, acute red only past 90 (R4)', () => {
    expect(meterTone(10)).toBe('ok');
    expect(meterTone(75)).toBe('warn');
    expect(meterTone(95)).toBe('high');
  });

  it('labels every heartbeat status', () => {
    expect(hostStatusLabel('online')).toBe('Online');
    expect(hostStatusLabel('draining')).toBe('Draining');
    expect(hostStatusLabel('retired')).toBe('Retired');
  });

  it('gives acute tone only to degraded/offline; settled states stay calm', () => {
    expect(hostStatusTone('online')).toBe('ok');
    expect(hostStatusTone('idle')).toBe('idle');
    expect(hostStatusTone('degraded')).toBe('warn');
    expect(hostStatusTone('offline')).toBe('error');
    expect(hostStatusTone('draining')).toBe('idle');
    expect(hostStatusTone('retired')).toBe('calm');
  });

  it('labels host role', () => {
    expect(hostRoleLabel('local')).toBe('Local');
    expect(hostRoleLabel('remote')).toBe('Remote');
  });

  it('computes used RAM / disk percentages, null when stats missing', () => {
    // 62 total, 38 free => 24 used => ~38.7%
    expect(ramUsedPct(stats)).toBe(39);
    // 240 total, 96 free => 144 used => 60%
    expect(diskUsedPct(stats)).toBe(60);
    expect(ramUsedPct(null)).toBeNull();
    expect(diskUsedPct(undefined)).toBeNull();
  });

  it('renders relative heartbeat ages against an injected clock', () => {
    const now = Date.parse('2026-07-10T12:00:00Z');
    expect(relativeHeartbeat(null, now)).toBe('never');
    expect(relativeHeartbeat('2026-07-10T11:59:50Z', now)).toBe('just now');
    expect(relativeHeartbeat('2026-07-10T11:58:00Z', now)).toBe('2m ago');
    expect(relativeHeartbeat('2026-07-10T09:00:00Z', now)).toBe('3h ago');
    expect(relativeHeartbeat('2026-07-08T12:00:00Z', now)).toBe('2d ago');
    expect(relativeHeartbeat('not-a-date', now)).toBe('never');
  });

  it('treats an expired connectivity capability as an unavailable route', () => {
    const advertisedAt = '2026-08-01T15:30:00Z';
    const host = {
      id: 'runner', name: 'Runner', role: 'remote', address: null, clientId: 'runner',
      status: 'offline', os: 'Linux', lastHeartbeatAt: advertisedAt, uptimeLabel: null,
      capabilities: [], cliQuotas: [], stats: null,
      capabilityHealth: [{
        key: 'task-server:connectivity', category: 'foundation', advertisedStatus: 'ready',
        healthState: 'healthy', reason: null, advertisedAt,
        freshUntil: '2026-08-01T15:33:00Z', isFresh: false, consecutiveFailures: 0,
        affectedClaims: [], recoveryHistory: [],
      }],
      taskServerConnection: {
        status: 'reachable', observedAt: advertisedAt, failureStartedAt: null,
        consecutiveFailures: 0, escalatedAt: null, lastError: null, lastRecoveredAt: null,
      },
    } satisfies RemoteHost;

    expect(taskServerRouteStatus(host)).toBe('unreachable');
    expect(taskServerRouteDetail(host)).toContain('No connectivity advertisement has arrived');
    expect(taskServerRouteDetail(host)).toContain('Check the tunnel');
  });

  it('keeps a fresh ready route when telemetry records a short failure streak', () => {
    const host = {
      id: 'runner', name: 'Runner', role: 'remote', address: null, clientId: 'runner',
      status: 'online', os: 'Linux', lastHeartbeatAt: '2026-08-01T15:30:00Z',
      uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
      capabilityHealth: [{
        key: 'task-server:connectivity', category: 'foundation', advertisedStatus: 'ready',
        healthState: 'healthy', reason: null, advertisedAt: '2026-08-01T15:30:00Z',
        freshUntil: '2026-08-01T15:33:00Z', isFresh: true, consecutiveFailures: 0,
        detail: 'Task Server route has failures since 15:30; 1 consecutive request failures.',
        affectedClaims: [], recoveryHistory: [],
      }],
      taskServerConnection: {
        status: 'unreachable', observedAt: '2026-08-01T15:30:00Z',
        failureStartedAt: '2026-08-01T15:30:00Z', consecutiveFailures: 1,
        escalatedAt: null, lastError: 'origin timeout', lastRecoveredAt: null,
      },
    } satisfies RemoteHost;
    expect(taskServerRouteStatus(host)).toBe('reachable');
    expect(taskServerRouteDetail(host)).toContain('1 consecutive request failures');
    const legacy = { ...host, capabilityHealth: [] };
    expect(taskServerRouteStatus(legacy)).toBe('reachable');
    expect(taskServerRouteStatus({ ...legacy, taskServerConnection: {
      ...legacy.taskServerConnection, observedAt: '2026-08-01T15:31:00Z', consecutiveFailures: 3,
    } })).toBe('unreachable');
  });

  it('does not turn an intentionally retired host into a route outage', () => {
    const host = {
      id: 'runner', name: 'Runner', role: 'remote', address: null, clientId: 'runner',
      status: 'retired', os: 'Linux', lastHeartbeatAt: '2026-08-01T15:30:00Z',
      uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
      capabilityHealth: [{
        key: 'task-server:connectivity', category: 'foundation', advertisedStatus: 'ready',
        healthState: 'healthy', reason: null, advertisedAt: '2026-08-01T15:30:00Z',
        freshUntil: '2026-08-01T15:33:00Z', isFresh: false, consecutiveFailures: 0,
        affectedClaims: [], recoveryHistory: [],
      }],
    } satisfies RemoteHost;

    expect(taskServerRouteStatus(host)).toBe('unknown');
  });
});
