export interface StructureNode {
  id: string;
  kind: string;
  label: string;
  parentId: string | null;
  unitId: string | null;
  environmentId: string | null;
  project: string | null;
  provenance: 'declared' | 'observed';
  sourceRef: string;
  role?: string | null;
  observation?: StructureObservation | null;
  ownerLabel?: string | null;
  otherProject?: boolean;
  systemBoundaryId?: string | null;
  /** Kind line detail, such as the deploy target kind behind a placement. */
  detail?: string | null;
  /** Declared replica count of one deployment. */
  replicas?: number | null;
  /** Managed services: observed through a control-plane health check, or declared only. */
  probeObservation?: 'probed' | 'not-observed' | null;
  /** Health check kind that observes a managed service (http, tcp, tls). */
  probe?: string | null;
  /** Health check status: up, degraded, down, unknown or invalid. */
  status?: string | null;
  /** Performance, network, capability and data facts (AGT-3954). Absent when nothing reports them. */
  facts?: StructureNodeFacts | null;
}

/** Facts only; the shape is documented in docs/system/contracts/operations-structure.md. */
export interface StructureNodeFacts {
  hostId?: string | null;
  machine?: { kind: string; virtualization?: string | null; containerRuntime?: string | null;
    containerName?: string | null; reportedParentId?: string | null; source: string } | null;
  roles?: { runnerId: string; role: string; status: string; version?: string | null; lastSeenAt: string;
    busySlots?: number | null; freeSlots?: number | null; runningCards: string[] }[] | null;
  cpuCores?: number | null;
  cpuPercent?: number | null;
  load1?: number | null;
  load5?: number | null;
  load15?: number | null;
  memoryUsedBytes?: number | null;
  memoryTotalBytes?: number | null;
  mounts?: { purpose: string; mount: string; usedBytes: number; freeBytes: number; totalBytes: number }[] | null;
  networkReceiveBytesPerSecond?: number | null;
  networkTransmitBytesPerSecond?: number | null;
  busySlots?: number | null;
  freeSlots?: number | null;
  runningCards?: string[] | null;
  version?: string | null;
  stableVersion?: string | null;
  releaseState?: string | null;
  releaseDetail?: string | null;
  pendingHostUpdate?: string | null;
  lastSeenAt?: string | null;
  telemetryObservedAt?: string | null;
  admissionState?: string | null;
  tools?: { key: string; name: string; status: string; version?: string | null; targetVersion?: string | null; behind?: boolean }[] | null;
  requirements?: { key: string; state: string; detail?: string | null; waitingCards?: string[] | null }[] | null;
  linksDown?: string[] | null;
  heldCards?: string[] | null;
  dataLocations?: StructureDataLocation[] | null;
  imageDigest?: string | null;
  containerState?: string | null;
}

export interface StructureDataLocation {
  id: string; role: string; label: string; sizeBytes?: number | null;
  backupCovered: boolean; gitRemoteCovered?: boolean | null; measuredAt?: string | null; detail?: string | null;
}

export interface StructureObservation {
  /** `observed` marks a link only the reports prove; nodes that nothing declares are `undeclared`. */
  difference: 'matched' | 'declared-only' | 'undeclared' | 'observed' | 'differs' | 'stale' | 'unreachable-by-design';
  health: 'up' | 'down' | 'degraded' | 'stale' | 'not observed' | string;
  observedAt?: string | null;
  version?: string | null;
  expectedVersion?: string | null;
  behind?: boolean;
  cpuPercent?: number | null;
  memoryUsedBytes?: number | null;
  memoryTotalBytes?: number | null;
  activeGates?: number | null;
  detail?: string | null;
}

export interface StructureLink {
  /** Stable link id (`link:{from}|{to}|{kind}`); deltas address links by it. */
  id?: string | null;
  from: string;
  to: string;
  kind: string;
  label: string;
  sourceRef: string;
  observation?: StructureObservation | null;
  facts?: { state: string; latencyMs?: number | null; probedAt?: string | null; transport?: string | null } | null;
}

export interface StructureFinding { code: string; subject: string; message: string; remediation: string; }
export interface StructureData {
  nodes: StructureNode[];
  links: StructureLink[];
  /** Snapshot revision; a delta applies only to the revision it names. */
  revision?: number;
  generatedAt?: string | null;
  findings?: StructureFinding[] | null;
}

/** `structureChanged` hub push: what changed between two workspace snapshot revisions. */
export interface StructureDelta {
  fromRevision: number;
  revision: number;
  generatedAt: string;
  upsertNodes: StructureNode[];
  removeNodes: string[];
  upsertLinks: StructureLink[];
  removeLinks: string[];
  findings?: StructureFinding[] | null;
}

/**
 * Applies a delta to the snapshot it was computed from. Returns null when the
 * delta does not start at this snapshot's revision; the caller then reads the
 * whole snapshot again. Order is kept: changed entries stay in place, new ones
 * append, so the layout does not reshuffle on a fact update.
 */
export function applyStructureDelta(data: StructureData, delta: StructureDelta): StructureData | null {
  if (data.revision === undefined || data.revision !== delta.fromRevision) return null;
  const merge = <T>(items: T[], upserts: T[], removed: string[], key: (item: T) => string): T[] => {
    const gone = new Set(removed);
    const changed = new Map(upserts.map(item => [key(item), item]));
    const kept = items.filter(item => !gone.has(key(item))).map(item => {
      const next = changed.get(key(item));
      if (next) changed.delete(key(item));
      return next ?? item;
    });
    return [...kept, ...changed.values()];
  };
  return {
    ...data,
    nodes: merge(data.nodes, delta.upsertNodes, delta.removeNodes, node => node.id),
    links: merge(data.links, delta.upsertLinks, delta.removeLinks, link => link.id ?? `${link.from}|${link.to}|${link.kind}`),
    revision: delta.revision,
    generatedAt: delta.generatedAt,
    findings: delta.findings ?? null,
  };
}
export interface StructureBox extends StructureNode {
  children: StructureBox[];
  collapsed: boolean;
  summary: Record<string, number>;
  replicas: number;
  /** Ids merged into this box by replica grouping, its own id first. */
  members: string[];
  /** Distinct placements of the merged members (container view). */
  placements: number;
}

const PLACEMENT = new Set(['physical', 'cloud-server', 'vm', 'cluster', 'namespace', 'host', 'server', 'managed-runtime']);
const LANE = new Set(['location', 'region']);
const GROUP = new Set(['boundary', 'system', 'environment', 'group', ...LANE, ...PLACEMENT]);
const NESTED = new Set(['vm', 'cluster', 'namespace']);
export const isPlacement = (node: Pick<StructureNode, 'id' | 'kind'>): boolean =>
  PLACEMENT.has(node.kind) || node.id.startsWith('machine:');
export const isGroup = (node: Pick<StructureNode, 'id' | 'kind'>): boolean => GROUP.has(node.kind) || isPlacement(node);
/** The container view groups instances by function; managed services share one group. */
const functionOf = (node: StructureNode): string => node.role || (node.kind === 'managed' ? 'Managed services' : 'Other');

/** One pass over the declaration graph. Unknown placement kinds remain boxes. */
export function projectStructure(data: StructureData, view: 'host' | 'container', level: 1 | 2 | 3,
  toggles: Readonly<Record<string, boolean>>): StructureBox[] {
  const nodes = new Map<string, StructureBox>();
  const boundary = data.nodes.find(node => node.kind === 'boundary');
  const box = (node: StructureNode, parentId: string | null): StructureBox => ({ ...node, parentId, children: [],
    collapsed: false, summary: {}, replicas: node.replicas ?? 1, members: [node.id], placements: 1 });
  if (view === 'container' && boundary) {
    for (const role of new Set(data.nodes.filter(node => !isGroup(node) && node.kind !== 'external').map(functionOf))) {
      const id = `function:${role}`;
      nodes.set(id, { ...box(boundary, boundary.id), id, members: [id], label: role, kind: 'group', detail: null });
    }
  }
  for (const node of data.nodes) {
    if (view === 'container' && (isPlacement(node) || LANE.has(node.kind))) continue;
    const parentId = view === 'container' && !isGroup(node) && node.kind !== 'external'
      ? `function:${functionOf(node)}` : node.parentId;
    nodes.set(node.id, box(node, parentId));
  }
  if (view === 'host') {
    const children = new Map<string, StructureNode[]>();
    for (const node of data.nodes) if (node.parentId) {
      const siblings = children.get(node.parentId) ?? [];
      siblings.push(node);
      children.set(node.parentId, siblings);
    }
    const members = (id: string): StructureNode[] => (children.get(id) ?? []).flatMap(child =>
      child.project && !isGroup(child) ? [child] : members(child.id));
    for (const node of data.nodes.filter(node => node.parentId === boundary?.id &&
      (LANE.has(node.kind) || isPlacement(node)))) {
      const owned = members(node.id);
      const systemId = owned[0]?.systemBoundaryId;
      if (systemId && owned.every(member => member.systemBoundaryId === systemId) && nodes.has(systemId))
        nodes.get(node.id)!.parentId = systemId;
    }
  }
  const roots: StructureBox[] = [];
  for (const node of nodes.values()) {
    const parent = node.parentId ? nodes.get(node.parentId) : null;
    if (parent) parent.children.push(node);
    else if (view !== 'container' || node.kind === 'boundary' || node.kind === 'external') roots.push(node);
  }
  if (view === 'container') for (const root of roots)
    root.children = root.children.filter(child => child.kind !== 'environment' || child.children.length > 0);
  // Replica grouping is local to one parent; the retained first node keeps a stable id.
  // Numbered siblings (Review host 1, 2, 3) merge in both views. In the container
  // view the same unit in one environment merges across its placements.
  const source = new Map(data.nodes.map(node => [node.id, node]));
  const groupReplicas = (parent: StructureBox): void => {
    for (const child of parent.children) groupReplicas(child);
    if (level === 3 || toggles[parent.id] === false) return;
    const seen = new Map<string, { first: StructureBox; placements: Set<string | null> }>();
    parent.children = parent.children.filter(child => {
      if (isGroup(child) || child.kind === 'external') return true;
      const numbered = child.kind === 'container' ? /^(.*?)(?:\s+[1-9][0-9]*)$/.exec(child.label) : null;
      const key = view === 'container' && child.unitId
        ? `unit:${child.project}:${child.environmentId}:${child.unitId}:${child.kind}`
        : numbered ? `${child.kind}:${numbered[1]}` : null;
      if (!key) return true;
      const placement = source.get(child.id)?.parentId ?? null;
      const group = seen.get(key);
      if (!group) {
        seen.set(key, { first: child, placements: new Set([placement]) });
        if (numbered) child.label = numbered[1];
        return true;
      }
      group.first.replicas += child.replicas;
      group.first.members.push(...child.members);
      group.placements.add(placement);
      group.first.placements = group.placements.size;
      return false;
    });
  };
  for (const root of roots) groupReplicas(root);
  const decorate = (box: StructureBox): Record<string, number> => {
    const counts: Record<string, number> = {};
    for (const child of box.children) {
      const nested = decorate(child);
      if (isGroup(child)) {
        if (NESTED.has(child.kind)) counts[child.kind] = (counts[child.kind] || 0) + 1;
        for (const [kind, count] of Object.entries(nested)) counts[kind] = (counts[kind] || 0) + count;
      } else counts[child.kind] = (counts[child.kind] || 0) + child.replicas;
    }
    box.summary = counts;
    // Level 1 shows boundaries, environments and lanes with machines, clusters and
    // function groups closed; level 2 opens machines but keeps VMs and namespaces closed.
    box.collapsed = toggles[box.id] ?? (level === 1 && (isPlacement(box) || box.kind === 'group') ||
      level === 2 && (box.kind === 'vm' || box.kind === 'namespace'));
    return counts;
  };
  for (const root of roots) decorate(root);
  return roots;
}

export function visibleBoxes(roots: readonly StructureBox[]): StructureBox[] {
  const out: StructureBox[] = [];
  const visit = (box: StructureBox): void => {
    out.push(box);
    if (!box.collapsed) box.children.forEach(visit);
  };
  roots.forEach(visit);
  return out;
}

export function summaryText(box: StructureBox): string {
  return Object.entries(box.summary).map(([kind, count]) =>
    `${count} ${kind}${count === 1 ? '' : kind.endsWith('s') ? 'es' : 's'}`).join(' · ');
}

const KIND_LABEL: Record<string, string> = {
  boundary: 'System boundary', system: 'System boundary', environment: 'Environment', location: 'Location',
  region: 'Region', physical: 'Physical machine', 'cloud-server': 'Cloud server', server: 'Server', host: 'Host',
  vm: 'Virtual machine', cluster: 'Cluster', namespace: 'Namespace', 'managed-runtime': 'Managed runtime',
  group: 'Function', container: 'Container', process: 'Process', managed: 'Managed service',
  external: 'External service', registry: 'Registry', unit: 'Deployable unit',
};

/** Kind line: the kind in words plus the declared detail (Cluster · Kubernetes). */
export function kindText(box: Pick<StructureNode, 'kind' | 'detail'>): string {
  const kind = KIND_LABEL[box.kind] ?? box.kind;
  return box.detail ? `${kind} · ${box.detail}` : kind;
}

const PROBE_LABEL: Record<string, string> = { tls: 'TLS', tcp: 'TCP', http: 'HTTP' };

/** Managed services are unreachable by design: probed from the control plane, or not observed. */
export function observationText(box: Pick<StructureNode, 'probeObservation' | 'probe'>): string | null {
  if (box.probeObservation === 'probed') return `probed via ${PROBE_LABEL[box.probe ?? ''] ?? box.probe ?? 'health check'}`;
  return box.probeObservation === 'not-observed' ? 'not observed' : null;
}

export function nearestVisible(id: string, visible: ReadonlySet<string>, source: ReadonlyMap<string, StructureNode>): string | null {
  let node = source.get(id);
  while (node && !visible.has(node.id)) node = node.parentId ? source.get(node.parentId) : undefined;
  return node?.id ?? null;
}

export function observationWord(observation?: StructureObservation | null): string {
  if (!observation) return 'Not observed';
  if (observation.difference === 'undeclared') return 'Not declared';
  if (observation.difference === 'unreachable-by-design') return 'Unreachable by design';
  if (observation.difference === 'stale') return 'Stale';
  if (observation.health === 'down') return 'Down';
  if (observation.health === 'degraded') return 'Degraded';
  if (observation.difference === 'declared-only' || observation.health === 'not observed') return 'Not observed';
  if (observation.difference === 'differs') return 'Differs';
  return observation.health === 'up' || observation.health === 'healthy' ? 'Up' : 'Not observed';
}

export function observationGlyph(observation?: StructureObservation | null): string {
  const word = observationWord(observation);
  return word === 'Up' ? '●' : word === 'Down' ? '■' : word === 'Degraded' || word === 'Differs' ? '▲' : '○';
}

const gib = (bytes: number): string => `${(bytes / 1024 ** 3).toFixed(1)} GiB`;
const rate = (bytes: number): string => bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MiB/s`
  : `${(bytes / 1024).toFixed(0)} KiB/s`;
const MACHINE_WORD: Record<string, string> = { container: 'Container', vm: 'Virtual machine', physical: 'Physical machine', unknown: 'Machine' };

/** One label and value per fact the node carries, in reading order. Facts only; nothing is ranked. */
export function factRows(facts?: StructureNodeFacts | null): { label: string; value: string }[] {
  if (!facts) return [];
  const rows: { label: string; value: string }[] = [];
  // Labels are the row identity, so a repeated one (two mounts with one purpose) gets a suffix.
  const add = (label: string, value: string | null | undefined): void => {
    if (!value) return;
    let unique = label;
    for (let n = 2; rows.some(row => row.label === unique); n++) unique = `${label} ${n}`;
    rows.push({ label: unique, value });
  };
  const machine = facts.machine;
  add('Runs as', machine ? [MACHINE_WORD[machine.kind] ?? machine.kind, machine.containerRuntime,
    machine.containerName, machine.virtualization && machine.virtualization !== 'none' ? `on ${machine.virtualization}` : null,
    machine.reportedParentId ? `in ${machine.reportedParentId}` : null].filter(Boolean).join(' · ') : null);
  add('CPU', facts.cpuCores != null || facts.cpuPercent != null
    ? [facts.cpuCores != null ? `${facts.cpuCores} cores` : null,
      facts.cpuPercent != null ? `${facts.cpuPercent.toFixed(0)}% busy` : null].filter(Boolean).join(' · ') : null);
  add('Load', facts.load1 != null ? [facts.load1, facts.load5, facts.load15].map(v => v?.toFixed(2) ?? '–').join(' / ') : null);
  add('Memory', facts.memoryTotalBytes != null
    ? `${facts.memoryUsedBytes != null ? gib(facts.memoryUsedBytes) : '–'} of ${gib(facts.memoryTotalBytes)}` : null);
  for (const mount of facts.mounts ?? [])
    add(`Disk ${mount.purpose}`, mount.totalBytes > 0
      ? `${Math.round(mount.usedBytes * 100 / mount.totalBytes)}% used · ${gib(mount.freeBytes)} free` : null);
  add('Network', facts.networkReceiveBytesPerSecond != null || facts.networkTransmitBytesPerSecond != null
    ? `↓ ${rate(facts.networkReceiveBytesPerSecond ?? 0)} · ↑ ${rate(facts.networkTransmitBytesPerSecond ?? 0)}` : null);
  add('Slots', facts.busySlots != null || facts.freeSlots != null
    ? `${facts.busySlots ?? 0} busy · ${facts.freeSlots ?? '–'} free` : null);
  add('Running', facts.runningCards?.length ? facts.runningCards.join(', ') : null);
  add('Roles', facts.roles?.length ? facts.roles.map(role => `${role.role} ${role.status}`).join(', ') : null);
  add('Version', facts.version ? [facts.version, facts.stableVersion ? `stable ${facts.stableVersion}` : null,
    facts.releaseState && facts.releaseState !== 'current' ? facts.releaseState : null].filter(Boolean).join(' · ') : null);
  add('Host update', facts.pendingHostUpdate);
  add('Last seen', facts.lastSeenAt ? new Date(facts.lastSeenAt).toLocaleString('en-US') : null);
  add('Toolchains', facts.tools?.length ? facts.tools.map(tool =>
    `${tool.name} ${tool.version ?? tool.status}${tool.behind ? ' (behind)' : ''}`).join(', ') : null);
  add('Requirements', facts.requirements?.length ? facts.requirements.map(item => `${item.key} ${item.state}`).join(', ') : null);
  add('Links down', facts.linksDown?.length ? String(facts.linksDown.length) : null);
  add('Held cards', facts.heldCards?.length ? facts.heldCards.join(', ') : null);
  for (const location of facts.dataLocations ?? [])
    add(location.label, [location.sizeBytes != null ? gib(location.sizeBytes) : 'size not measured',
      location.backupCovered ? 'in backups' : 'not in backups',
      location.gitRemoteCovered === true ? 'Git remote' : location.gitRemoteCovered === false ? 'no Git remote' : null]
      .filter(Boolean).join(' · '));
  add('Container', facts.containerState ? [facts.containerState, facts.imageDigest?.slice(0, 19)].filter(Boolean).join(' · ') : null);
  return rows;
}
