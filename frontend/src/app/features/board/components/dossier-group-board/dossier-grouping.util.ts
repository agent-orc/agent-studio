import type { TaskInfo } from '../../../../models/task.model';

export interface DossierBoardGroup {
  id: string;
  path: string | null;
  cards: TaskInfo[];
  completed: number;
}

/** Group only the visible cards, so every count matches the board filters. */
export function groupCardsByDossier(tasks: readonly TaskInfo[]): DossierBoardGroup[] {
  const groups = new Map<string, DossierBoardGroup>();
  for (const task of tasks) {
    const path = task.dossier?.path?.trim() || null;
    const id = `${task.projectName.toLowerCase()}::${path?.toLowerCase() ?? '__unassigned__'}`;
    let group = groups.get(id);
    if (!group) {
      group = { id, path, cards: [], completed: 0 };
      groups.set(id, group);
    }
    group.cards.push(task);
    if (task.state === '6-completed' || task.state === '7-archive') group.completed++;
  }
  return [...groups.values()].sort((a, b) =>
    a.path === null && b.path === null ? a.id.localeCompare(b.id)
      : a.path === null ? 1 : b.path === null ? -1
        : a.path.localeCompare(b.path) || a.id.localeCompare(b.id));
}
