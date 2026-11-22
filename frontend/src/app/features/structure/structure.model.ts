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
  facts?: StructureNodeFacts | null;
}

/** Facts of one node (AGT-3954). Facts only: ranking and wording belong to assessments. */
export interface StructureNodeFacts {
  hostId?: string | null;
  /** Kind the host detected for the place it runs in: container, vm, physical or unknown. */
  machineKind?: string | null;
  virtualization?: string | null;
  containerName?: string | null;
  roles?: string[] | null;
  lastSeenAt?: string | null;
  performance?: StructurePerformanceFacts | null;
  capacity?: StructureCapacityFacts | null;
  release?: StructureReleaseFacts | null;
  dataLocations?: StructureDataLocation[] | null;
  warnings?: StructureWarningFacts | null;
}
export interface StructurePerformanceFacts {
  observedAt: string;
  cpuPercent?: number | null;
  cpuCores?: number | null;
  load1?: number | null;
  load5?: number | null;
  load15?: number | null;
  memoryUsedBytes?: number | null;
  memoryTotalBytes?: number | null;
  disks?: StructureDiskFact[] | null;
  networkReceiveBytesPerSecond?: number | null;
  networkTransmitBytesPerSecond?: number | null;
}
export interface StructureDiskFact { mount: string; purpose: string; usedBytes: number; freeBytes: number; totalBytes: number; }
export interface StructureCapacityFacts { busySlots: number; freeSlots?: number | null; totalSlots?: number | null; runningCards: string[]; }
export interface StructureReleaseFacts {
  version?: string | null; stableVersion?: string | null;
  state: 'current' | 'behind' | 'differs' | 'unknown' | string;
  behind: boolean; pendingInstall: boolean; detail?: string | null;
}
/** A location that holds state. The path stays on the host; `sourceRef` names the setting that selects it. */
export interface StructureDataLocation {
  id: string; role: string; label: string; sourceRef: string; exists: boolean;
  sizeBytes?: number | null; sizeComplete: boolean; measuredAt?: string | null;
  backupCovered: boolean; gitRemoteCovered: boolean; coverage: 'backup' | 'git-remote' | 'both' | 'none';
}
export interface StructureToolFact { name: string; kind: 'cli' | 'toolchain' | string; version?: string | null; targetVersion?: string | null; state: 'ok' | 'behind' | 'missing' | 'unhealthy' | string; }
export interface StructureBlockedCard { key: string; state: 'parked' | 'refused' | string; reason: string; }
export interface StructureWarningFacts {
  tools: StructureToolFact[]; missingRequired: string[]; behindRequired: string[];
  releaseBehind: boolean; pendingHostInstall: boolean; linksDown: string[]; blockedCards: StructureBlockedCard[];
}
export interface StructureLinkFacts { state: string; latencyMs?: number | null; probedAt?: string | null; transport?: string | null; }

export interface StructureObservation {
  difference: 'matched' | 'declared-only' | 'undeclared' | 'differs' | 'stale' | 'unreachable-by-design';
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
  from: string;
  to: string;
  kind: string;
  label: string;
  sourceRef: string;
  observation?: StructureObservation | null;
  /** Stable across reports and restarts: `link:{from}>{to}:{kind}`. */
  id?: string | null;
  facts?: StructureLinkFacts | null;
}

export interface StructureData {
  nodes: StructureNode[];
  links: StructureLink[];
  /** Increases whenever a node or link of the scope changes; also the entity tag of the read. */
  revision?: number;
  generatedAt?: string | null;
  schemaVersion?: number;
}
/** Changes of one scope since `baseRevision`, pushed on the Studio hub as `structureDelta`. */
export interface StructureDelta {
  scope: string;
  environment: string | null;
  baseRevision: number;
  revision: number;
  generatedAt: string;
  nodes: StructureNode[];
  removedNodes: string[];
  links: StructureLink[];
  removedLinks: string[];
}
const linkKey = (link: StructureLink): string => link.id ?? `link:${link.from}>${link.to}:${link.kind}`;

/** True when the delta adds or removes a node or link; fact updates keep the same ids. */
export function isStructuralDelta(data: StructureData, delta: StructureDelta): boolean {
  if (delta.removedNodes.length || delta.removedLinks.length) return true;
  const nodes = new Set(data.nodes.map(node => node.id));
  const links = new Set(data.links.map(linkKey));
  return delta.nodes.some(node => !nodes.has(node.id)) || delta.links.some(link => !links.has(linkKey(link)));
}

/** Applies a delta whose base is the data's revision. Kept nodes keep their position; new ones follow. */
export function applyStructureDelta(data: StructureData, delta: StructureDelta): StructureData {
  const changedNodes = new Map(delta.nodes.map(node => [node.id, node]));
  const removedNodes = new Set(delta.removedNodes);
  const known = new Set(data.nodes.map(node => node.id));
  const changedLinks = new Map(delta.links.map(link => [linkKey(link), link]));
  const removedLinks = new Set(delta.removedLinks);
  const knownLinks = new Set(data.links.map(linkKey));
  return {
    ...data,
    nodes: [...data.nodes.filter(node => !removedNodes.has(node.id)).map(node => changedNodes.get(node.id) ?? node),
      ...delta.nodes.filter(node => !known.has(node.id))],
    links: [...data.links.filter(link => !removedLinks.has(linkKey(link))).map(link => changedLinks.get(linkKey(link)) ?? link),
      ...delta.links.filter(link => !knownLinks.has(linkKey(link)))],
    revision: delta.revision,
    generatedAt: delta.generatedAt,
  };
}

/** Observed and not declared: a host or container that reports while nothing declares it. */
export const isUndeclared = (node: Pick<StructureNode, 'provenance' | 'observation'>): boolean =>
  node.provenance === 'observed' || node.observation?.difference === 'undeclared';
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
  // An undeclared node still has a health; the "not declared" chip says the rest.
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
const size = (bytes: number): string => bytes >= 1024 ** 3 ? gib(bytes)
  : bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MiB` : `${Math.max(0, Math.round(bytes / 1024))} KiB`;
const rate = (bytes: number): string => bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MiB/s` : `${(bytes / 1024).toFixed(1)} KiB/s`;
const COVERAGE: Record<string, string> = { both: 'backup and git remote', backup: 'backup', 'git-remote': 'git remote', none: 'not covered' };

export interface StructureFactLine { label: string; value: string; }

/** The facts of a node as label and value pairs, in a fixed order and without any ranking. */
export function factLines(node: Pick<StructureNode, 'facts'>): StructureFactLine[] {
  const facts = node.facts;
  if (!facts) return [];
  const lines: StructureFactLine[] = [];
  const add = (label: string, value: string | null | undefined): void => { if (value) lines.push({ label, value }); };
  add('Reported as', [facts.machineKind, facts.virtualization, facts.containerName].filter(Boolean).join(' · '));
  add('Roles', facts.roles?.join(', '));
  const performance = facts.performance;
  if (performance) {
    add('CPU', performance.cpuPercent != null
      ? `${performance.cpuPercent.toFixed(0)}%${performance.cpuCores ? ` of ${performance.cpuCores} cores` : ''}`
      : performance.cpuCores ? `${performance.cpuCores} cores` : null);
    add('Load', performance.load1 != null
      ? [performance.load1, performance.load5, performance.load15].filter(value => value != null).map(value => value!.toFixed(2)).join(' · ') : null);
    add('Memory', performance.memoryUsedBytes != null && performance.memoryTotalBytes
      ? `${gib(performance.memoryUsedBytes)} of ${gib(performance.memoryTotalBytes)}`
      : performance.memoryTotalBytes ? gib(performance.memoryTotalBytes) : null);
    for (const disk of performance.disks ?? [])
      add(`Disk ${disk.mount}`, `${gib(disk.usedBytes)} used · ${gib(disk.freeBytes)} free · ${disk.purpose}`);
    add('Network', performance.networkReceiveBytesPerSecond != null && performance.networkTransmitBytesPerSecond != null
      ? `↓ ${rate(performance.networkReceiveBytesPerSecond)} · ↑ ${rate(performance.networkTransmitBytesPerSecond)}` : null);
  }
  const capacity = facts.capacity;
  if (capacity && (capacity.totalSlots != null || capacity.busySlots > 0))
    add('Slots', `${capacity.busySlots} busy${capacity.freeSlots != null ? ` · ${capacity.freeSlots} free` : ''}`);
  add('Running cards', capacity?.runningCards.join(', '));
  const release = facts.release;
  if (release) add('Version', [release.version ?? 'not reported', release.stableVersion ? `stable ${release.stableVersion}` : null,
    release.state, release.pendingInstall ? 'install pending' : null].filter(Boolean).join(' · '));
  add('Last seen', facts.lastSeenAt ? new Date(facts.lastSeenAt).toLocaleString('en-US') : null);
  for (const location of facts.dataLocations ?? [])
    add(location.label, !location.exists ? 'not visible to the API'
      : `${location.sizeBytes != null ? `${location.sizeComplete ? '' : 'at least '}${size(location.sizeBytes)}` : 'size not measured yet'} · ${COVERAGE[location.coverage] ?? location.coverage}`);
  const warnings = facts.warnings;
  if (warnings) {
    add('Tools', warnings.tools.map(tool => `${tool.name}${tool.version ? ` ${tool.version}` : ''}${tool.state === 'ok' ? '' : ` (${tool.state}${tool.targetVersion ? `, target ${tool.targetVersion}` : ''})`}`).join(', '));
    add('Missing', warnings.missingRequired.join(', '));
    add('Links down', warnings.linksDown.length ? String(warnings.linksDown.length) : null);
    add('Waiting cards', warnings.blockedCards.map(card => `${card.key} ${card.state} (${card.reason})`).join(', '));
  }
  return lines;
}
