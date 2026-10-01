import { Injectable, computed, inject } from '@angular/core';
import type { MarkdownTaskReference } from 'coding-agent-chat/markdown';
import type { TaskInfo } from '../models/task.model';
import { NotificationService } from './notification.service';
import { TaskService } from './task.service';
// These two services are imported from their concrete module paths rather than
// the feature barrels, which is why the cross-feature barrel lint rule is
// disabled on each line below. The studio-shell / task-detail barrels also
// re-export their heavy host components (StudioShellComponent, TaskDetailComponent).
// Those components render <app-markdown-view>, which injects THIS service - so a
// barrel import here closes a module cycle that left the host component def
// undefined at evaluation time (Angular NG0919) and blanked the studio tab strip.
// The barrels share one module per feature, so a service-only import cannot avoid
// pulling the component; the direct path is the narrow break that keeps this
// root-level service out of the component graph.
// eslint-disable-next-line no-restricted-imports
import { TaskSelectionService } from '../features/task-detail/state/task-selection.service';
// eslint-disable-next-line no-restricted-imports
import { StudioTabStateService } from '../features/studio-shell/services/studio-tab-state.service';

@Injectable({ providedIn: 'root' })
export class TaskReferenceNavigationService {
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly notifications = inject(NotificationService);

  private readonly jobsByTaskKey = computed(() => {
    const map = new Map<string, TaskInfo>();
    for (const job of this.currentJobs()) {
      map.set(job.taskKey, job);
    }
    return map;
  });

  readonly markdownReferences = computed<readonly MarkdownTaskReference[]>(() => {
    const references: MarkdownTaskReference[] = [];
    for (const job of this.currentJobs()) {
      const labels = new Set<string>();
      if (job.key) labels.add(job.key);
      if (job.id) labels.add(job.id);
      const taskKeyTail = job.taskKey.includes('::')
        ? job.taskKey.slice(job.taskKey.lastIndexOf('::') + 2)
        : job.taskKey;
      if (taskKeyTail) labels.add(taskKeyTail);
      const folderSlug = folderName(job.folderPath);
      if (folderSlug) labels.add(folderSlug);
      for (const label of labels) {
        references.push({ label, taskKey: job.taskKey });
      }
    }
    return references;
  }, { equal: sameMarkdownTaskReferences });

  openTaskKey(taskKey: string | null | undefined): boolean {
    if (!taskKey) return false;
    const job = this.jobsByTaskKey().get(taskKey);
    if (!job) return false;
    this.tabs.open({ kind: 'task', taskKey: job.taskKey });
    this.selection.openDetail(job);
    return true;
  }

  /** Open a task by a display reference such as `AGT-1234` (key, id, or folder slug). */
  openReference(label: string | null | undefined): boolean {
    const wanted = (label ?? '').trim().toUpperCase();
    if (!wanted) return false;
    const match = this.markdownReferences().find((item) => item.label.toUpperCase() === wanted);
    return this.openTaskKey(match?.taskKey);
  }

  /** {@link openReference}, telling the user when the referenced task is not loaded. */
  openReferenceOrNotify(label: string): boolean {
    if (this.openReference(label)) return true;
    this.notifications.info(`${label} is not loaded in the current workspace view.`);
    return false;
  }

  private currentJobs(): readonly TaskInfo[] {
    const source = this.tasks.jobs as unknown;
    if (typeof source === 'function') return source() as readonly TaskInfo[];
    return Array.isArray(source) ? source as readonly TaskInfo[] : [];
  }
}

function folderName(path: string | null | undefined): string | null {
  const trimmed = (path ?? '').trim().replace(/[\\/]+$/, '');
  if (!trimmed) return null;
  const parts = trimmed.split(/[\\/]+/);
  return parts[parts.length - 1] || null;
}

function sameMarkdownTaskReferences(
  previous: readonly MarkdownTaskReference[],
  current: readonly MarkdownTaskReference[],
): boolean {
  if (previous === current) return true;
  if (previous.length !== current.length) return false;
  return previous.every((reference, index) =>
    reference.label === current[index].label
    && reference.taskKey === current[index].taskKey);
}
