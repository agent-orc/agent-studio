import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { DeployEnvironmentCardComponent } from '../deploy-environment-card/deploy-environment-card';
import { ProjectDeployStepsFormComponent } from '../project-deploy-steps-form/project-deploy-steps-form';
import type { PipelineDefinitionResponse } from '../../services/project-pipeline-definition.service';
import {
  declaredEnvironments, type DeployActionOption, type DeployActionState, type DeployPanel, type EnvironmentCardView,
} from '../../state/deploy-area.model';

/**
 * Overview: one status card per declared environment. Environments project.yml
 * declares before they have deploy steps show as declared cards with a way to
 * add the steps (F5); the legacy JSON guidance is only for schema 1 projects.
 */
@Component({
  selector: 'app-project-deploy-overview',
  standalone: true,
  imports: [EmptyStateComponent, LoadingSurfaceComponent, StatusIndicatorComponent, DeployEnvironmentCardComponent,
    ProjectDeployStepsFormComponent],
  templateUrl: './project-deploy-overview.html',
  styleUrl: './project-deploy-overview.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployOverviewComponent {
  /** Environment panel state; cards are only read once it is ready. */
  readonly panel = input.required<DeployPanel<unknown>>();
  readonly cards = input.required<readonly EnvironmentCardView[]>();
  readonly now = input.required<number>();
  readonly busy = input(false);
  readonly stop = output<string>();
  readonly remove = output<string>();
  readonly projectName = input('');
  /** The parsed project.yml, or null while it loads or when it is unavailable. */
  readonly definition = input<PipelineDefinitionResponse | null>(null);
  readonly openRun = output<string>();
  readonly openTools = output<void>();
  readonly actions = input<readonly DeployActionOption[]>([]);
  readonly actionState = input<DeployActionState>({ busy: null, error: null });
  readonly startAction = output<DeployActionOption>();
  readonly openSecrets = output<string>();
  /** project.yml changed; the owner reloads the Deploy sources. */
  readonly definitionChanged = output<void>();

  readonly addingSteps = signal(false);
  readonly canAddSteps = computed(() => !!this.definition()?.deployStepsAvailable && this.projectName() !== '');
  readonly declared = computed(() => declaredEnvironments(this.definition(), this.cards()));
  readonly environmentNames = computed(() => this.definition()?.diagram?.environments.map(item => item.name) ?? []);

  readonly errorBody = computed(() => {
    const panel = this.panel();
    const reason = panel.status === 'error' ? panel.message : '';
    return `${reason} The other Deploy views still work; try Refresh.`.trim();
  });

  stepsWritten(): void {
    this.addingSteps.set(false);
    this.definitionChanged.emit();
  }
}
