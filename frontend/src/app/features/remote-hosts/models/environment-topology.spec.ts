import { describe, expect, it } from 'vitest';
import { buildEnvironmentTopology } from './environment-topology';
import type { EnvironmentMap } from './environment-map';

describe('buildEnvironmentTopology', () => {
  it('connects machine frames and external Git remote with separate bidirectional lines', () => {
    const map: EnvironmentMap = {
      machines: ['control', 'runner', 'gate', 'website'].map(id => ({ id, name: id, nodes: [] })),
      connections: [
        { id: 'claim', from: 'control', to: 'runner', type: 'outbound claim link', health: 'ok' },
        { id: 'tunnel', from: 'runner', to: 'control', type: 'reverse tunnel', health: 'warn' },
        { id: 'gate', from: 'control', to: 'gate', type: 'SSH gate transport', health: 'unknown' },
        { id: 'git', from: 'runner', to: 'git-remote', type: 'Git remote', health: 'ok' },
      ],
    };
    const topology = buildEnvironmentTopology(map);
    expect(topology.points.map(point => point.label)).toEqual(['control', 'runner', 'gate', 'website', 'Git remote']);
    expect(topology.edges.map(edge => edge.type)).toEqual(map.connections.map(connection => connection.type));
    expect(topology.edges[0]?.labelY).not.toBe(topology.edges[1]?.labelY);
    expect(topology.height).toBeGreaterThan(150);
  });
});
