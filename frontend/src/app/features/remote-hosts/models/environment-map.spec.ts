import { describe, expect, it } from 'vitest';
import { buildEnvironmentMap, type EnvironmentSnapshot } from './environment-map';
import type { PhysicalHostGroup } from './physical-host-group';
import type { RemoteHost } from './remote-host.model';

const snapshot: EnvironmentSnapshot = {
  controlHost: 'TUFI2', installationId: 'studio', mode: 'reverse-ssh-transition',
  routes: [{ runnerId: 'coding', route: 'reverse-ssh' }],
  machines: [
    { id: 'control', name: 'TUFI2', components: [
      { id: 'studio', name: 'Studio', role: 'control', kind: 'service' },
      { id: 'task-server', name: 'Task Server container', role: 'control', kind: 'container' },
    ] },
    { id: 'agent-runner-01', name: 'agent-runner-01', components: [
      { id: 'link', name: 'Tunnel service', role: 'link', kind: 'service' },
      { id: 'promotion', name: 'Promotion service', role: 'integration', kind: 'service' },
      { id: 'integrator', name: 'Integrator', role: 'integration', kind: 'service' },
      { id: 'engine', name: 'Engine', role: 'control', kind: 'container' },
    ] },
    { id: 'kori', name: 'KORI', components: [1, 2, 3].map(index => ({
      id: `review-${index}`, name: `Review container ${index}`, role: 'review', kind: 'container',
    })) },
    { id: 'website-vm', name: 'website-vm', components: [{ id: 'website', name: 'Website service', role: 'control', kind: 'service' }] },
  ],
  gateHost: 'KORI', gateSlots: { busy: 1, total: 3 },
  taskServer: { version: '1.0', commit: 'abc' },
  engine: { state: 'ready', lastContact: '2026-10-16T10:00:00Z', engineId: 'engine-1' },
};

function host(id: string, role: RemoteHost['serviceRole'], machine: string): RemoteHost {
  return {
    id, clientId: id, name: id, role: 'remote', serviceRole: role, capacityHostId: machine,
    address: null, status: 'online', os: 'Linux', lastHeartbeatAt: '2026-10-16T10:00:00Z',
    uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null, liveDataState: 'ready',
    roleMaxParallelism: role === 'review' ? 3 : 2,
  };
}

function group(name: string, ...roles: RemoteHost[]): PhysicalHostGroup {
  return { id: name, name, roles, machine: roles[0] };
}

describe('buildEnvironmentMap', () => {
  const coding = { ...host('coding', 'coding', 'agent-runner-01'), gitPushStatus: 'ready' as const,
    release: { releaseId: 'coding-release', version: '1.0', commit: 'abc' },
    runnerLink: { runnerId: 'coding', kind: 'ssh-reverse' as const, state: 'up' as const,
      since: '2026-10-16T10:00:00Z', lastHeartbeatAt: null, lastProbe: null, lastError: null,
      attempt: 1, nextRetryAt: null, childPid: null, notificationRaisedAt: null } };
  const review = host('review', 'review', 'agent-runner-01');
  const groups = [group('agent-runner-01', coding, review)];

  it('shows each known machine and control, coding, review, gate, integration, and engine nodes', () => {
    const map = buildEnvironmentMap(groups, snapshot, () => 1);
    expect(map.machines.map(machine => machine.name)).toEqual(['TUFI2', 'agent-runner-01', 'KORI', 'website-vm']);
    expect(map.machines.flatMap(machine => machine.nodes.map(node => node.label)))
      .toEqual(expect.arrayContaining(['Studio', 'Task Server', 'Integrator', 'Engine', 'coding', 'review', 'Gate slots',
        'Review container 1', 'Review container 2', 'Review container 3', 'Website service']));
    expect(map.machines[1].nodes[0]).toMatchObject({ role: 'coding', capacity: '1/2 slots',
      drift: 'same as Task Server', repositoryAccess: 'writes' });
    expect(map.machines[1].nodes[1]).toMatchObject({ role: 'review', capacity: '1/3 slots', repositoryAccess: 'unknown' });
    expect(map.machines[2].nodes[0].capacity).toBe('1/3 slots');
  });

  it('renders each reported connection type with source health', () => {
    const connections = buildEnvironmentMap(groups, snapshot, () => 0).connections;
    expect(connections.map(connection => connection.type)).toEqual(expect.arrayContaining([
      'outbound claim link', 'reverse tunnel', 'SSH gate transport', 'Git remote',
    ]));
    expect(connections.find(connection => connection.type === 'reverse tunnel')?.health).toBe('ok');
    expect(connections.find(connection => connection.type === 'SSH gate transport')?.health).toBe('unknown');
  });

  it('does not invent gate or route facts when no installation snapshot exists', () => {
    const map = buildEnvironmentMap(groups, null, () => 0);
    expect(map.machines.some(machine => machine.name === 'KORI')).toBe(false);
    expect(map.connections.some(connection => connection.type === 'SSH gate transport')).toBe(false);
    expect(map.machines.flatMap(machine => machine.nodes).find(node => node.id === 'engine')?.health).toBe('Unknown');
    expect(map.machines.find(machine => machine.id === 'unplaced')?.nodes.map(node => node.label))
      .toEqual(['Studio', 'Integrator', 'Engine']);
  });
});
