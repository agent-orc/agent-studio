import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { LoadingSurfaceComponent } from '@studio/ui/components/async-feedback/index';
import { ProjectRunbooksComponent } from '../project-runbooks/project-runbooks';
import { ProjectOperationsRailComponent } from '../project-operations-rail/project-operations-rail';
import { EnvironmentStateComponent } from '../environment-state/environment-state';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { DEPLOY_CLOCK, type EnvironmentCardView } from '../../state/deploy-area.model';

/** One environment row: its state from the shared projection, and the runbook kind its declaration names. */
interface EnvironmentRow {
  state: EnvironmentCardView;
  runbookKind: 'staging' | 'production' | null;
}

/** Operations entry: application health per declared environment. */
@Component({
  selector: 'app-project-environment-overview',
  standalone: true,
  imports: [LoadingSurfaceComponent, ProjectRunbooksComponent, ProjectOperationsRailComponent, EnvironmentStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-environment-overview.component.html',
  styleUrl: './project-environment-overview.component.scss',
})
export class ProjectEnvironmentOverviewComponent {
  private readonly data = inject(ProjectDeployDataService);
  private readonly clock = inject(DEPLOY_CLOCK);
  readonly projectName = input.required<string>();
  readonly heading = input('Operations');
  readonly focusedEnvironment = input<string | null>(null);
  readonly openDeploy = output<void>();
  readonly openRelease = output<string>();
  readonly openTask = output<{ jobId: string; watchPath: string }>();
  readonly environments = signal<readonly EnvironmentRow[]>([]);
  /** An incident's Runbooks link names one environment; the others stay out of the way. */
  readonly visibleEnvironments = computed(() => this.focusedEnvironment()
    ? this.environments().filter(item => item.state.id === this.focusedEnvironment())
    : this.environments());
  readonly now = signal(this.clock());
  readonly loading = signal(true);
  readonly failed = signal(false);

  constructor() {
    effect(onCleanup => {
      const projectName = this.projectName();
      this.loading.set(true);
      this.failed.set(false);
      const subscription = this.data.environmentStates(projectName).subscribe(panel => {
        if (panel.status === 'loading') return;
        this.now.set(this.clock());
        if (panel.status === 'ready') {
          const kinds = new Map(panel.value.environments?.environments.map(item => [item.declaration.id, item.declaration.kind]) ?? []);
          this.environments.set(panel.value.states.map(state => ({ state, runbookKind: runbookKind(kinds.get(state.id)) })));
        } else {
          this.environments.set([]);
          this.failed.set(true);
        }
        this.loading.set(false);
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
}

function runbookKind(kind: string | undefined): EnvironmentRow['runbookKind'] {
  if (kind === 'stage') return 'staging';
  return kind === 'production' ? 'production' : null;
}
