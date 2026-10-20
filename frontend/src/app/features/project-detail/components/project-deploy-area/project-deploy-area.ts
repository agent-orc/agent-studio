import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { ProjectDeploymentPanelComponent } from '../project-deployment-panel/project-deployment-panel.component';
import { ProjectDeployOverviewComponent } from '../project-deploy-overview/project-deploy-overview';
import { ProjectDeployReleasesComponent } from '../project-deploy-releases/project-deploy-releases';
import { ProjectDeployPipelineComponent } from '../project-deploy-pipeline/project-deploy-pipeline';
import { ProjectPipelinePanelComponent } from '../project-pipeline-panel/project-pipeline-panel.component';
import { ProjectDeployDataService, type ProjectDeploySnapshot } from '../../services/project-deploy-data.service';
import { EnvironmentHealthPanelComponent } from '../../../environment-health';
import { ProjectSecretsPageComponent } from '../../../secrets/project-secrets-page/project-secrets-page.component';

export type DeployView = 'overview' | 'releases' | 'pipeline' | 'steps' | 'operations' | 'secrets' | 'tools';

@Component({
  selector: 'app-project-deploy-area',
  standalone: true,
  imports: [ProjectDeployOverviewComponent, ProjectDeployReleasesComponent, ProjectDeployPipelineComponent, ProjectDeploymentPanelComponent,
    ProjectPipelinePanelComponent, EnvironmentHealthPanelComponent, ProjectSecretsPageComponent],
  templateUrl: './project-deploy-area.html',
  styleUrl: './project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployAreaComponent {
  private readonly data = inject(ProjectDeployDataService);
  readonly projectName = input.required<string>();
  /** View opened on entry; old Pipeline bookmarks land on the run steps. */
  readonly initialView = input<DeployView>('overview');
  readonly focusStepId = input<string | undefined>();
  readonly openPrompts = output<void>();
  /** Emits each view the user selects so the host can keep Run steps in the route. */
  readonly viewChange = output<DeployView>();
  readonly snapshot = signal<ProjectDeploySnapshot | null>(null);
  readonly view = signal<DeployView>('overview');

  constructor() {
    effect(() => this.view.set(this.initialView()));
    effect(onCleanup => {
      this.snapshot.set(null);
      const subscription = this.data.load(this.projectName()).subscribe(snapshot => this.snapshot.set(snapshot));
      onCleanup(() => subscription.unsubscribe());
    });
  }

  show(view: DeployView): void {
    this.view.set(view);
    this.viewChange.emit(view);
  }
}
