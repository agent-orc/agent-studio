import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked, type WritableSignal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ProjectDeploymentPanelComponent } from '../project-deployment-panel/project-deployment-panel.component';
import { ProjectDeployOverviewComponent } from '../project-deploy-overview/project-deploy-overview';
import { ProjectDeployReleasesComponent } from '../project-deploy-releases/project-deploy-releases';
import { ProjectDeployPipelineComponent } from '../project-deploy-pipeline/project-deploy-pipeline';
import { ProjectDeployHistoryComponent } from '../project-deploy-history/project-deploy-history';
import { DeployRunDrawerComponent, type DeployRunActionState } from '../deploy-run-drawer/deploy-run-drawer';
import { ProjectPipelinePanelComponent } from '../project-pipeline-panel/project-pipeline-panel.component';
import { EnvironmentHealthPanelComponent } from '../../../environment-health';
import { ProjectSecretsPageComponent } from '../../../secrets/project-secrets-page/project-secrets-page.component';
import type { EnvironmentHealthResponse } from '../../../environment-health/models/environment-health.model';
import type { PipelineDefinitionResponse } from '../../services/project-pipeline-definition.service';
import { ProjectDeployDataService, serverMessage, type DeployPage } from '../../services/project-deploy-data.service';
import type { ProjectEnvironments } from '../../../../models/project-overview.model';
import {
  DEPLOY_CLOCK,
  buildDeployRuns,
  buildEnvironmentCards,
  environmentNames,
  ready,
  ALL_DEPLOY_RUNS,
  type DeployPanel,
  type DeployRunAction,
  type Deployment,
  type DeploymentLogLine,
  type EnvironmentStatus,
  type Release,
  type DeployRunFilter,
} from '../../state/deploy-area.model';

export type DeployView = 'overview' | 'releases' | 'pipeline' | 'history' | 'steps' | 'operations' | 'secrets' | 'tools';

const IDLE: DeployRunActionState = { busy: null, error: null };

@Component({
  selector: 'app-project-deploy-area',
  standalone: true,
  imports: [
    ProjectDeployOverviewComponent, ProjectDeployReleasesComponent, ProjectDeployPipelineComponent,
    ProjectDeployHistoryComponent, DeployRunDrawerComponent, ProjectDeploymentPanelComponent,
    ProjectPipelinePanelComponent, EnvironmentHealthPanelComponent, ProjectSecretsPageComponent,
  ],
  templateUrl: './project-deploy-area.html',
  styleUrl: './project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployAreaComponent {
  private readonly data = inject(ProjectDeployDataService);
  private readonly clock = inject(DEPLOY_CLOCK);
  private subscriptions = new Subscription();
  private runReads = new Subscription();
  private releaseReads = new Subscription();
  private logReads = new Subscription();
  private mutations = new Subscription();
  private readGeneration = 0;
  private releaseGeneration = 0;
  private logGeneration = 0;
  private mutationAction: DeployRunAction | null = null;
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
  readonly historyPanel = signal<DeployPanel<Deployment[]>>({ status: 'loading' });
  readonly historyDeployments = signal<Deployment[]>([]);
  readonly historyTotal = signal(0);
  readonly historyOffset = signal(0);
  readonly historyFilter = signal<DeployRunFilter>(ALL_DEPLOY_RUNS);
  readonly releaseTotal = signal(0);
  readonly releaseOffset = signal(0);
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
  readonly historyRuns = computed(() => buildDeployRuns(this.historyDeployments(), environmentNames(this.sources()), ready(this.releasePanel())));
  readonly statusRuns = computed(() => {
    const sources = this.sources();
    return buildDeployRuns((sources.statuses ?? []).flatMap(status => status.currentDeployment ? [status.currentDeployment] : []),
      environmentNames(sources), sources.releases);
  });
  readonly cards = computed(() => buildEnvironmentCards(this.sources()));
  /** The overview waits for the AGT-3176 environments, or for AGT-3201 status when those failed. */
  readonly overviewPanel = computed<DeployPanel<unknown>>(() => {
    const environments = this.environmentPanel();
    return environments.status === 'error' && this.statusPanel().status === 'ready' ? this.statusPanel() : environments;
  });
  readonly environmentOptions = computed(() => this.cards().map(card => ({ id: card.id, name: card.name })));
  readonly selectedRun = computed(() => [...this.runs(), ...this.historyRuns(), ...this.statusRuns()]
    .find(run => run.id === this.selectedRunId()) ?? null);
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
      const project = this.projectName();
      untracked(() => this.load(project, true));
      onCleanup(() => {
        this.subscriptions.unsubscribe();
        this.runReads.unsubscribe();
        this.releaseReads.unsubscribe();
        this.logReads.unsubscribe();
        this.mutations.unsubscribe();
        this.mutations = new Subscription();
        this.mutationAction = null;
      });
    });
  }

  show(view: DeployView): void {
    if (view !== 'history' && this.historyFilter() !== ALL_DEPLOY_RUNS) {
      this.historyFilter.set(ALL_DEPLOY_RUNS);
      this.historyOffset.set(0);
      this.reloadRuns(this.projectName(), false);
    }
    this.view.set(view);
    this.viewChange.emit(view);
  }

  refresh(): void { this.load(this.projectName(), false); }

  openRun(runId: string): void {
    this.runOpener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.selectedRunId.set(runId);
    this.actionState.set({ busy: this.mutationAction, error: null });
    this.loadLog(runId);
  }

  closeRun(): void {
    this.logReads.unsubscribe();
    this.logGeneration++;
    this.selectedRunId.set(null);
    const opener = this.runOpener;
    this.runOpener = null;
    if (opener?.isConnected) queueMicrotask(() => opener.focus());
  }

  act(action: DeployRunAction, reason?: string): void {
    const runId = this.selectedRunId();
    if (!runId || this.mutationAction) return;
    const run = this.selectedRun();
    if (!run) return;
    const production = ready(this.statusPanel())?.some(item => item.environment.id === run.environmentId && item.environment.kind === 'production')
      || ready(this.environmentPanel())?.environments.some(item => item.declaration.id === run.environmentId && item.declaration.kind === 'production')
      || run.environmentName.toLowerCase() === 'production';
    if ((action === 'cancel' && run.state === 'running') || action === 'abort' ||
      (production && (action === 'approve' || action === 'reject'))) {
      const verb = action === 'cancel' || action === 'abort' ? 'Abort' : action === 'approve' ? 'Approve' : 'Reject';
      if (!window.confirm(`${verb} ${run.environmentName} run ${run.id}?`)) return;
    }
    const project = this.projectName();
    this.mutationAction = action;
    this.runReads.unsubscribe();
    this.readGeneration++;
    this.actionState.set({ busy: action, error: null });
    this.mutations.add(this.data.act(project, runId, action, reason).subscribe({
      next: () => {
        this.mutationAction = null;
        this.actionState.set(IDLE);
        this.reloadRuns(project, true);
        if (this.selectedRunId() === runId) this.loadLog(runId);
      },
      error: (error: unknown) => {
        this.mutationAction = null;
        this.actionState.set(this.selectedRunId() === runId
          ? { busy: null, error: serverMessage(error) ?? 'The deploy service did not accept the request.' } : IDLE);
        this.reloadRuns(project, true);
      },
    }));
  }

  private load(projectName: string, resetSelection: boolean): void {
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.now.set(this.clock());
    if (resetSelection) {
      this.selectedRunId.set(null);
      this.actionState.set(IDLE);
      this.historyFilter.set(ALL_DEPLOY_RUNS);
      this.historyOffset.set(0);
      this.releaseOffset.set(0);
    }
    this.subscriptions.add(this.data.environments(projectName).subscribe(panel => this.environmentPanel.set(panel)));
    this.loadReleases(projectName, true);
    this.subscriptions.add(this.data.pipeline(projectName).subscribe(panel => this.pipelinePanel.set(panel)));
    this.subscriptions.add(this.data.health(projectName).subscribe(panel => this.healthPanel.set(panel)));
    this.reloadRuns(projectName, false);
  }

  /** Status and runs change together after an approval, rejection, or cancellation. */
  private reloadRuns(projectName: string, keepShown: boolean): void {
    this.now.set(this.clock());
    this.runReads.unsubscribe();
    this.runReads = new Subscription();
    const generation = ++this.readGeneration;
    this.runReads.add(this.data.statuses(projectName).subscribe(panel => {
      if (generation === this.readGeneration) this.into(this.statusPanel, keepShown)(panel);
    }));
    this.runReads.add(this.data.deploymentPage(projectName, 0).subscribe(panel => {
      if (generation !== this.readGeneration) return;
      this.into(this.deploymentPanel, keepShown)(this.items(panel));
    }));
    this.runReads.add(this.data.deploymentPage(projectName, this.historyOffset(), this.historyFilter(), this.now()).subscribe(panel => {
      if (generation !== this.readGeneration) return;
      if (panel.status === 'ready') {
        this.historyDeployments.set(panel.value.items);
        this.historyTotal.set(panel.value.total);
      }
      this.into(this.historyPanel, keepShown)(this.items(panel));
    }));
  }

  changeHistoryFilter(filter: DeployRunFilter): void {
    this.historyFilter.set(filter);
    this.historyOffset.set(0);
    this.reloadRuns(this.projectName(), false);
  }

  changeHistoryPage(offset: number): void {
    this.historyOffset.set(offset);
    this.reloadRuns(this.projectName(), false);
  }

  changeReleasePage(offset: number): void {
    this.releaseOffset.set(offset);
    this.loadReleases(this.projectName(), false);
  }

  private loadReleases(projectName: string, keepShown: boolean): void {
    this.releaseReads.unsubscribe();
    this.releaseReads = new Subscription();
    const generation = ++this.releaseGeneration;
    this.releaseReads.add(this.data.releasePage(projectName, this.releaseOffset()).subscribe(panel => {
      if (generation !== this.releaseGeneration) return;
      if (panel.status === 'ready') this.releaseTotal.set(panel.value.total);
      this.into(this.releasePanel, keepShown)(this.items(panel));
    }));
  }

  private items<T>(panel: DeployPanel<DeployPage<T>>): DeployPanel<T[]> {
    return panel.status === 'ready' ? { status: 'ready', value: panel.value.items } : panel;
  }

  /** With `keepShown`, ready data stays on screen until the reload answers. */
  private into<T>(target: WritableSignal<DeployPanel<T>>, keepShown: boolean): (panel: DeployPanel<T>) => void {
    return panel => {
      if (!keepShown || panel.status !== 'loading' || target().status !== 'ready') target.set(panel);
    };
  }

  private loadLog(runId: string): void {
    this.logReads.unsubscribe();
    this.logReads = new Subscription();
    const generation = ++this.logGeneration;
    this.logReads.add(this.data.deploymentLog(this.projectName(), runId).subscribe(panel => {
      if (generation === this.logGeneration && this.selectedRunId() === runId) this.logPanel.set(panel);
    }));
  }
}
