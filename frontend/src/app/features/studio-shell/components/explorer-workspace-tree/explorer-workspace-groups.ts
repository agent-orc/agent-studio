import type {
  RegistryProjectSummary,
  RegistryProjectUrl,
  RegistryWorkspaceListItem,
} from '../../../../models/task.model';
import type { ExplorerLaneCounts } from '../../studio-shell.project-rows';

/** Flat project row as computed by the shell (`ProjectSidebarRow`). */
export interface ExplorerProjectRow {
  name: string;
  initial: string;
  color: string;
  totalJobs: number;
  laneCounts?: ExplorerLaneCounts;
  isActive: boolean;
}

/** A rendered project row decorated with its registry metadata. */
export interface ExplorerProjectNode extends ExplorerProjectRow {
  /** Present for every registered project, including Unassigned rows. */
  projectId: string | null;
  /** Null when the registered project is not assigned to a real workspace. */
  workspaceId: string | null;
  displayLabel: string;
  shortCode: string | null;
  urls: readonly RegistryProjectUrl[];
}

export interface ExplorerWorkspaceGroup {
  id: string;
  displayName: string;
  color: string | null;
  projects: ExplorerProjectNode[];
}

function folderTail(path: string): string {
  const parts = path.split(/[\\/]+/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}

/** Canonical storage path used for the rename-stable registry join. */
function normalizeStorage(path: string): string {
  return path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
}

function projectNode(
  row: ExplorerProjectRow,
  registry: RegistryProjectSummary | undefined,
  workspaceId = registry?.workspaceId || null,
): ExplorerProjectNode {
  return {
    ...row,
    projectId: registry?.id ?? null,
    workspaceId,
    displayLabel: registry?.displayName ?? row.name,
    shortCode: registry?.shortCode ?? null,
    urls: registry?.urls ?? [],
  };
}

function matchingRegistryProject(
  row: ExplorerProjectRow,
  registryProjects: readonly RegistryProjectSummary[],
  storageByName: ReadonlyMap<string, string>,
): RegistryProjectSummary | undefined {
  const storage = storageByName.get(row.name);
  return registryProjects.find(project =>
    project.displayName === row.name
    || folderTail(project.storageLocation) === row.name
    || (!!storage && normalizeStorage(project.storageLocation) === normalizeStorage(storage)));
}

/**
 * Joins watch-path rows to registry projects and groups them by workspace.
 * Registry records with an empty or invalid workspace remain registered nodes
 * inside the synthetic Unassigned bucket.
 */
export function buildExplorerWorkspaceGroups(
  rows: readonly ExplorerProjectRow[],
  registryWorkspaces: readonly RegistryWorkspaceListItem[],
  flatRegistryProjects: readonly RegistryProjectSummary[],
  storageByName: ReadonlyMap<string, string>,
  activeProjectName: string | null = null,
): ExplorerWorkspaceGroup[] {
  const workspaces = [...registryWorkspaces].sort((a, b) => a.sortOrder - b.sortOrder);
  const registryById = new Map<string, RegistryProjectSummary>();
  for (const workspace of workspaces) {
    for (const project of workspace.projects) registryById.set(project.id, project);
  }
  for (const project of flatRegistryProjects) registryById.set(project.id, project);
  const registryProjects = [...registryById.values()];
  // Registry membership is authoritative even before watch paths load the new project.
  const visibleRows = [...rows];
  for (const project of registryProjects.filter(project => !project.archived)) {
    if (rows.some(row => matchingRegistryProject(row, [project], storageByName))) continue;
    visibleRows.push({
      name: project.displayName,
      initial: project.displayName[0]?.toUpperCase() ?? '?',
      color: project.color ?? '',
      totalJobs: 0,
      isActive: project.displayName === activeProjectName,
    });
  }
  rows = visibleRows;


  if (workspaces.length === 0) {
    return rows.length
      ? [{
        id: '__all__',
        displayName: 'Workspace',
        color: null,
        projects: rows.map(row =>
          projectNode(row, matchingRegistryProject(row, registryProjects, storageByName))),
      }]
      : [];
  }

  const byName = new Map(rows.map(row => [row.name, row] as const));
  const byStorage = new Map<string, ExplorerProjectRow>();
  for (const row of rows) {
    const storage = storageByName.get(row.name);
    if (storage) byStorage.set(normalizeStorage(storage), row);
  }

  const used = new Set<string>();
  const groups: ExplorerWorkspaceGroup[] = [];
  for (const workspace of workspaces) {
    const projects: ExplorerProjectNode[] = [];
    for (const registry of registryProjects.filter(project => project.workspaceId === workspace.id)) {
      const match =
        byStorage.get(normalizeStorage(registry.storageLocation))
        ?? byName.get(registry.displayName)
        ?? byName.get(folderTail(registry.storageLocation));
      if (match && !used.has(match.name)) {
        used.add(match.name);
        projects.push(projectNode(match, registry, workspace.id));
      }
    }
    projects.sort((a, b) => a.displayLabel.localeCompare(b.displayLabel));
    groups.push({
      id: workspace.id,
      displayName: workspace.displayName,
      color: workspace.color,
      projects,
    });
  }

  const leftover = rows
    .filter(row => !used.has(row.name))
    .sort((a, b) => a.name.localeCompare(b.name))
    .map(row => projectNode(
      row,
      matchingRegistryProject(row, registryProjects, storageByName),
    ));
  if (leftover.length > 0) {
    groups.push({
      id: '__unassigned__',
      displayName: 'Unassigned',
      color: null,
      projects: leftover,
    });
  }
  return groups;
}

/** Accessible destination label for a registered project URL. */
export function explorerUrlAriaLabel(project: ExplorerProjectNode, url: RegistryProjectUrl): string {
  return url.id === 'repo'
    ? `Repository, open Git View for ${project.displayLabel}`
    : `${url.label}, opens ${url.url} as an embedded preview tab`;
}
