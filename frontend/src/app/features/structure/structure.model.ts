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
}

const PLACEMENT = new Set(['physical', 'cloud-server', 'vm', 'cluster', 'namespace', 'host', 'server']);
const GROUP = new Set(['boundary', 'system', 'environment', 'location', 'group', ...PLACEMENT]);
const isPlacement = (node: StructureNode): boolean => PLACEMENT.has(node.kind) || node.id.startsWith('machine:');
const isGroup = (node: StructureNode): boolean => GROUP.has(node.kind) || isPlacement(node);

/** One pass over the declaration graph. Unknown placement kinds remain boxes. */
export function projectStructure(data: StructureData, view: 'host' | 'container', level: 1 | 2 | 3,
  toggles: Readonly<Record<string, boolean>>): StructureBox[] {
  const nodes = new Map<string, StructureBox>();
  const boundary = data.nodes.find(node => node.kind === 'boundary');
  if (view === 'container' && boundary) {
    for (const role of new Set(data.nodes.filter(node => !isGroup(node) && node.kind !== 'external')
      .map(node => node.role || 'Other'))) {
      const id = `function:${role}`;
      nodes.set(id, { ...boundary, id, label: role, kind: 'group', parentId: boundary.id,
        children: [], collapsed: false, summary: {}, replicas: 1 });
    }
  }
  for (const node of data.nodes) {
    if (view === 'container' && (isPlacement(node) || node.kind === 'location')) continue;
    const parentId = view === 'container' && !isGroup(node) && node.kind !== 'external'
      ? `function:${node.role || 'Other'}` : node.parentId;
    nodes.set(node.id, { ...node, parentId, children: [], collapsed: false, summary: {}, replicas: 1 });
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
      (node.kind === 'location' || isPlacement(node)))) {
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
  const groupReplicas = (box: StructureBox): void => {
    for (const child of box.children) groupReplicas(child);
    if (level === 3 || toggles[box.id] === false) return;
    const seen = new Map<string, StructureBox>();
    box.children = box.children.filter(child => {
      const match = /^(.*?)(?:\s+[1-9][0-9]*)$/.exec(child.label);
      if (!match || child.kind !== 'container') return true;
      const key = `${child.kind}:${match[1]}`;
      const first = seen.get(key);
      if (!first) { seen.set(key, child); child.label = match[1]; return true; }
      first.replicas += child.replicas;
      return false;
    });
  };
  for (const root of roots) groupReplicas(root);
  const decorate = (box: StructureBox): Record<string, number> => {
    const counts: Record<string, number> = {};
    for (const child of box.children) {
      const nested = decorate(child);
      if (isGroup(child)) {
        if (child.kind === 'vm' || child.kind === 'cluster' || child.kind === 'namespace')
          counts[child.kind] = (counts[child.kind] || 0) + 1;
        for (const [kind, count] of Object.entries(nested)) counts[kind] = (counts[kind] || 0) + count;
      } else counts[child.kind] = (counts[child.kind] || 0) + child.replicas;
    }
    box.summary = counts;
    box.collapsed = toggles[box.id] ?? (level === 1 && isPlacement(box) ||
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
    `${count} ${kind}${count === 1 ? '' : 's'}`).join(' · ');
}

export function nearestVisible(id: string, visible: ReadonlySet<string>, source: ReadonlyMap<string, StructureNode>): string | null {
  let node = source.get(id);
  while (node && !visible.has(node.id)) node = node.parentId ? source.get(node.parentId) : undefined;
  return node?.id ?? null;
}
