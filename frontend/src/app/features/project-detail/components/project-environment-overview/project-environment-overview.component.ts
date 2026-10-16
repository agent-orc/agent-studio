import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import type { ProjectDeploymentTarget } from '../../../../models/project-overview.model';
import { TaskService } from '../../../../services/task.service';

/** Environment inventory shared by Deploy and Operations until BD-3 supplies release views. */
@Component({
  selector: 'app-project-environment-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-environment-overview.component.html',
  styleUrl: './project-environment-overview.component.scss',
})
export class ProjectEnvironmentOverviewComponent {
  private readonly tasks = inject(TaskService);
  readonly projectName = input.required<string>();
  readonly area = input.required<'deploy' | 'operations'>();
  readonly openDeploy = output<void>();
  readonly environments = signal<readonly ProjectDeploymentTarget[]>([]);
  readonly loading = signal(true);
  readonly failed = signal(false);

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      this.loading.set(true);
      this.failed.set(false);
      this.tasks.getProjectDeploymentSummary(projectName).subscribe({
        next: summary => {
          if (this.projectName() !== projectName) return;
          this.environments.set(summary.targets);
          this.loading.set(false);
        },
        error: () => {
          if (this.projectName() !== projectName) return;
          this.environments.set([]);
          this.failed.set(true);
          this.loading.set(false);
        },
      });
    });
  }
}
