import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import {
  ProjectUrlContextService,
  type ProjectUrlRepositoryContext,
} from '../../services/project-url-context.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Integration line of the preview source: aligned is good, drift needs attention, no comparison is a fact. */
export function integrationStatus(
  context: Pick<ProjectUrlRepositoryContext, 'comparisonRef' | 'ahead' | 'behind' | 'error'> | null,
): { tone: StatusTone; label: string } {
  if (context?.error) return { tone: 'neutral', label: 'Unavailable' };
  if (!context?.comparisonRef) return { tone: 'neutral', label: 'No comparison line' };
  if (context.ahead === 0 && context.behind === 0) return { tone: 'success', label: `Aligned with ${context.comparisonRef}` };
  return { tone: 'warning', label: `${context.ahead} ahead, ${context.behind} behind ${context.comparisonRef}` };
}

@Component({
  selector: 'app-project-url-context-header',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-url-context-header.html',
  styleUrl: './project-url-context-header.scss',
})
export class ProjectUrlContextHeaderComponent {
  readonly projectId = input<string | null>(null);
  readonly projectName = input.required<string>();
  readonly urlId = input.required<string>();
  readonly workingDirectoryKey = input<string | null>(null);

  private readonly contexts = inject(ProjectUrlContextService);
  private readonly tasks = inject(TaskService);
  private readonly navigation = inject(TaskReferenceNavigationService);

  readonly context = signal<ProjectUrlRepositoryContext | null>(null);
  readonly loading = signal(false);
  readonly tasksExpanded = signal(false);
  readonly openTasks = computed(() => this.tasks.jobs()
    .filter(task => task.projectName === this.projectName()
      && task.state !== TaskState.Completed
      && task.state !== TaskState.Archive)
    .sort((left, right) => this.taskLabel(left).localeCompare(this.taskLabel(right))));
  readonly integration = computed(() => integrationStatus(this.context()));

  constructor() {
    effect(() => {
      const projectId = this.projectId();
      const urlId = this.urlId();
      void this.workingDirectoryKey();
      if (!projectId || !urlId) return;
      this.loading.set(true);
      this.contexts.load(projectId, urlId).subscribe({
        next: context => {
          if (this.projectId() === projectId && this.urlId() === urlId) this.context.set(context);
          this.loading.set(false);
        },
        error: () => this.loading.set(false),
      });
    });
  }

  taskLabel(task: TaskInfo): string {
    return task.key || task.displayKey || task.id;
  }

  openTask(task: TaskInfo): void {
    this.navigation.openTaskKey(task.taskKey);
  }
}
