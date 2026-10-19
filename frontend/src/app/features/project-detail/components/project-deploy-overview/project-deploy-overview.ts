import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { DeployEnvironmentCardComponent } from '../deploy-environment-card/deploy-environment-card';
import type { DeployPanel, EnvironmentCardView } from '../../state/deploy-area.model';

/** Overview: one status card per declared environment. */
@Component({
  selector: 'app-project-deploy-overview',
  standalone: true,
  imports: [EmptyStateComponent, LoadingSurfaceComponent, DeployEnvironmentCardComponent],
  templateUrl: './project-deploy-overview.html',
  styleUrl: './project-deploy-overview.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployOverviewComponent {
  /** Environment panel state; cards are only read once it is ready. */
  readonly panel = input.required<DeployPanel<unknown>>();
  readonly cards = input.required<readonly EnvironmentCardView[]>();
  readonly now = input.required<number>();
  readonly openRun = output<string>();
  readonly openTools = output<void>();

  readonly errorBody = computed(() => {
    const panel = this.panel();
    const reason = panel.status === 'error' ? panel.message : '';
    return `${reason} The other Deploy views still work; try Refresh.`.trim();
  });
}
