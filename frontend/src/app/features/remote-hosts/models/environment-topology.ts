import type { EnvironmentMap } from './environment-map';

export interface TopologyPoint { id: string; label: string; x: number; y: number; external: boolean }
export interface TopologyEdge {
  id: string; type: string; health: string;
  x1: number; y1: number; x2: number; y2: number; labelX: number; labelY: number;
}
export interface EnvironmentTopology { points: readonly TopologyPoint[]; edges: readonly TopologyEdge[]; height: number }

export function buildEnvironmentTopology(map: EnvironmentMap): EnvironmentTopology {
  const machinePoints = map.machines.map((machine, index) => ({
    id: machine.id, label: machine.name,
    x: 90 + (index % 5) * 180,
    y: 60 + Math.floor(index / 5) * 180 + (index % 2) * 90,
    external: false,
  }));
  const externalIds = [...new Set(map.connections.flatMap(connection => [connection.from, connection.to]))]
    .filter(id => !machinePoints.some(point => point.id === id));
  const points: TopologyPoint[] = [
    ...machinePoints,
    ...externalIds.map((id, index) => {
      const position = machinePoints.length + index;
      return { id, label: id === 'git-remote' ? 'Git remote' : id,
        x: 90 + (position % 5) * 180,
        y: 60 + Math.floor(position / 5) * 180 + (position % 2) * 90,
        external: true };
    }),
  ];
  const byId = new Map(points.map(point => [point.id, point]));
  const pairs = new Map<string, number>();
  const edges: TopologyEdge[] = [];
  for (const connection of map.connections) {
    const from = byId.get(connection.from);
    const to = byId.get(connection.to);
    if (!from || !to || from.id === to.id) continue;
    const pair = [from.id, to.id].sort().join(':');
    const pairIndex = pairs.get(pair) ?? 0;
    pairs.set(pair, pairIndex + 1);
    const length = Math.hypot(to.x - from.x, to.y - from.y);
    const offset = (pairIndex === 0 ? -14 : 14 + (pairIndex - 1) * 10) * (from.id < to.id ? 1 : -1);
    const shiftX = -(to.y - from.y) / length * offset;
    const shiftY = (to.x - from.x) / length * offset;
    const x1 = from.x + shiftX;
    const y1 = from.y + shiftY;
    const x2 = to.x + shiftX;
    const y2 = to.y + shiftY;
    edges.push({ id: connection.id, type: connection.type, health: connection.health,
      x1, y1, x2, y2, labelX: (from.x + to.x) / 2,
      labelY: (from.y + to.y) / 2 + (pairIndex === 0 ? 0 : 18) });
  }
  return { points, edges, height: 115 + Math.floor(Math.max(points.length - 1, 0) / 5) * 180 + (points.length > 1 ? 90 : 0) };
}
