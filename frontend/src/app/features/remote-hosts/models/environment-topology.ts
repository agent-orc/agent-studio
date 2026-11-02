import type { EnvironmentMap, EnvironmentNode } from './environment-map';

export interface TopologyPoint { id: string; label: string; x: number; y: number; external: boolean; hostId?: string | null; capacity?: string; roles?: string; health?: string }
export interface TopologyEdge {
  id: string; type: string; health: string; label: string;
  x1: number; y1: number; x2: number; y2: number; labelX: number; labelY: number;
}
export interface EnvironmentTopology { points: readonly TopologyPoint[]; edges: readonly TopologyEdge[]; height: number }

export function buildEnvironmentTopology(map: EnvironmentMap): EnvironmentTopology {
  const machinePoints = map.machines.map((machine, index) => ({
    id: machine.id, label: machine.name,
    health: machine.nodes.some(node => node.health === 'offline' || node.health === 'unreachable')
      ? 'error' : machine.nodes.some(node => node.health === 'degraded') ? 'warn' : 'ok',
    hostId: machine.nodes.find(node => node.hostId)?.hostId ?? null,
    capacity: machineCapacity(machine.nodes),
    roles: machine.nodes.map(node => node.role).filter(role => role !== 'control').join(', ') || 'Task Server',
    x: index === 0 ? 450 : 90 + ((index - 1) % 5) * 180,
    y: index === 0 ? 60 : 210 + Math.floor((index - 1) / 5) * 180,
    external: false,
  }));
  const externalIds = [...new Set(map.connections.flatMap(connection => [connection.from, connection.to]))]
    .filter(id => !machinePoints.some(point => point.id === id));
  const points: TopologyPoint[] = [
    ...machinePoints,
    ...externalIds.map((id, index) => {
      const position = machinePoints.length + index;
      return { id, label: id === 'git-remote' ? 'Git remote' : id,
        x: 90 + ((position - 1) % 5) * 180,
        y: 210 + Math.floor((position - 1) / 5) * 180,
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
      label: connection.label || (connection.health === 'warn' ? `${connection.type} · degraded`
        : connection.health === 'unknown' ? `${connection.type} · no recent contact`
        : `${connection.type} · healthy`),
      x1, y1, x2, y2, labelX: (from.x + to.x) / 2,
      labelY: (from.y + to.y) / 2 + (pairIndex === 0 ? 0 : 18) });
  }
  return { points, edges, height: Math.max(...points.map(point => point.y)) + 70 };
}

function machineCapacity(nodes: readonly EnvironmentNode[]): string {
  const slots = nodes.map(node => node.capacity.match(/^(\d+)\/(\d+) slots$/))
    .filter((value): value is RegExpMatchArray => value !== null);
  if (!slots.length) return 'Unknown';
  const reported = `${slots.reduce((sum, slot) => sum + Number(slot[1]), 0)}/${slots.reduce((sum, slot) => sum + Number(slot[2]), 0)} slots`;
  return slots.length === nodes.length ? reported : `${reported} reported`;
}
