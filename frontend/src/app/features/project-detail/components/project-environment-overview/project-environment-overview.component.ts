import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import type { ProjectEnvironments } from '../../../../models/project-overview.model';
import { TaskService } from '../../../../services/task.service';
import { ProjectOperationsRailComponent } from '../project-operations-rail/project-operations-rail';

type EnvironmentStatus = ProjectEnvironments['environments'][number];

/** Operations entry: application health per declared environment. */
@Component({
  selector: 'app-project-environment-overview',
  standalone: true,
  imports: [ProjectOperationsRailComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-environment-overview.component.html',
  styleUrl: './project-environment-overview.component.scss',
})
export class ProjectEnvironmentOverviewComponent {
  private readonly tasks = inject(TaskService);
  readonly projectName = input.required<string>();
  readonly openDeploy = output<void>();
  readonly openRelease = output<string>();
  readonly openTask = output<{ jobId: string; watchPath: string }>();
  readonly environments = signal<readonly EnvironmentStatus[]>([]);
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

  healthLabel(health: string): string {
    if (health === 'healthy') return 'Healthy';
    if (health === 'failed') return 'Failed';
    return 'Health unknown';
  }
}
