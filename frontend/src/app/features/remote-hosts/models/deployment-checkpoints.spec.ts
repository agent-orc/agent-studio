import { describe, expect, it } from 'vitest';
import type { RemoteHost, RemoteHostCapabilityHealth } from './remote-host.model';
import { latestRepositoryProbes, repositoryProofLabel, type RepositoryStatus } from './deployment-checkpoints';
import { hostReadiness } from './host-readiness';

const NOW = Date.parse('2026-10-07T10:00:00Z');

function capability(key: string, isFresh = true): RemoteHostCapabilityHealth {
  return {
    key, category: 'execution', advertisedStatus: 'ready', healthState: 'healthy',
    advertisedAt: '2026-10-07T09:59:00Z', freshUntil: '2026-10-07T10:01:00Z',
    isFresh, consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
  };
}

function host(overrides: Partial<RemoteHost> = {}): RemoteHost {
  return {
    id: 'runner-a', clientId: 'runner-a', name: 'host-a', role: 'remote', serviceRole: 'coding',
    capacityHostId: 'host-a', runnerInstanceId: 'instance-a', status: 'online', os: 'Linux',
    address: null, lastHeartbeatAt: '2026-10-07T09:59:30Z', uptimeLabel: null,
    capabilities: [], cliQuotas: [], stats: null,
    hostAdmission: { hostId: 'host-a', admissionState: 'open' }, roleMaxParallelism: 1,
    capabilityHealth: [capability('executor:coding'), capability('git:push'), capability('provider-auth:codex')],
    ...overrides,
  };
}

describe('deployment checkpoint projections', () => {
  it('separates enrollment, connection and role readiness without awarding project eligibility', () => {
    expect(hostReadiness(host(), NOW).state).toBe('capability-ready');
    expect(hostReadiness(host({ runnerInstanceId: null }), NOW).state).toBe('not-enrolled');
    expect(hostReadiness(host({ status: 'offline' }), NOW).state).toBe('enrolled');
    expect(hostReadiness(host({ roleMaxParallelism: 0 }), NOW)).toMatchObject({
      state: 'connected', reason: 'No role slot budget is reported.',
    });
    expect(hostReadiness(host({ capabilityHealth: [capability('executor:coding', false)] }), NOW))
      .toMatchObject({ state: 'connected', reason: 'executor:coding advertisement is stale.' });
  });

  it('uses only the latest repository receipt per runner and does not treat a disconnected host as proven now', () => {
    const status: RepositoryStatus = {
      registration: { projectId: 'project-a', repositoryUrl: 'https://example.test/a.git',
        integrationRef: 'develop', releaseRef: 'main', deliveryPolicy: 'reviewed-publication' },
      probes: [
        { runnerId: 'runner-a', admitted: true, verdict: 'admitted', detail: null, observedAt: '2026-10-06T09:00:00Z' },
        { runnerId: 'runner-a', admitted: false, verdict: 'push-failed', detail: 'Push denied', observedAt: '2026-10-07T09:00:00Z' },
      ],
    };
    const [latest] = latestRepositoryProbes(status);
    expect(latest.admitted).toBe(false);
    expect(repositoryProofLabel(latest, [host()], NOW)).toContain('Push denied');
    expect(repositoryProofLabel(status.probes[0], [host({ status: 'offline' })], NOW))
      .toContain('host disconnected');
  });
});
