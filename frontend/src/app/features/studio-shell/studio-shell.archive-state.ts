import type { RegistryProjectSummary, RegistryWorkspaceListItem } from '../../models/task.model';
import type { StudioTab } from './studio-shell.types';
import { buildProjectSidebarRows } from './studio-shell.project-rows';
import type { GroupedJobs } from '../../models/task.model';

function normalized(path: string): string {
  return path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
}

export function activeRegistryWorkspaces(workspaces: readonly RegistryWorkspaceListItem[]): RegistryWorkspaceListItem[] {
  return workspaces.map(workspace => ({ ...workspace,
    projects: workspace.projects.filter(project => !project.archived) }));
}

export function visibleProjectRows(grouped: GroupedJobs, known: readonly string[], active: string | null,
  archived: ReadonlySet<string>) {
  return buildProjectSidebarRows(grouped, known, active).filter(row => !archived.has(row.name));
}

export function archivedTabProject(tab: StudioTab | null, taskProject: string | null,
  currentProject: string | null, archived: readonly RegistryProjectSummary[]): string | null {
  if (tab?.kind === 'task') {
    const projectKey = tab.taskKey.split('::')[0];
    const scopedProject = tab.scope?.kind === 'project' ? tab.scope.projectName : null;
    return archived.find(project => project.displayName === taskProject
      || project.displayName === scopedProject
      || project.id === projectKey
      || normalized(project.storageLocation) === normalized(projectKey))?.displayName ?? null;
  }
  return archived.some(project => project.displayName === currentProject) ? currentProject : null;
}
