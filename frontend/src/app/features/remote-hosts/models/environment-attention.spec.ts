import { describe, expect, it } from 'vitest';
import { buildEnvironmentAttention } from './environment-attention';
import type { RemoteHost } from './remote-host.model';

function host(id: string, reason: string): RemoteHost {
  return {
    id, name: id, role: 'remote', serviceRole: 'coding', clientId: id,
    address: null, status: 'degraded', os: 'Linux', lastHeartbeatAt: '2026-11-02T10:00:00Z',
    uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
    runnerInstanceId: id, capacityHostId: id, hostAdmission: { admissionState: 'open' } as RemoteHost['hostAdmission'],
    taskServerConnection: { status: 'unreachable', observedAt: '2026-11-02T10:00:00Z',
      failureStartedAt: '2026-11-02T10:00:00Z', consecutiveFailures: 1,
      escalatedAt: null, lastError: reason, lastRecoveredAt: null },
    capabilityHealth: [{ key: 'task-server:connectivity', category: 'route', advertisedStatus: 'failed',
      healthState: 'suspect', reason, advertisedAt: '2026-11-02T10:00:00Z',
      freshUntil: '2026-11-02T11:00:00Z', isFresh: true, consecutiveFailures: 1,
      affectedClaims: [], recoveryHistory: [] }],
  };
}

describe('environment attention', () => {
  it('groups five roles with the same capability and HTTP code into one visible cause', () => {
    const hosts = Array.from({ length: 5 }, (_, index) => host(`runner-${index}`, 'HTTP 524: Task Server route unavailable'));
    const causes = buildEnvironmentAttention({ hosts, stalls: [], identities: [], failures: [], review: null,
      now: Date.parse('2026-11-02T10:01:00Z') });
    expect(causes).toHaveLength(1);
    expect(causes[0]).toMatchObject({ id: 'task-server:connectivity:524', count: 5 });
  });
  it('lists closed claim gates as one cause that names the host, load and duration', () => {
    const gate = (since: string) => ({ state: 'closed' as const, loadPerCore: 5.51, threshold: 4,
      sustainedSeconds: 1380, since, activeSlots: 4 });
    const healthy = (id: string): RemoteHost => ({ ...host(id, ''), status: 'online', taskServerConnection: null,
      capabilityHealth: [] });
    const now = Date.parse('2026-11-02T10:30:00Z');
    const single = buildEnvironmentAttention({ hosts: [{ ...healthy('runner-a'), claimLoadGate: gate('2026-11-02T10:07:00Z') }],
      stalls: [], identities: [], failures: [], review: null, now })
      .find(cause => cause.id === 'claim-load-gate:closed');
    expect(single).toMatchObject({ count: 1, hostId: 'runner-a', since: '2026-11-02T10:07:00Z',
      statement: 'runner-a: Not claiming: load 5.5 per core above 4.0 for 23 min. Running work continues.' });

    const both = buildEnvironmentAttention({ hosts: [
      { ...healthy('runner-b'), claimLoadGate: gate('2026-11-02T10:20:00Z') },
      { ...healthy('runner-a'), claimLoadGate: gate('2026-11-02T10:07:00Z') },
      { ...healthy('runner-c'), status: 'retired', claimLoadGate: gate('2026-11-02T10:00:00Z') },
    ], stalls: [], identities: [], failures: [], review: null, now })
      .find(cause => cause.id === 'claim-load-gate:closed');
    expect(both).toMatchObject({ count: 2, hostId: 'runner-a' });
    expect(both?.statement).toContain('2 hosts not claiming');
  });
  it('keeps unrecognised causes separate', () => {
    const causes = buildEnvironmentAttention({ hosts: [], stalls: [
      { id: 'a', project: 'p', subject: 'host:a', severity: 'warn', lastSummary: 'Unusual failure', affectedCards: [] },
      { id: 'b', project: 'p', subject: 'host:b', severity: 'warn', lastSummary: 'Unusual failure', affectedCards: [] },
    ], identities: [], failures: [], review: null, now: 0 });
    expect(causes.map(cause => cause.count)).toEqual([1, 1]);
  });
});
