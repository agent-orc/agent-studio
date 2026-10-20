import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, type WritableSignal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ProjectDeploymentPanelComponent } from '../project-deployment-panel/project-deployment-panel.component';
import { ProjectDeployOverviewComponent } from '../project-deploy-overview/project-deploy-overview';
import { ProjectDeployReleasesComponent } from '../project-deploy-releases/project-deploy-releases';
import { ProjectDeployPipelineComponent } from '../project-deploy-pipeline/project-deploy-pipeline';
import { ProjectDeployHistoryComponent } from '../project-deploy-history/project-deploy-history';
import { DeployRunDrawerComponent, type DeployRunActionState } from '../deploy-run-drawer/deploy-run-drawer';
import { ProjectPipelinePanelComponent } from '../project-pipeline-panel/project-pipeline-panel.component';
import { EnvironmentHealthPanelComponent } from '../../../environment-health';
import type { EnvironmentHealthResponse } from '../../../environment-health/models/environment-health.model';
import type { PipelineDefinitionResponse } from '../../services/project-pipeline-definition.service';
import { ProjectDeployDataService, serverMessage } from '../../services/project-deploy-data.service';
import type { ProjectEnvironments } from '../../../../models/project-overview.model';
import {
  DEPLOY_CLOCK,
  buildDeployRuns,
  buildEnvironmentCards,
  environmentNames,
  ready,
  type DeployPanel,
  type DeployRunAction,
  type Deployment,
  type DeploymentLogLine,
  type EnvironmentStatus,
  type Release,
} from '../../state/deploy-area.model';

export type DeployView = 'overview' | 'releases' | 'pipeline' | 'history' | 'steps' | 'operations' | 'tools';

const IDLE: DeployRunActionState = { busy: null, error: null };

@Component({
  selector: 'app-project-deploy-area',
  standalone: true,
  imports: [
    ProjectDeployOverviewComponent, ProjectDeployReleasesComponent, ProjectDeployPipelineComponent,
    ProjectDeployHistoryComponent, DeployRunDrawerComponent, ProjectDeploymentPanelComponent,
    ProjectPipelinePanelComponent, EnvironmentHealthPanelComponent,
  ],
  templateUrl: './project-deploy-area.html',
  styleUrl: './project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployAreaComponent {
  private readonly data = inject(ProjectDeployDataService);
  private readonly clock = inject(DEPLOY_CLOCK);
  private subscriptions = new Subscription();
  private runSubscriptions = new Subscription();
  private runOpener: HTMLElement | null = null;

  readonly projectName = input.required<string>();
  readonly initialView = input<DeployView>('overview');
  readonly focusStepId = input<string | undefined>();
  readonly openPrompts = output<void>();
  readonly viewChange = output<DeployView>();
  readonly view = signal<DeployView>('overview');
  readonly now = signal(this.clock());
  readonly environmentPanel = signal<DeployPanel<ProjectEnvironments>>({ status: 'loading' });
  readonly statusPanel = signal<DeployPanel<EnvironmentStatus[]>>({ status: 'loading' });
  readonly releasePanel = signal<DeployPanel<Release[]>>({ status: 'loading' });
  readonly deploymentPanel = signal<DeployPanel<Deployment[]>>({ status: 'loading' });
  readonly pipelinePanel = signal<DeployPanel<PipelineDefinitionResponse>>({ status: 'loading' });
  readonly healthPanel = signal<DeployPanel<EnvironmentHealthResponse>>({ status: 'loading' });
  readonly logPanel = signal<DeployPanel<DeploymentLogLine[]>>({ status: 'loading' });
  readonly actionState = signal<DeployRunActionState>(IDLE);
  readonly selectedRunId = signal<string | null>(null);

  private readonly sources = computed(() => ({
    environments: ready(this.environmentPanel()),
    statuses: ready(this.statusPanel()),
    deployments: ready(this.deploymentPanel()),
    releases: ready(this.releasePanel()),
    health: ready(this.healthPanel()),
  }));
  readonly runs = computed(() => {
    const sources = this.sources();
    return sources.deployments ? buildDeployRuns(sources.deployments, environmentNames(sources), sources.releases) : [];
  });
  readonly cards = computed(() => buildEnvironmentCards(this.sources()));
  /** The overview waits for the AGT-3176 environments, or for AGT-3201 status when those failed. */
  readonly overviewPanel = computed<DeployPanel<unknown>>(() => {
    const environments = this.environmentPanel();
    return environments.status === 'error' && this.statusPanel().status === 'ready' ? this.statusPanel() : environments;
  });
  readonly environmentOptions = computed(() => this.cards().map(card => ({ id: card.id, name: card.name })));
  readonly selectedRun = computed(() => this.runs().find(run => run.id === this.selectedRunId()) ?? null);
  readonly notices = computed(() => {
    const notice = (name: string, panel: DeployPanel<unknown>) => panel.status === 'error' ? [`${name}: ${panel.message}`] : [];
    return [
      ...notice('Environments', this.environmentPanel()),
      ...notice('Environment status', this.statusPanel()), ...notice('Releases', this.releasePanel()),
      ...notice('Deploy runs', this.deploymentPanel()), ...notice('Health', this.healthPanel()),
    ];
  });

  constructor() {
    effect(() => this.view.set(this.initialView()));
    effect(onCleanup => {
      this.load(this.projectName());
      onCleanup(() => {
        this.subscriptions.unsubscribe();
        this.runSubscriptions.unsubscribe();
      });
    });
  }

  show(view: DeployView): void { this.view.set(view); this.viewChange.emit(view); }

  refresh(): void { this.load(this.projectName()); }

  openRun(runId: string): void {
    this.runOpener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.selectedRunId.set(runId);
    this.actionState.set(IDLE);
    this.loadLog(runId);
  }

  closeRun(): void {
    this.runSubscriptions.unsubscribe();
    this.selectedRunId.set(null);
    const opener = this.runOpener;
    this.runOpener = null;
    if (opener?.isConnected) queueMicrotask(() => opener.focus());
  }

  act(action: DeployRunAction): void {
    const runId = this.selectedRunId();
    if (!runId || this.actionState().busy) return;
    const run = this.selectedRun();
    if (!run) return;
    const production = ready(this.statusPanel())?.some(item => item.environment.id === run.environmentId && item.environment.kind === 'production')
      || ready(this.environmentPanel())?.environments.some(item => item.declaration.id === run.environmentId && item.declaration.kind === 'production')
      || run.environmentName.toLowerCase() === 'production';
    if ((action === 'cancel' && run.state === 'running') ||
      (production && (action === 'approve' || action === 'reject'))) {
      const verb = action === 'cancel' ? 'Abort' : action === 'approve' ? 'Approve' : 'Reject';
      if (!window.confirm(`${verb} ${run.environmentName} run ${run.id}?`)) return;
    }
    const project = this.projectName();
    this.actionState.set({ busy: action, error: null });
    this.runSubscriptions.add(this.data.act(project, runId, action).subscribe({
      next: () => {
        this.actionState.set(IDLE);
        this.reloadRuns(project, true);
        this.loadLog(runId);
      },
      error: (error: unknown) => {
        this.actionState.set({ busy: null, error: serverMessage(error) ?? 'The deploy service did not accept the request.' });
        this.reloadRuns(project, true);
      },
    }));
  }

  private load(projectName: string): void {
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.runSubscriptions.unsubscribe();
    this.now.set(this.clock());
    this.selectedRunId.set(null);
    this.subscriptions.add(this.data.environments(projectName).subscribe(panel => this.environmentPanel.set(panel)));
    this.subscriptions.add(this.data.releases(projectName).subscribe(panel => this.releasePanel.set(panel)));
    this.subscriptions.add(this.data.pipeline(projectName).subscribe(panel => this.pipelinePanel.set(panel)));
    this.subscriptions.add(this.data.health(projectName).subscribe(panel => this.healthPanel.set(panel)));
    this.reloadRuns(projectName, false);
  }

  /** Status and runs change together after an approval, rejection, or cancellation. */
  private reloadRuns(projectName: string, keepShown: boolean): void {
    this.now.set(this.clock());
    this.subscriptions.add(this.data.statuses(projectName).subscribe(this.into(this.statusPanel, keepShown)));
    this.subscriptions.add(this.data.deployments(projectName).subscribe(this.into(this.deploymentPanel, keepShown)));
  }

  /** With `keepShown`, ready data stays on screen until the reload answers. */
  private into<T>(target: WritableSignal<DeployPanel<T>>, keepShown: boolean): (panel: DeployPanel<T>) => void {
    return panel => {
      if (!keepShown || panel.status !== 'loading' || target().status !== 'ready') target.set(panel);
    };
  }

  private loadLog(runId: string): void {
    this.runSubscriptions.unsubscribe();
    this.runSubscriptions = new Subscription();
    this.runSubscriptions.add(this.data.deploymentLog(this.projectName(), runId).subscribe(panel => this.logPanel.set(panel)));
  }
}
