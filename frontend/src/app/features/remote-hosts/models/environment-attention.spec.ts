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
  it('keeps unrecognised causes separate', () => {
    const causes = buildEnvironmentAttention({ hosts: [], stalls: [
      { id: 'a', project: 'p', subject: 'host:a', severity: 'warn', lastSummary: 'Unusual failure', affectedCards: [] },
      { id: 'b', project: 'p', subject: 'host:b', severity: 'warn', lastSummary: 'Unusual failure', affectedCards: [] },
    ], identities: [], failures: [], review: null, now: 0 });
    expect(causes.map(cause => cause.count)).toEqual([1, 1]);
  });
});
