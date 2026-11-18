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
import { ProjectEnvironmentOverviewComponent } from '../project-environment-overview/project-environment-overview.component';
import { ProjectOperationsComponent } from '../project-operations/project-operations';
import { OperationsCardLookupService } from '../../state/operations-card-lookup.service';
import { HostedDataPanelComponent } from '../hosted-data-panel/hosted-data-panel';
import { ManagedDatabasesPanelComponent } from '../managed-databases-panel/managed-databases-panel';
import { ProjectSecretsPageComponent } from '../../../secrets/project-secrets-page/project-secrets-page.component';
import { FlowHealthPageComponent, type FlowDetailTarget } from '../../../flow-health';
import type { EnvironmentHealthResponse } from '../../../environment-health/models/environment-health.model';
import type { PipelineDefinitionResponse } from '../../services/project-pipeline-definition.service';
import { ProjectDeployDataService, serverMessage } from '../../services/project-deploy-data.service';
import { ServerCheckoutStore } from '../../../server-checkout/server-checkout.store';
import { LocalOwnerSessionService } from '../../../../services/local-owner-session.service';
import { ProjectDeployEnvironmentActions } from '../../services/project-deploy-environment-actions.service';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { LocalOwnerDialogComponent } from '../../../../components/local-owner-dialog/local-owner-dialog';
import { RollbackOfferComponent } from '../rollback-offer/rollback-offer';
import { DeployRollbackFlow } from '../../state/deploy-rollback-flow';
import { serverCheckoutMissing } from '../../../server-checkout/server-checkout.model';
import type { ProjectEnvironments } from '../../../../models/project-overview.model';
import {
  DEPLOY_CLOCK,
  artifactReleaseLookups,
  buildDeployRuns,
  buildEnvironmentCards,
  environmentNames,
  ready,
  ALL_DEPLOY_RUNS,
  type DeployPanel, type DeployRunAction,
  type Deployment,
  type DeploymentLogLine, type EnvironmentStatus,
  type Release,
  type DeployRunFilter,
  type ArtifactReleaseLookup,
  type ReleaseGateStatus,
} from '../../state/deploy-area.model';
import {
  DEPLOY_LIVE_REFRESH_MS, deployInFlight, deployNotices, deployRunConfirmation, intoPanel, isProductionRun, pageItems,
} from '../../state/deploy-area-reads';
import { DeployAreaSecrets, missingSecretsPrompt } from './deploy-area-secrets';

export type DeployView = 'overview' | 'releases' | 'pipeline' | 'flow' | 'history' | 'steps' | 'runbooks' | 'operations' | 'secrets' | 'tools';

const IDLE: DeployRunActionState = { busy: null, error: null };

@Component({
  selector: 'app-project-deploy-area',
  standalone: true,
  providers: [ProjectDeployEnvironmentActions, DeployRollbackFlow],
  imports: [
    ProjectDeployOverviewComponent, ProjectDeployReleasesComponent, ProjectDeployPipelineComponent,
    ProjectDeployHistoryComponent, DeployRunDrawerComponent, ProjectDeploymentPanelComponent,
    ProjectPipelinePanelComponent, EnvironmentHealthPanelComponent, ProjectEnvironmentOverviewComponent,
    ProjectOperationsComponent, HostedDataPanelComponent, ManagedDatabasesPanelComponent, ProjectSecretsPageComponent,
    FlowHealthPageComponent, LocalOwnerDialogComponent, PendingButtonDirective, RollbackOfferComponent,
  ],
  templateUrl: './project-deploy-area.html',
  styleUrl: './project-deploy-area.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployAreaComponent {
  private readonly data = inject(ProjectDeployDataService);
  private readonly serverCheckout = inject(ServerCheckoutStore);
  private readonly cardLookup = inject(OperationsCardLookupService);
  private readonly clock = inject(DEPLOY_CLOCK);
  private readonly localOwner = inject(LocalOwnerSessionService);
  private readonly environmentActions = inject(ProjectDeployEnvironmentActions);
  private subscriptions = new Subscription();
  private runReads = new Subscription();
  private releaseReads = new Subscription();
  private logReads = new Subscription();
  private runFetch = new Subscription();
  private artifactReads = new Subscription();
  private mutations = new Subscription();
  private cardReads = new Subscription();
  private liveReads = new Subscription();
  private readGeneration = 0;
  private releaseGeneration = 0;
  private logGeneration = 0;
  private mutationAction: DeployRunAction | null = null;
  private runOpener: HTMLElement | null = null;
  private openedTarget: { project: string; id: string } | null = null;
  readonly projectName = input.required<string>();
  readonly checkoutMissing = computed(() => {
    const project = this.serverCheckout.projectFor({ name: this.projectName() });
    if (!project || !serverCheckoutMissing(project)) return false;
    const state = this.serverCheckout.statusFor(project.id)?.state;
    return state !== 'present' && state !== 'ready';
  });
  readonly releaseTarget = input<{ project: string; id: string } | null>(null);
  readonly initialView = input<DeployView>('overview');
  readonly focusStepId = input<string | undefined>();
  readonly openPrompts = output<void>();
  readonly openTask = output<{ jobId: string; watchPath: string }>();
  readonly viewChange = output<DeployView>();
  /** A flow-health block opened its detail page on a Project rail outside Deploy. */
  readonly openRail = output<'cycle-time' | 'overview'>();
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
  readonly releaseGatePanel = signal<DeployPanel<ReleaseGateStatus>>({ status: 'loading' });
  /** F2: a refused release attempt shows its reason and next step here, not only in the API answer. */
  readonly releaseRefusal = computed(() => ready(this.releaseGatePanel())?.refusal ?? null);
  readonly logPanel = signal<DeployPanel<DeploymentLogLine[]>>({ status: 'loading' });
  readonly actionState = signal<DeployRunActionState>(IDLE);
  readonly environmentActionMessage = this.environmentActions.message;
  readonly environmentActionError = this.environmentActions.error;
  readonly environmentActionBusy = this.environmentActions.busy;
  readonly pendingRemovals = computed(() => (ready(this.environmentPanel())?.runTargetRemovals ?? [])
    .filter(item => item.status === 'pending-confirmation'));
  readonly selectedRunId = signal<string | null>(null);
  readonly highlightedReleaseId = signal<string | null>(null);
  readonly secrets = new DeployAreaSecrets(this.data, this); // F14: secret gaps for the cards and the run drawer
  openRelease(id: string): void {
    this.highlightedReleaseId.set(id);
    this.show('releases');
    this.releaseReads.unsubscribe();
    this.releaseReads = new Subscription();
    const generation = ++this.releaseGeneration;
    this.releaseReads.add(this.data.releasePageContaining(this.projectName(), id).subscribe(panel => {
      if (generation !== this.releaseGeneration || this.highlightedReleaseId() !== id) return;
      if (panel.status === 'ready') {
        this.releaseOffset.set(panel.value.offset);
        this.releaseTotal.set(panel.value.total);
      }
      this.releasePanel.set(pageItems(panel));
    }));
  }

  /** A later card click replaces a lookup that has not answered yet. */
  openCard(key: string): void {
    this.cardReads.unsubscribe();
    this.cardReads = this.cardLookup.resolve(this.projectName(), key).subscribe(card => this.openTask.emit(card));
  }
  /** A selected run that neither a loaded page nor `/deploy/status` contains, read by id. */
  readonly fetchedRun = signal<DeployPanel<Deployment> | null>(null);
  /** Releases of host artifacts that status does not name, by lookup key (project-scoped). */
  readonly artifactReleases = signal<ReadonlyMap<string, DeployPanel<Release | null>>>(new Map());
  private readonly sources = computed(() => ({
    environments: ready(this.environmentPanel()),
    statuses: ready(this.statusPanel()),
    deployments: ready(this.deploymentPanel()),
    releases: ready(this.releasePanel()),
    health: ready(this.healthPanel()),
    artifactReleases: this.artifactReleases(),
  }));
  /** Release versions for runs: the visible page, the releases status names, and resolved host artifacts. */
  private readonly knownReleases = computed(() => {
    const statusReleases = (ready(this.statusPanel()) ?? []).flatMap(item => [item.currentRelease, item.newerRelease]);
    const resolved = [...this.artifactReleases().values()].map(ready);
    return [...(ready(this.releasePanel()) ?? []), ...statusReleases, ...resolved].filter((item): item is Release => !!item);
  });
  readonly runs = computed(() => {
    const deployments = this.sources().deployments;
    return deployments ? buildDeployRuns(deployments, environmentNames(this.sources()), this.knownReleases()) : [];
  });
  readonly historyRuns = computed(() => buildDeployRuns(this.historyDeployments(), environmentNames(this.sources()), this.knownReleases()));
  /** The latest run per environment from `/deploy/status`, which a bounded page can miss. */
  private readonly statusRuns = computed(() => buildDeployRuns(
    (ready(this.statusPanel()) ?? []).flatMap(item => item.currentDeployment ? [item.currentDeployment] : []),
    environmentNames(this.sources()), this.knownReleases()));
  private readonly fetchedRuns = computed(() => {
    const panel = this.fetchedRun();
    const fetched = panel ? ready(panel) : null;
    return fetched ? buildDeployRuns([fetched], environmentNames(this.sources()), this.knownReleases()) : [];
  });
  readonly cards = computed(() => buildEnvironmentCards(this.sources()).map(card => ({ ...card, secrets: this.secrets.forEnvironment(card.id) })));
  readonly pipelineDefinition = computed(() => ready(this.pipelinePanel()));
  readonly inFlight = computed(() => deployInFlight([...this.runs(), ...this.statusRuns()], ready(this.environmentPanel())));
  /** The overview waits for the AGT-3176 environments, or for AGT-3201 status when those failed. */
  readonly overviewPanel = computed<DeployPanel<unknown>>(() => {
    const environments = this.environmentPanel();
    return environments.status === 'error' && this.statusPanel().status === 'ready' ? this.statusPanel() : environments;
  });
  readonly environmentOptions = computed(() => this.cards().map(card => ({ id: card.id, name: card.name })));
  readonly selectedRun = computed(() => [...this.runs(), ...this.historyRuns(), ...this.statusRuns(), ...this.fetchedRuns()]
    .find(run => run.id === this.selectedRunId()) ?? null);
  readonly rollback = inject(DeployRollbackFlow).attach(this); // F13: one confirmed lifecycle rollback behind every entry point
  readonly notices = computed(() => deployNotices([
    ['Environments', this.environmentPanel()], ['Environment status', this.statusPanel()], ['Releases', this.releasePanel()],
    ['Deploy runs', this.deploymentPanel()], ['Health', this.healthPanel()],
    ...[...this.artifactReleases().values()].map(panel => ['Release lookup', panel] as const),
    ...(this.fetchedRun() ? [['Deploy run', this.fetchedRun()!] as const] : []),
  ]));

  constructor() {
    effect(() => this.view.set(this.initialView()));
    effect(onCleanup => {
      const project = this.projectName();
      const missingCheckout = this.checkoutMissing();
      if (!missingCheckout) untracked(() => this.load(project, true));
      onCleanup(() => {
        for (const reads of [this.subscriptions, this.runReads, this.releaseReads, this.logReads, this.cardReads, this.liveReads,
          this.runFetch, this.artifactReads]) reads.unsubscribe();
        this.artifactReads = new Subscription();
        this.mutations.unsubscribe(); this.mutations = new Subscription();
        this.mutationAction = null;
      });
    });
    effect(onCleanup => {
      const project = this.projectName();
      if (!this.inFlight() || this.checkoutMissing()) return;
      const timer = setInterval(() => { if (!this.mutationAction) this.refreshLive(project); }, DEPLOY_LIVE_REFRESH_MS);
      onCleanup(() => clearInterval(timer));
    });
    // A new target on the same project only opens its release page; the other sources stay loaded.
    effect(() => {
      const target = this.releaseTarget();
      untracked(() => {
        if (target && target !== this.openedTarget && target.project === this.projectName()) this.openTargetRelease(target);
      });
    });
    this.watchArtifactReleases();
  }
  private openTargetRelease(target: { project: string; id: string }): void {
    this.openedTarget = target;
    this.openRelease(target.id);
  }
  private watchArtifactReleases(): void {
    effect(() => {
      // Without AGT-3201 (status route missing) there are no release records to look up.
      const status = this.statusPanel().status;
      if (status === 'loading' || status === 'unavailable') return;
      const lookups = artifactReleaseLookups({ environments: ready(this.environmentPanel()), statuses: ready(this.statusPanel()) });
      untracked(() => this.resolveArtifacts(lookups));
    });
  }
  show(view: DeployView): void {
    if (!this.checkoutMissing() && view !== 'history' && this.historyFilter() !== ALL_DEPLOY_RUNS) {
      this.historyFilter.set(ALL_DEPLOY_RUNS);
      this.historyOffset.set(0);
      this.reloadRuns(this.projectName(), false);
    }
    this.view.set(view);
    this.viewChange.emit(view);
  }
  refresh(): void { if (!this.checkoutMissing()) this.load(this.projectName(), false); }
  stopEnvironment(id: string): void { this.environmentActions.stop(this.projectName(),
    this.cards().find(item => item.id === id), () => this.refresh()); }
  removeEnvironment(id: string): void { this.environmentActions.remove(this.projectName(),
    this.cards().find(item => item.id === id)); }
  confirmRemoval(id: string, hostId: string): void { this.environmentActions.confirm(this.projectName(), id, hostId, () => this.refresh()); }
  openFlowDetail(target: FlowDetailTarget): void {
    if (target === 'steps') this.show('steps');
    else this.openRail.emit(target);
  }
  openRun(runId: string): void {
    this.runOpener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.selectedRunId.set(runId);
    this.actionState.set({ busy: this.mutationAction, error: null });
    this.fetchRunIfUnknown(runId);
    this.loadLog(runId);
  }
  closeRun(): void {
    this.logReads.unsubscribe();
    this.runFetch.unsubscribe();
    this.logGeneration++;
    this.selectedRunId.set(null);
    this.fetchedRun.set(null);
    const opener = this.runOpener;
    this.runOpener = null;
    if (opener?.isConnected) queueMicrotask(() => opener.focus());
  }
  act(action: DeployRunAction, reason?: string): void {
    const runId = this.selectedRunId();
    if (!runId || this.mutationAction) return;
    const run = this.selectedRun();
    if (!run) return;
    const production = isProductionRun(run, ready(this.statusPanel()), ready(this.environmentPanel()));
    if ((action === 'cancel' && run.state === 'running') || action === 'abort' ||
      (production && (action === 'approve' || action === 'reject')) || this.secrets.approvalGap(action).length) {
      // The server lets only a signed-in Owner or Maintainer decide production; ask for the account first.
      if (production && this.localOwner.missing() && (action === 'approve' || action === 'reject'))
        return this.localOwner.run('Production approval needs the owner account.', () => this.act(action, reason));
      if (!window.confirm(`${deployRunConfirmation(action, run, this.knownReleases())}${missingSecretsPrompt(this.secrets.approvalGap(action))}`)) return;
    }
    const project = this.projectName();
    this.mutationAction = action;
    this.runReads.unsubscribe();
    this.readGeneration++;
    this.actionState.set({ busy: action, error: null });
    this.mutations.add(this.data.act(project, runId, action, reason, { confirmMissingSecrets: this.secrets.approvalGap(action).length > 0 }).subscribe({
      next: () => {
        this.mutationAction = null;
        this.actionState.set(IDLE);
        this.refreshLive(project);
        if (this.selectedRunId() === runId) this.loadLog(runId);
      },
      error: (error: unknown) => {
        this.mutationAction = null;
        this.actionState.set(this.selectedRunId() === runId
          ? { busy: null, error: serverMessage(error) ?? 'The deploy service did not accept the request.' } : IDLE);
        this.refreshLive(project);
      },
    }));
  }

  /** F17: an action or a host report changes the cards too: environments, health, status, and runs are read again. */
  private refreshLive(projectName: string): void {
    this.liveReads.unsubscribe(); this.liveReads = new Subscription();
    this.liveReads.add(this.data.environments(projectName).subscribe(intoPanel(this.environmentPanel, true)));
    this.liveReads.add(this.data.health(projectName).subscribe(intoPanel(this.healthPanel, true)));
    this.reloadRuns(projectName, true);
    const runId = this.selectedRunId();
    if (runId) this.refetchRun(runId);
  }
  private load(projectName: string, resetSelection: boolean): void {
    this.liveReads.unsubscribe();
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.now.set(this.clock());
    if (resetSelection) {
      // Nothing of the previous project may show under the new one, not even while it loads.
      this.selectedRunId.set(null);
      this.fetchedRun.set(null);
      this.actionState.set(IDLE);
      this.historyFilter.set(ALL_DEPLOY_RUNS);
      this.historyOffset.set(0);
      this.historyDeployments.set([]);
      this.historyTotal.set(0);
      this.releaseOffset.set(0);
      this.releaseTotal.set(0);
      this.artifactReleases.set(new Map());
      for (const target of [this.environmentPanel, this.statusPanel, this.releasePanel, this.deploymentPanel, this.historyPanel, this.releaseGatePanel] as const)
        (target as WritableSignal<DeployPanel<unknown>>).set({ status: 'loading' });
    } else {
      // Refresh retries failed lookups; resolved release records do not change.
      this.artifactReleases.set(new Map([...this.artifactReleases()].filter(([, panel]) => panel.status === 'ready' || panel.status === 'loading')));
    }
    this.subscriptions.add(this.data.environments(projectName).subscribe(panel => this.environmentPanel.set(panel)));
    const target = this.releaseTarget();
    if (resetSelection && target?.project === projectName) this.openTargetRelease(target);
    else this.loadReleases(projectName, !resetSelection);
    this.subscriptions.add(this.data.pipeline(projectName).subscribe(panel => this.pipelinePanel.set(panel)));
    this.subscriptions.add(this.data.health(projectName).subscribe(panel => this.healthPanel.set(panel)));
    this.subscriptions.add(this.data.releaseGate(projectName).subscribe(panel => this.releaseGatePanel.set(panel)));
    this.reloadRuns(projectName, false);
    this.secrets.load(projectName, resetSelection);
  }

  /** Status and runs change together after an approval, rejection, or cancellation. */
  private reloadRuns(projectName: string, keepShown: boolean): void {
    this.now.set(this.clock());
    this.runReads.unsubscribe();
    this.runReads = new Subscription();
    const generation = ++this.readGeneration;
    this.runReads.add(this.data.statuses(projectName).subscribe(panel => {
      if (generation === this.readGeneration) intoPanel(this.statusPanel, keepShown)(panel);
    }));
    this.runReads.add(this.data.deploymentPage(projectName, 0).subscribe(panel => {
      if (generation !== this.readGeneration) return;
      intoPanel(this.deploymentPanel, keepShown)(pageItems(panel));
    }));
    this.runReads.add(this.data.deploymentPage(projectName, this.historyOffset(), this.historyFilter(), this.now()).subscribe(panel => {
      if (generation !== this.readGeneration) return;
      if (panel.status === 'ready') {
        this.historyDeployments.set(panel.value.items);
        this.historyTotal.set(panel.value.total);
      }
      intoPanel(this.historyPanel, keepShown)(pageItems(panel));
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
      intoPanel(this.releasePanel, keepShown)(pageItems(panel));
    }));
  }

  /** Resolves each host artifact once per project; answers for another project are dropped. */
  private resolveArtifacts(lookups: readonly ArtifactReleaseLookup[]): void {
    const project = this.projectName();
    for (const lookup of lookups.filter(item => !this.artifactReleases().has(item.key))) {
      this.artifactReleases.update(map => new Map(map).set(lookup.key, { status: 'loading' }));
      this.artifactReads.add(this.data.releaseForArtifact(project, lookup.artifact, lookup.notAfterUtc).subscribe(panel => {
        if (project === this.projectName()) this.artifactReleases.update(map => new Map(map).set(lookup.key, panel));
      }));
    }
  }

  private fetchRunIfUnknown(runId: string): void {
    this.runFetch.unsubscribe();
    this.fetchedRun.set(null);
    if (this.selectedRun()) return;
    this.refetchRun(runId);
  }

  /** Reads the selected run by id when only a by-id read supplied it. */
  private refetchRun(runId: string): void {
    if (this.selectedRunId() !== runId || (this.selectedRun() && !this.fetchedRun())) return;
    const project = this.projectName();
    this.runFetch.unsubscribe();
    this.runFetch = this.data.deployment(project, runId).subscribe(panel => {
      if (this.selectedRunId() !== runId || project !== this.projectName()) return;
      if (panel.status !== 'loading' || this.fetchedRun()?.status !== 'ready') this.fetchedRun.set(panel);
    });
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
