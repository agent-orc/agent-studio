import { describe, expect, it } from 'vitest';
import { hostReadiness } from './host-readiness';
import { groupNotEnrolled } from './host-summary';
import { groupPhysicalHosts } from './physical-host-group';
import { projectRunHost } from './run-host';
import type { RunHostStatus } from '../../../models/run-host.model';

const now = Date.parse('2026-11-16T10:01:00Z');
const runHost = (state: RunHostStatus['state'], lastSeenAt: string | null): RunHostStatus => ({
  id: 'runner_deploy', name: 'run-host-1', state, projects: ['PROJ-002'],
  lastSeenAt, lastClaimAt: lastSeenAt, lastReportAt: null,
});

describe('run host rows (F4)', () => {
  it('is online after a recent claim and is not tagged "Not enrolled"', () => {
    const host = projectRunHost(undefined, runHost('reachable', '2026-11-16T10:00:30Z'));
    expect(host).toMatchObject({ serviceRole: 'deploy', status: 'online', lastClaimAt: '2026-11-16T10:00:30Z' });
    expect(hostReadiness(host, now).state).toBe('capability-ready');
    expect(groupNotEnrolled(groupPhysicalHosts([host], false)[0], now)).toBe(false);
  });

  it('is offline when it stopped claiming, and names the service to check', () => {
    const host = projectRunHost(undefined, runHost('unreachable', '2026-11-16T09:00:00Z'));
    expect(host.status).toBe('offline');
    expect(hostReadiness(host, now)).toMatchObject({ state: 'enrolled', action: expect.stringContaining('agent-runner-deploy') });
  });

  it('keeps an operator drain on the row it updates', () => {
    const current = { ...projectRunHost(undefined, runHost('reachable', '2026-11-16T10:00:30Z')), status: 'draining' as const };
    expect(projectRunHost(current, runHost('reachable', '2026-11-16T10:00:50Z')).status).toBe('draining');
  });
});
