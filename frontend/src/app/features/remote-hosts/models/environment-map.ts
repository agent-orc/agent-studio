import type { PhysicalHostGroup } from './physical-host-group';
import type { RemoteHost } from './remote-host.model';
import type { CredentialInventory } from '../../credentials/credential-inventory.service';

export interface EnvironmentSnapshot {
  controlHost: string;
  installationId: string | null;
  mode: string | null;
  routes: readonly { runnerId: string; route: string }[] | null;
  machines?: readonly { id: string; name: string; components: readonly {
    id: string; name: string; role: string; kind: string;
  }[] }[] | null;
  gateHost: string | null;
  gateSlots: { busy: number; total: number } | null;
  taskServer?: { version: string; commit: string | null } | null;
  engine: { state: string; lastContact: string | null; engineId: string | null };
}

export interface EnvironmentNode {
  id: string;
  label: string;
  role: 'control' | 'coding' | 'review' | 'gate' | 'integration' | 'link';
  health: string;
  capacity: string;
  load?: string;
  version: string;
  drift: string;
  lastContact: string;
  repositoryAccess: 'writes' | 'reads only' | 'no access' | 'unknown';
  hostId: string | null;
  kind?: string;
}

export interface EnvironmentMachine {
  id: string;
  name: string;
  nodes: readonly EnvironmentNode[];
}

export interface EnvironmentConnection {
  id: string;
  from: string;
  to: string;
  type: 'outbound claim link' | 'reverse tunnel' | 'SSH gate transport' | 'Git remote';
  health: 'ok' | 'warn' | 'unknown';
}

export interface EnvironmentMap { machines: readonly EnvironmentMachine[]; connections: readonly EnvironmentConnection[] }

const unknown = 'Unknown';

function access(aliases: readonly string[], inventory: CredentialInventory | null,
  role?: EnvironmentNode['role'], ambiguousHostId?: string): EnvironmentNode['repositoryAccess'] {
  if (!inventory) return 'unknown';
  const ids = new Set(aliases.filter(Boolean).map(id => id.toLowerCase()));
  const matched = inventory.items.filter(item => item.category === 'repository' && ids.has(item.hostId.toLowerCase()));
  const ambiguous = matched.some(item => item.state !== 'missing'
    && item.hostId.toLowerCase() === ambiguousHostId?.toLowerCase()
    && !item.scopes.some(scope => /^(coding|review|gate|integration|control):/.test(scope)));
  const items = matched.filter(item => {
    const boundRoles = item.scopes.filter(scope => /^(coding|review|gate|integration|control):/.test(scope));
    if (boundRoles.length) return !role || boundRoles.some(scope => scope.startsWith(`${role}:`));
    return item.hostId.toLowerCase() !== ambiguousHostId?.toLowerCase();
  });
  const present = items.filter(item => item.state !== 'missing');
  if (present.some(item => item.permission === 'write')) return 'writes';
  if (ambiguous) return 'unknown';
  if (present.some(item => item.permission === 'read')) return 'reads only';
  return present.length ? 'unknown' : 'no access';
}

function runnerNode(host: RemoteHost, slots: number, server: EnvironmentSnapshot['taskServer'],
  inventory: CredentialInventory | null, sharedHost: boolean): EnvironmentNode {
  const role = host.serviceRole === 'review' ? 'review' : 'coding';
  return {
    id: host.id, label: host.name, role,
    health: host.liveDataState === 'ready' && host.lastHeartbeatAt ? host.status : unknown,
    capacity: `${slots}/${host.roleMaxParallelism ?? host.effectiveMaxParallelism ?? host.runtimeCapacity?.maxParallelism ?? '?'} slots`,
    load: host.stats ? `${host.stats.cpuLoadPct}% CPU` : unknown,
    version: host.release?.version ?? unknown,
    drift: host.release?.version && server?.version
      ? host.release.version === server.version && (!host.release.commit || !server.commit || host.release.commit === server.commit)
        ? 'same as Task Server' : 'differs from Task Server'
      : unknown,
    lastContact: host.lastHeartbeatAt ?? unknown,
    repositoryAccess: access([host.id, host.clientId, host.capacityHostId ?? '', host.name], inventory,
      role, sharedHost ? host.capacityHostId ?? undefined : undefined), hostId: host.id,
  };
}

export function buildEnvironmentMap(
  groups: readonly PhysicalHostGroup[],
  snapshot: EnvironmentSnapshot | null,
  slotsFor: (host: RemoteHost) => number,
  inventory: CredentialInventory | null = null,
): EnvironmentMap {
  const controlId = 'control';
  const controlName = snapshot?.controlHost || 'Control host (unknown)';
  const controlNodes: EnvironmentNode[] = [
    { id: 'task-server', label: 'Task Server', role: 'control', health: snapshot ? 'reachable' : unknown,
      capacity: unknown, version: unknown, drift: unknown, lastContact: unknown,
      repositoryAccess: access(['task-server'], inventory), hostId: null },
  ];
  const machines: EnvironmentMachine[] = [{ id: controlId, name: controlName, nodes: controlNodes }];
  const connections: EnvironmentConnection[] = [];
  for (const group of groups) {
    // Local process identities live inside the control frame.
    if (group.machine.role === 'local') {
      controlNodes.push(...group.roles.map(host => runnerNode(host, slotsFor(host), snapshot?.taskServer,
        inventory, group.roles.length > 1)));
      continue;
    }
    machines.push({ id: group.id, name: group.name,
      nodes: group.roles.map(host => runnerNode(host, slotsFor(host), snapshot?.taskServer,
        inventory, group.roles.length > 1)) });
    const route = snapshot?.routes?.find(item => group.roles.some(host => host.clientId === item.runnerId));
    const link = group.roles.find(host => host.runnerLink)?.runnerLink;
    const claim = group.roles.map(host => host.taskServerConnection).find(connection => connection?.observedAt);
    if (route || claim) connections.push({ id: `claim:${group.id}`, from: controlId, to: group.id,
      type: 'outbound claim link', health: claim?.status === 'reachable' ? 'ok'
        : claim?.status === 'unreachable' ? 'warn' : 'unknown' });
    if (route?.route === 'reverse-ssh' || link?.kind === 'ssh-reverse')
      connections.push({ id: `tunnel:${group.id}`, from: group.id, to: controlId,
        type: 'reverse tunnel', health: link?.state === 'up' ? 'ok' : link ? 'warn' : 'unknown' });
    if (group.roles.some(host => host.gitPushStatus))
      connections.push({ id: `git:${group.id}`, from: group.id, to: 'git-remote',
        type: 'Git remote', health: group.roles.some(host => host.gitPushStatus === 'ready'
          || host.gitPushStatus === 'ready-no-workflow-scope') ? 'ok' : 'warn' });
  }
  if (snapshot?.gateHost) {
    const existing = machines.find(machine => machine.name === snapshot.gateHost);
    const gateNode: EnvironmentNode = { id: 'gate', label: 'Gate slots', role: 'gate', health: unknown,
      capacity: snapshot.gateSlots ? `${snapshot.gateSlots.busy}/${snapshot.gateSlots.total} slots` : unknown,
      version: unknown, drift: unknown, lastContact: unknown,
      repositoryAccess: access([snapshot.gateHost], inventory), hostId: null };
    if (existing) machines.splice(machines.indexOf(existing), 1, { ...existing, nodes: [...existing.nodes, gateNode] });
    else machines.push({ id: `gate:${snapshot.gateHost}`, name: snapshot.gateHost, nodes: [gateNode] });
    connections.push({ id: 'gate-transport', from: controlId, to: existing?.id ?? `gate:${snapshot.gateHost}`,
      type: 'SSH gate transport', health: 'unknown' });
  }
  for (const declared of snapshot?.machines ?? []) {
    const nodes: EnvironmentNode[] = declared.components.map(component => ({
      id: `manifest:${declared.id}:${component.id}`, label: component.name,
      role: knownRole(component.role), kind: component.kind,
      health: component.id === 'engine' ? snapshot?.engine.state ?? unknown : unknown,
      capacity: unknown, version: unknown, drift: unknown,
      lastContact: component.id === 'engine' ? snapshot?.engine.lastContact ?? unknown : unknown,
      repositoryAccess: access(component.id === 'integrator' ? ['control', component.id] : [component.id], inventory), hostId: null,
    }));
    const existing = machines.find(machine => machine.id === declared.id || machine.name === declared.name);
    if (existing) machines.splice(machines.indexOf(existing), 1, { ...existing, nodes: [...existing.nodes, ...nodes] });
    else machines.push({ id: declared.id, name: declared.name, nodes });
  }
  const placed = new Set((snapshot?.machines ?? []).flatMap(machine => machine.components.map(component => component.id)));
  const unplaced = ([
    { id: 'studio', label: 'Studio', role: 'control', health: unknown, capacity: unknown,
      version: unknown, drift: unknown, lastContact: unknown,
      repositoryAccess: access(['studio'], inventory), hostId: null },
    { id: 'integrator', label: 'Integrator', role: 'integration', health: unknown, capacity: unknown,
      version: unknown, drift: unknown, lastContact: unknown,
      repositoryAccess: access(['control', 'integrator'], inventory), hostId: null },
    { id: 'engine', label: 'Engine', role: 'control', health: snapshot?.engine.state ?? unknown,
      capacity: unknown, version: unknown, drift: unknown,
      lastContact: snapshot?.engine.lastContact ?? unknown,
      repositoryAccess: access(['engine'], inventory), hostId: null },
  ] satisfies EnvironmentNode[]).filter(node => !placed.has(node.id));
  if (unplaced.length) machines.push({ id: 'unplaced', name: 'Location unknown', nodes: unplaced });
  return { machines, connections };
}

function knownRole(role: string): EnvironmentNode['role'] {
  return role === 'coding' || role === 'review' || role === 'gate' || role === 'integration' || role === 'link'
    ? role : 'control';
}
