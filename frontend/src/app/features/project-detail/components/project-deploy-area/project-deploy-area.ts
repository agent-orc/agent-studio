import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ProjectDeploymentPanelComponent } from '../project-deployment-panel/project-deployment-panel.component';
import { ProjectDeployOverviewComponent } from '../project-deploy-overview/project-deploy-overview';
import { ProjectDeployReleasesComponent } from '../project-deploy-releases/project-deploy-releases';
import { ProjectDeployPipelineComponent } from '../project-deploy-pipeline/project-deploy-pipeline';
import { ProjectDeployHistoryComponent } from '../project-deploy-history/project-deploy-history';
import { DeployRunDrawerComponent } from '../deploy-run-drawer/deploy-run-drawer';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import type { ProjectEnvironments } from '../../../../models/project-overview.model';
import {
  DEPLOY_CLOCK,
  buildDeployRuns,
  buildEnvironmentCards,
  type DeployHealthCheckSummary,
  type DeployLifecycleRead,
  type DeployPanel,
  type Release,
} from '../../state/deploy-area.model';

type DeployView = 'overview' | 'releases' | 'pipeline' | 'history' | 'tools';

function ready<T>(panel: DeployPanel<T>): T | null {
  return panel.status === 'ready' ? panel.value : null;
}

@Component({
  selector: 'app-project-deploy-area',
  standalone: true,
  imports: [
    ProjectDeployOverviewComponent, ProjectDeployReleasesComponent, ProjectDeployPipelineComponent,
    ProjectDeployHistoryComponent, DeployRunDrawerComponent, ProjectDeploymentPanelComponent,
  ],
  templateUrl: './project-deploy-area.html',
  styleUrl: './project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployAreaComponent {
  private readonly data = inject(ProjectDeployDataService);
  private readonly clock = inject(DEPLOY_CLOCK);
  private subscriptions = new Subscription();
  private runOpener: HTMLElement | null = null;

  readonly projectName = input.required<string>();
  readonly view = signal<DeployView>('overview');
  readonly now = signal(this.clock());
  readonly environmentPanel = signal<DeployPanel<ProjectEnvironments>>({ status: 'loading' });
  readonly releasePanel = signal<DeployPanel<Release[]>>({ status: 'loading' });
  readonly healthPanel = signal<DeployPanel<DeployHealthCheckSummary[]>>({ status: 'loading' });
  readonly lifecyclePanel = signal<DeployPanel<DeployLifecycleRead>>({ status: 'loading' });
  readonly selectedRunId = signal<string | null>(null);

  readonly runs = computed(() => {
    const environments = ready(this.environmentPanel());
    return environments ? buildDeployRuns(environments, ready(this.releasePanel())) : [];
  });
  readonly cards = computed(() => {
    const environments = ready(this.environmentPanel());
    return environments
      ? buildEnvironmentCards(environments, this.runs(), ready(this.releasePanel()), ready(this.healthPanel()))
      : [];
  });
  readonly environmentOptions = computed(() => this.cards().map(card => ({ id: card.id, name: card.name })));
  readonly selectedRun = computed(() => this.runs().find(run => run.id === this.selectedRunId()) ?? null);

  constructor() {
    effect(onCleanup => {
      this.load(this.projectName());
      onCleanup(() => this.subscriptions.unsubscribe());
    });
  }

  show(view: DeployView): void { this.view.set(view); }

  refresh(): void { this.load(this.projectName()); }

  openRun(runId: string): void {
    this.runOpener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.selectedRunId.set(runId);
  }

  closeRun(): void {
    this.selectedRunId.set(null);
    const opener = this.runOpener;
    this.runOpener = null;
    if (opener?.isConnected) queueMicrotask(() => opener.focus());
  }

  private load(projectName: string): void {
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.now.set(this.clock());
    this.selectedRunId.set(null);
    this.subscriptions.add(this.data.environments(projectName).subscribe(panel => this.environmentPanel.set(panel)));
    this.subscriptions.add(this.data.releases(projectName).subscribe(panel => this.releasePanel.set(panel)));
    this.subscriptions.add(this.data.healthSummaries(projectName).subscribe(panel => this.healthPanel.set(panel)));
    this.subscriptions.add(this.data.lifecycle(projectName).subscribe(panel => this.lifecyclePanel.set(panel)));
  }
}
