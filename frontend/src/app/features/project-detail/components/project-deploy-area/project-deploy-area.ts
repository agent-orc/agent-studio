import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { ProjectDeploymentPanelComponent } from '../project-deployment-panel/project-deployment-panel.component';
import { ProjectDeployOverviewComponent } from '../project-deploy-overview/project-deploy-overview';
import { ProjectDeployReleasesComponent } from '../project-deploy-releases/project-deploy-releases';
import { ProjectDeployPipelineComponent } from '../project-deploy-pipeline/project-deploy-pipeline';
import { ProjectDeployDataService, type ProjectDeploySnapshot } from '../../services/project-deploy-data.service';

type DeployView = 'overview' | 'releases' | 'pipeline' | 'tools';

@Component({
  selector: 'app-project-deploy-area',
  standalone: true,
  imports: [ProjectDeployOverviewComponent, ProjectDeployReleasesComponent, ProjectDeployPipelineComponent, ProjectDeploymentPanelComponent],
  templateUrl: './project-deploy-area.html',
  styleUrl: './project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployAreaComponent {
  private readonly data = inject(ProjectDeployDataService);
  readonly projectName = input.required<string>();
  readonly snapshot = signal<ProjectDeploySnapshot | null>(null);
  readonly view = signal<DeployView>('overview');

  constructor() {
    effect(onCleanup => {
      this.snapshot.set(null);
      const subscription = this.data.load(this.projectName()).subscribe(snapshot => this.snapshot.set(snapshot));
      onCleanup(() => subscription.unsubscribe());
    });
  }

  show(view: DeployView): void { this.view.set(view); }
}
