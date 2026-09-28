import { WikiSearchResponse, WikiTreeNode } from '../../../../models/project-docs.model';

/**
 * A flattened, depth-tagged row of the physical wiki tree, ready for `@for`
 * rendering. Folders are descended into only when expanded.
 */
export interface WikiTreeRow {
  node: WikiTreeNode;
  depth: number;
  hasChildren: boolean;
  expanded: boolean;
}

/**
 * Stable id for a tree node. The backend already guarantees a unique relPath per
 * node; folders and files never share one, so relPath doubles as the id. The
 * (never-rendered) synthetic root would have a null relPath, hence the fallback.
 */
export function nodeId(node: WikiTreeNode): string {
  return node.relPath ?? '';
}

export function findWikiNode(nodes: readonly WikiTreeNode[], id: string): WikiTreeNode | null {
  for (const node of nodes) {
    if (nodeId(node) === id) return node;
    const child = findWikiNode(node.children, id);
    if (child) return child;
  }
  return null;
}

export function findFirstWikiDocument(nodes: readonly WikiTreeNode[]): WikiTreeNode | null {
  for (const node of nodes) {
    if (node.type !== 'folder') return node;
    const child = findFirstWikiDocument(node.children);
    if (child) return child;
  }
  return null;
}

/** Keep only documents carrying every selected tag and their parent folders. */
export function filterWikiTreeByTags(roots: readonly WikiTreeNode[], ids: ReadonlySet<string>): WikiTreeNode[] {
  if (!ids.size) return [...roots];
  const keep = (node: WikiTreeNode): WikiTreeNode | null => {
    if (node.type !== 'folder') return [...ids].every(id => node.tags?.includes(id)) ? node : null;
    const children = node.children.map(keep).filter((child): child is WikiTreeNode => child !== null);
    return children.length ? { ...node, children } : null;
  };
  return roots.map(keep).filter((node): node is WikiTreeNode => node !== null);
}

export function filterWikiSearchByTags(
  response: WikiSearchResponse | null,
  roots: readonly WikiTreeNode[],
  ids: ReadonlySet<string>,
): WikiSearchResponse | null {
  if (!response || !ids.size) return response;
  const paths = new Set(collectDocumentPaths(roots));
  return { ...response, results: response.results.filter(hit => paths.has(hit.relPath)) };
}

/**
 * Flattens the physical tree to a render list, descending into a folder only
 * when its id is in `expanded`. Folders are always listed before their
 * (expanded) children, matching the backend's folders-first ordering.
 */
export function flattenWikiTree(
  roots: readonly WikiTreeNode[],
  expanded: ReadonlySet<string>,
): WikiTreeRow[] {
  const out: WikiTreeRow[] = [];
  const walk = (nodes: readonly WikiTreeNode[], depth: number): void => {
    for (const node of nodes) {
      const isFolder = node.type === 'folder';
      const hasChildren = node.children.length > 0;
      const isOpen = isFolder && expanded.has(nodeId(node));
      out.push({ node, depth, hasChildren, expanded: isOpen });
      if (isOpen) walk(node.children, depth + 1);
    }
  };
  walk(roots, 0);
  return out;
}

/**
 * Filters the tree to nodes matching `needle` (case-insensitive, against the
 * node's own title and basename). A folder is kept when it has a kept
 * descendant, so the path to every match stays navigable. Matching deliberately
 * ignores the ancestor-inclusive relPath: otherwise every file under a matched
 * folder would match via its folder prefix. An empty needle returns the tree
 * unchanged.
 */
export function filterWikiTree(
  roots: readonly WikiTreeNode[],
  needle: string,
): WikiTreeNode[] {
  const q = needle.trim().toLowerCase();
  if (!q) return [...roots];

  const matches = (n: WikiTreeNode): boolean =>
    n.title.toLowerCase().includes(q) || n.name.toLowerCase().includes(q);

  const keep = (n: WikiTreeNode): WikiTreeNode | null => {
    if (n.type !== 'folder') return matches(n) ? n : null;
    const children = n.children
      .map(keep)
      .filter((c): c is WikiTreeNode => c !== null);
    if (children.length > 0) return { ...n, children };
    return matches(n) ? { ...n, children: [] } : null;
  };

  return roots.map(keep).filter((c): c is WikiTreeNode => c !== null);
}

/** Collects the ids of every folder node in the tree (for "expand all" seeding). */
export function collectFolderIds(roots: readonly WikiTreeNode[]): string[] {
  const out: string[] = [];
  const walk = (nodes: readonly WikiTreeNode[]): void => {
    for (const n of nodes) {
      if (n.type === 'folder') out.push(nodeId(n));
      if (n.children.length > 0) walk(n.children);
    }
  };
  walk(roots);
  return out;
}

/** Depth-first document paths in the exact order projected by the wiki tree. */
export function collectDocumentPaths(roots: readonly WikiTreeNode[]): string[] {
  const out: string[] = [];
  const walk = (nodes: readonly WikiTreeNode[]): void => {
    for (const node of nodes) {
      if (node.type === 'folder') walk(node.children);
      else if (node.relPath) out.push(node.relPath);
    }
  };
  walk(roots);
  return out;
}

export interface WikiSiblingReorder {
  parentRel: string;
  orderedNames: string[];
}

/** Direct document names under one parent, in the tree's current order. */
export function collectDirectDocumentNames(
  roots: readonly WikiTreeNode[],
  parentRel: string,
): string[] {
  return (findFolderChildren(roots, parentRel) ?? [])
    .filter(node => node.type !== 'folder')
    .map(node => node.name);
}

/** Plan a same-kind reorder without mutating the tree. */
export function planWikiSiblingReorder(
  roots: readonly WikiTreeNode[],
  draggedRel: string,
  targetRel: string,
  kind: 'folder' | 'file',
): WikiSiblingReorder | null {
  const parentRel = parentDir(draggedRel);
  if (draggedRel === targetRel || parentRel !== parentDir(targetRel)) return null;
  const siblings = findFolderChildren(roots, parentRel);
  if (!siblings) return null;
  const isKind = (node: WikiTreeNode) => kind === 'folder'
    ? node.type === 'folder'
    : node.type !== 'folder';
  const orderedNames = siblings.filter(isKind).map(node => node.name);
  const from = orderedNames.indexOf(basename(draggedRel));
  const to = orderedNames.indexOf(basename(targetRel));
  if (from < 0 || to < 0 || from === to) return null;
  const [dragged] = orderedNames.splice(from, 1);
  orderedNames.splice(to, 0, dragged);
  return { parentRel, orderedNames };
}

/** Immutable optimistic reorder of file children under one parent folder. */
export function reorderWikiFiles(
  roots: readonly WikiTreeNode[],
  parentRel: string,
  orderedNames: readonly string[],
): WikiTreeNode[] {
  const reorder = (nodes: readonly WikiTreeNode[]): WikiTreeNode[] => {
    const index = new Map(orderedNames.map((name, position) => [name, position]));
    const folders = nodes.filter(node => node.type === 'folder');
    const files = nodes.filter(node => node.type !== 'folder')
      .slice()
      .sort((a, b) => (index.get(a.name) ?? Number.MAX_SAFE_INTEGER)
        - (index.get(b.name) ?? Number.MAX_SAFE_INTEGER));
    return [...folders, ...files];
  };
  if (!parentRel) return reorder(roots);
  return roots.map(node => {
    if (node.type !== 'folder') return node;
    if (node.relPath === parentRel) return { ...node, children: reorder(node.children) };
    return { ...node, children: reorderWikiFiles(node.children, parentRel, orderedNames) };
  });
}

function findFolderChildren(
  roots: readonly WikiTreeNode[],
  parentRel: string,
): readonly WikiTreeNode[] | null {
  if (!parentRel) return roots;
  for (const node of roots) {
    if (node.type !== 'folder') continue;
    if (node.relPath === parentRel) return node.children;
    const nested = findFolderChildren(node.children, parentRel);
    if (nested) return nested;
  }
  return null;
}

function parentDir(relPath: string): string {
  const slash = relPath.lastIndexOf('/');
  return slash < 0 ? '' : relPath.slice(0, slash);
}

function basename(relPath: string): string {
  const slash = relPath.lastIndexOf('/');
  return slash < 0 ? relPath : relPath.slice(slash + 1);
}
