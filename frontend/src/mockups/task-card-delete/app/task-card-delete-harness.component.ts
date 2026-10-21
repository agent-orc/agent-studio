import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import { TaskCardComponent } from '../../../app/features/board';
import { TaskState } from '../../../app/models/task.model';
import type { TaskInfo } from '../../../app/models/task.model';

/**
 * Visual harness for the task card context menu in both themes.
 */
function makeTask(overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    id: 'task-seed',
    taskKey: 'demo::task-seed',
    key: 'ATP-1',
    title: 'Task-Karte: Löschen ins Kontextmenü',
    state: TaskState.Progress,
    order: 1,
    agent: 'codex',
    createdAt: '2026-07-09T09:00:00Z',
    watchPath: '/demo/watch',
    projectName: 'Demo',
    folderPath: '/demo/watch/3-progress/task-seed',
    lastActivity: '2026-07-09T09:30:00Z',
    sessionName: null,
    model: 'opus-4.8',
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  } as TaskInfo;
}

type Theme = 'dark' | 'light';

@Component({
  selector: 'mockup-task-card-delete-harness',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TaskCardComponent],
  templateUrl: './task-card-delete-harness.component.html',
  styleUrl: './task-card-delete-harness.component.scss',
})
export class TaskCardDeleteHarnessComponent {
  readonly theme = signal<Theme>('dark');

  readonly task = signal<TaskInfo>(makeTask());
  setTheme(theme: Theme): void {
    this.theme.set(theme);
    const root = document.documentElement;
    if (theme === 'light') {
      root.setAttribute('data-studio-theme', 'light');
    } else {
      root.removeAttribute('data-studio-theme');
    }
  }
}
