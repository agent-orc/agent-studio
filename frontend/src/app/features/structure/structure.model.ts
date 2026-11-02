export interface StructureNode {
  id: string;
  kind: string;
  label: string;
  parentId: string | null;
  unitId: string | null;
  environmentId: string | null;
  project: string | null;
  provenance: 'declared';
  sourceRef: string;
  role?: string | null;
  ownerLabel?: string | null;
  otherProject?: boolean;
  systemBoundaryId?: string | null;
  /** Kind line detail, such as the deploy target kind behind a placement. */
  detail?: string | null;
  /** Declared replica count of one deployment. */
  replicas?: number | null;
  /** Managed services: observed through a control-plane health check, or declared only. */
  observation?: 'probed' | 'not-observed' | null;
  /** Health check kind that observes a managed service (http, tcp, tls). */
  probe?: string | null;
  /** Health check status: up, degraded, down, unknown or invalid. */
  status?: string | null;
}

export interface StructureLink {
  from: string;
  to: string;
  kind: string;
  label: string;
  sourceRef: string;
}

export interface StructureData { nodes: StructureNode[]; links: StructureLink[]; }
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
export function observationText(box: Pick<StructureNode, 'observation' | 'probe'>): string | null {
  if (box.observation === 'probed') return `probed via ${PROBE_LABEL[box.probe ?? ''] ?? box.probe ?? 'health check'}`;
  return box.observation === 'not-observed' ? 'not observed' : null;
}

export function nearestVisible(id: string, visible: ReadonlySet<string>, source: ReadonlyMap<string, StructureNode>): string | null {
  let node = source.get(id);
  while (node && !visible.has(node.id)) node = node.parentId ? source.get(node.parentId) : undefined;
  return node?.id ?? null;
}
