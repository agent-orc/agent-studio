import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { TaskService } from '../../../../services/task.service';
import type { ProjectDeploymentSummary, ProjectDeploymentTarget, ProjectEnvironments } from '../../../../models/project-overview.model';
import { VisibleCliTaskService, type VisibleCliTaskCreated } from '../../../visible-cli-task';
import type { WatchPathEntry } from '../../../../models/task.model';
import { DeploymentDefinitionEditorComponent } from '../deployment-definition-editor/deployment-definition-editor';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { TARGET_READINESS, deploymentHistoryTone, type TargetReadiness } from './deployment-status-tone';
import { EnvironmentStateComponent } from '../environment-state/environment-state';
import { RollbackOfferComponent } from '../rollback-offer/rollback-offer';
import type { RollbackOffer } from '../../state/rollback-offer.model';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { DEPLOY_CLOCK, shortArtifact, type EnvironmentCardView } from '../../state/deploy-area.model';

@Component({
  selector: 'app-project-deployment-panel',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, PendingButtonDirective, DeploymentDefinitionEditorComponent, StatusIndicatorComponent,
    EnvironmentStateComponent, RollbackOfferComponent],
  templateUrl: './project-deployment-panel.component.html',
  styleUrl: './project-deployment-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeploymentPanelComponent {
  private readonly tasks = inject(TaskService);
  private readonly cliTasks = inject(VisibleCliTaskService);
  private readonly deployData = inject(ProjectDeployDataService);
  private readonly clock = inject(DEPLOY_CLOCK);
  private environmentReads = new Subscription();

  readonly projectName = input.required<string>();
  /**
   * F13: rollback runs only through the lifecycle offer of the Deploy area,
   * which names the target and asks first; this panel has no rollback call.
   */
  readonly rollbackOffers = input<readonly RollbackOffer[]>([]);
  readonly rollbackBusy = input<string | null>(null);
  /** The deploy area's clock; ages of the environment states read against it too. */
  readonly now = input<number>(this.clock());
  readonly rollback = output<string>();
  readonly offers = computed(() => new Map(this.rollbackOffers().map(offer => [offer.environmentId, offer])));
  readonly summary = signal<ProjectDeploymentSummary | null>(null);
  readonly environments = signal<ProjectEnvironments | null>(null);
  /** Health, release and age per environment id, from the one environment state projection. */
  readonly environmentStates = signal<ReadonlyMap<string, EnvironmentCardView>>(new Map());
  readonly environmentError = signal<string | null>(null);
  /** The promotion whose decision is in flight. */
  readonly pendingEnvironmentAction = signal<string | null>(null);
  readonly environmentBusy = computed(() => this.pendingEnvironmentAction() !== null);
  readonly loading = signal(true);
  readonly requestFailed = signal(false);
  readonly workspaces = signal<WatchPathEntry[]>([]);
  readonly selectedTarget = signal<ProjectDeploymentTarget | null>(null);
  readonly parameterValues = signal<Record<string, string | boolean>>({});
  readonly running = signal(false);
  readonly runError = signal<string | null>(null);
  readonly createdTask = signal<VisibleCliTaskCreated | null>(null);
  readonly visibleExecution = signal(true);
  readonly revisionMode = signal<'tested' | 'head'>('head');
  readonly headExceptionReason = signal('');

  constructor() {
    effect(() => this.load(this.projectName()));
    inject(DestroyRef).onDestroy(() => this.environmentReads.unsubscribe());
  }

  refresh(): void {
    this.load(this.projectName());
  }

  decidePromotion(artifact: string, approve: boolean): void {
    if (this.environmentBusy()) return;
    this.environmentError.set(null);
    this.pendingEnvironmentAction.set(`${approve ? 'approve' : 'reject'}:${artifact}`);
    this.tasks.decideProjectPromotion(this.projectName(), artifact, approve).subscribe({
      next: () => { this.pendingEnvironmentAction.set(null); this.loadEnvironments(this.projectName()); },
      error: () => { this.pendingEnvironmentAction.set(null); this.environmentError.set('Promotion could not be updated.'); },
    });
  }

  chooseTarget(target: ProjectDeploymentTarget): void {
    this.selectedTarget.set(target);
    this.parameterValues.set(Object.fromEntries(target.parameters.map(parameter => [
      parameter.name,
      typeof parameter.default === 'boolean' || typeof parameter.default === 'string'
        ? parameter.default
        : parameter.type === 'boolean' ? false : '',
    ])));
    this.createdTask.set(null);
    this.runError.set(null);
    this.visibleExecution.set(true);
  }

  setParameter(name: string, value: string | boolean): void {
    this.parameterValues.update(current => ({ ...current, [name]: value }));
  }

  canRun(target: ProjectDeploymentTarget): boolean {
    if (target.parameters.some(parameter => parameter.type === 'secret-ref')) return false;
    const values = this.parameterValues();
    const revisionReady = this.revisionMode() === 'tested'
      ? !!this.summary()?.defaultEvidenceRun
      : this.headExceptionReason().trim().length > 0;
    return revisionReady && this.visibleExecution() && !this.createdTask() && target.runnable && !!target.command && target.parameters.every(parameter =>
      !parameter.required || (this.isStableIdle(parameter.name)
        ? values[parameter.name] === true
        : parameter.type === 'boolean'
          ? typeof values[parameter.name] === 'boolean'
          : String(values[parameter.name] ?? '').trim().length > 0));
  }

  isStableIdle(name: string): boolean {
    return name === 'stableIdle';
  }

  hasValueParameters(target: ProjectDeploymentTarget): boolean {
    return target.parameters.some(parameter => !this.isStableIdle(parameter.name));
  }

  hasSecretRefs(target: ProjectDeploymentTarget): boolean {
    return target.parameters.some(parameter => parameter.type === 'secret-ref');
  }

  targetEnvironment(target: ProjectDeploymentTarget): string {
    if (target.template === 'deploy-stable') return 'Stable environment';
    return target.targetHostId || 'Repository environment';
  }

  readonly historyTone = deploymentHistoryTone;
  /** Digests are secondary detail: short form, full value on hover. */
  readonly shortArtifact = shortArtifact;

  targetReadiness(target: ProjectDeploymentTarget): { tone: StatusTone; label: string } {
    let readiness: TargetReadiness;
    if (target.template === 'deploy-stable') {
      readiness = this.parameterValues()['stableIdle'] === true ? 'idle-confirmed' : 'idle-required';
    } else {
      readiness = target.runnable ? 'ready' : 'setup-required';
    }
    return TARGET_READINESS[readiness];
  }

  parameterLabel(name: string): string {
    if (name === 'stableIdle') return 'Require the stable environment to be idle before deployment';
    return humanize(name);
  }

  parameterHelp(name: string): string {
    if (name === 'stableIdle') return 'Prevents the deployment from interrupting active stable work.';
    return `Value used by the repository deployment command for ${humanize(name).toLowerCase()}.`;
  }

  runSelected(): void {
    const target = this.selectedTarget();
    const workspace = this.workspaces().find(item => item.name.toLowerCase() === this.projectName().toLowerCase());
    if (!target || !workspace || !this.canRun(target) || this.running()) return;
    const values = this.parameterValues();
    const command = target.command!.replace(/\{\{([A-Za-z][A-Za-z0-9_-]*)\}\}/g, (_, name: string) => shellValue(values[name]));
    const evidence = this.summary()?.defaultEvidenceRun;
    const tested = this.revisionMode() === 'tested' && evidence;
    const deploymentCommit = tested ? evidence.commit : 'HEAD';
    const evidencePrompt = tested
      ? `Deploy exactly commit \`${evidence.commit}\`, justified by successful test run \`${evidence.id}\`. If the target command would deploy another revision, stop instead of falling forward to HEAD.`
      : `This is an explicit HEAD deployment exception. Record the resolved HEAD and this justification before running: ${this.headExceptionReason().trim()}`;
    this.running.set(true);
    this.runError.set(null);
    this.cliTasks.start({
      title: `Deploy ${target.title}`,
      scope: `Deploy ${target.title}`,
      reason: target.summary,
      command,
      prompt: [
        `Run deployment target \`${target.id}\` using the exact command in the execution contract.`,
        evidencePrompt,
        'Report preflight checks, each command outcome, the deployed revision, and the final health check in this task conversation.',
        'Do not request or copy secrets. Any secret reference must resolve on the execution host.',
      ].join('\n\n'),
      context: {
        deploymentTarget: target.id,
        source: target.source,
        deploymentCommit,
        testRunId: tested ? evidence.id : 'HEAD_EXCEPTION',
        distanceToHead: tested ? String(evidence.distanceToHead ?? 'unknown') : '0',
        headDirection: tested ? evidence.headDirection : 'exact',
        headExceptionReason: tested ? '' : this.headExceptionReason().trim(),
        ...Object.fromEntries(Object.entries(values).map(([key, value]) => [key, String(value)])),
      },
      cliType: 'codex',
    }, workspace.path).subscribe({
      next: task => this.createdTask.set(task),
      error: error => {
        this.runError.set(error?.error?.message ?? 'The deployment task could not be created.');
        this.running.set(false);
      },
      complete: () => this.running.set(false),
    });
  }

  shortSha(sha: string): string {
    return sha.slice(0, 8);
  }

  headDistanceLabel(distance: number | null, direction: string): string {
    if (direction === 'exact') return 'matches Head';
    if (direction === 'diverged') return 'diverged from Head';
    if (distance === null) return 'Head distance unknown';
    const commits = `${distance} commit${distance === 1 ? '' : 's'}`;
    return direction === 'head-behind'
      ? `${commits} ahead of Head`
      : `Head is ${commits} ahead`;
  }

  private load(projectName: string): void {
    this.loadEnvironments(projectName);
    this.loading.set(true);
    this.requestFailed.set(false);
    this.tasks.getProjectDeploymentSummary(projectName).subscribe({
      next: summary => {
        this.summary.set(summary);
        this.revisionMode.set(summary.defaultEvidenceRun ? 'tested' : 'head');
        this.headExceptionReason.set('');
        const current = this.selectedTarget();
        const next = summary.targets.find(target => target.id === current?.id) ?? summary.targets[0] ?? null;
        if (next) this.chooseTarget(next);
        this.loading.set(false);
      },
      error: () => {
        this.summary.set(null);
        this.requestFailed.set(true);
        this.loading.set(false);
      },
    });
    this.tasks.getWatchPaths().subscribe({ next: paths => this.workspaces.set(paths) });
  }

  /** Declarations and history, with each environment's state from the shared projection (M2). */
  private loadEnvironments(projectName: string): void {
    this.environmentReads.unsubscribe();
    this.environmentReads = this.deployData.environmentStates(projectName).subscribe(panel => {
      if (panel.status === 'loading') return;
      if (panel.status === 'ready' && panel.value.environments) {
        this.environments.set(panel.value.environments);
        this.environmentStates.set(new Map(panel.value.states.map(state => [state.id, state])));
      } else {
        this.environmentError.set('Environments could not be loaded.');
      }
    });
  }
}

function shellValue(value: string | boolean | undefined): string {
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  return `'${String(value ?? '').replace(/'/g, `'"'"'`)}'`;
}

function humanize(value: string): string {
  return value.replace(/[-_]/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, first => first.toUpperCase());
}
