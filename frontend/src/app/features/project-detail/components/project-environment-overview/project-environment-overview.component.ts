import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import type { ProjectEnvironments } from '../../../../models/project-overview.model';
import { TaskService } from '../../../../services/task.service';
import { ProjectRunbooksComponent } from '../project-runbooks/project-runbooks';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { ProjectOperationsRailComponent } from '../project-operations-rail/project-operations-rail';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';

type EnvironmentStatus = ProjectEnvironments['environments'][number];

/** Operations entry: application health per declared environment. */
@Component({
  selector: 'app-project-environment-overview',
  standalone: true,
  imports: [ProjectRunbooksComponent, ProjectOperationsRailComponent, StatusIndicatorComponent, LoadingSurfaceComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-environment-overview.component.html',
  styleUrl: './project-environment-overview.component.scss',
})
export class ProjectEnvironmentOverviewComponent {
  private readonly tasks = inject(TaskService);
  readonly projectName = input.required<string>();
  readonly heading = input('Operations');
  readonly focusedEnvironment = input<string | null>(null);
  readonly openDeploy = output<void>();
  readonly openRelease = output<string>();
  readonly openTask = output<{ jobId: string; watchPath: string }>();
  readonly environments = signal<readonly EnvironmentStatus[]>([]);
  readonly visibleEnvironments = computed(() => this.focusedEnvironment()
    ? this.environments().filter(item => item.declaration.id === this.focusedEnvironment())
    : this.environments());
  readonly loading = signal(true);
  readonly failed = signal(false);

  constructor() {
    effect(onCleanup => {
      const projectName = this.projectName();
      this.loading.set(true);
      this.failed.set(false);
      const subscription = this.tasks.getProjectEnvironments(projectName).subscribe({
        next: value => {
          this.environments.set(value.environments);
          this.loading.set(false);
        },
        error: () => {
          this.environments.set([]);
          this.failed.set(true);
          this.loading.set(false);
        },
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  healthTone(health: string): StatusTone {
    if (health === 'healthy') return 'success';
    if (health === 'failed') return 'danger';
    return 'neutral';
  }

  healthLabel(health: string): string {
    if (health === 'healthy') return 'Healthy';
    if (health === 'failed') return 'Failed';
    return 'Health unknown';
  }
}
