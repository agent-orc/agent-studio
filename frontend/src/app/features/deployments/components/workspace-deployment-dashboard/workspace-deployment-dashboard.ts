import { studioProjectSlug } from '../../../../services/studio-project-slug.util';
import { ChangeDetectionStrategy, ChangeDetectorRef, Component, computed, inject, input, output, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of, switchMap } from 'rxjs';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import type { DeploymentState, EnvironmentStatus, InstanceRunState } from '../../models/deployment.model';
import { DEPLOYMENT_CLOCK, DeploymentStatusService } from '../../services/deployment-status.service';
import type { DeployMatrixResponse, DeployMatrixRow } from '../../services/deployment-status.service';
import type { DeploymentUrl, InstallationOverview, UrlObservation } from '../../models/observed-deployment.model';
import { InstallationOverviewComponent } from '../installation-overview/installation-overview';
import { DeliveryLanesComponent } from '../delivery-lanes/delivery-lanes';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';
import { StatusIndicatorComponent, type StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';

/**
 * Current deployment state per cell (AGT-3403); unknown states stay neutral.
 * `stalled` is the instance deploy run that stopped reporting (AGT-3949).
 */
const DASHBOARD_STATE_TONE: Readonly<Record<DeploymentState | InstanceRunState, StatusTone>> = {
  queued: 'neutral',
  running: 'running',
  succeeded: 'success',
  failed: 'danger',
  stalled: 'warning',
  cancelled: 'neutral',
  'awaiting-approval': 'warning',
};

export function dashboardStateTone(state: string | null | undefined): StatusTone {
  return state && Object.hasOwn(DASHBOARD_STATE_TONE, state)
    ? DASHBOARD_STATE_TONE[state as keyof typeof DASHBOARD_STATE_TONE] : 'neutral';
}

type DashboardHealth = EnvironmentStatus['health'];
const DASHBOARD_HEALTH: Readonly<Record<DashboardHealth, { tone: StatusTone; label: string }>> = {
  healthy: { tone: 'success', label: 'Healthy' },
  unhealthy: { tone: 'danger', label: 'Unhealthy' },
  unknown: { tone: 'neutral', label: 'Unknown' },
};

export function dashboardHealth(health: string): { tone: StatusTone; label: string } {
  return Object.hasOwn(DASHBOARD_HEALTH, health) ? DASHBOARD_HEALTH[health as DashboardHealth]
    : { tone: 'neutral', label: health.charAt(0).toUpperCase() + health.slice(1) };
}

/** Where a project repository declares its environments, linked from a row without any. */
export const ENVIRONMENT_DECLARATION_DOCS =
  'https://github.com/agent-orc/agent-studio/blob/develop/docs/concepts/deployment-first-class.md#stage-and-production-host-deployments';

const EMPTY_MATRIX: DeployMatrixResponse = { environmentKinds: [], projects: [] };

interface MatrixLoad { state: 'loading' | 'loaded' | 'failed'; value: DeployMatrixResponse }

interface DashboardRow {
  id: string;
  name: string;
  groupId: string;
  tenant: string | null;
  statuses: EnvironmentStatus[];
  installation: InstallationOverview | null;
  instanceStatus: EnvironmentStatus | undefined;
  urls: DeploymentUrl[];
  /** False when the project declares no environments; the row then says so. */
  declared: boolean;
  error: string | null;
}

@Component({
  selector: 'app-workspace-deployment-dashboard',
  standalone: true,
  imports: [DisclosureMarkerComponent, DeliveryLanesComponent, StatusIndicatorComponent, InstallationOverviewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-deployment-dashboard.html',
  styleUrl: './workspace-deployment-dashboard.scss',
})
export class WorkspaceDeploymentDashboardComponent {
  protected readonly changeDetector = inject(ChangeDetectorRef);
  readonly projectSlug = studioProjectSlug;
  readonly declarationDocs = ENVIRONMENT_DECLARATION_DOCS;
  private readonly service = inject(DeploymentStatusService);
  private readonly now = inject(DEPLOYMENT_CLOCK);
  private readonly load = toSignal(this.service.matrix().pipe(
    map((value): MatrixLoad => ({ state: 'loaded', value })),
    catchError(() => of<MatrixLoad>({ state: 'failed', value: EMPTY_MATRIX }))),
    { initialValue: { state: 'loading', value: EMPTY_MATRIX } as MatrixLoad });
  private readonly matrix = computed(() => this.load().value);
  readonly projectNames = input.required<readonly string[]>();
  readonly projectStorageByName = input<ReadonlyMap<string, string>>(new Map());
  readonly workspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  readonly openDeployment = output<string>();
  readonly openCard = output<string>();
  readonly groupFilter = signal('all');
  readonly environmentFilter = signal('all');
  readonly tenantFilter = signal('all');
  readonly hasInstallation = computed(() => this.matrix().projects.some(row => row.installation || row.statuses.some(status => status.instance)));
  readonly hasRecords = computed(() => !!this.matrix().hasDeployRecords);
  readonly environments = computed(() => {
    const options = new Map([['staging', 'Staging'], ['production', 'Production']]);
    for (const row of this.matrix().projects) {
      for (const status of [...row.statuses, ...row.tenantStatuses]) {
        if (status.environment.kind === 'other') options.set(this.column(status), status.environment.name);
      }
      for (const url of row.urls ?? []) options.set(this.urlColumn(url), url.name);
    }
    return [...options].map(([id, name]) => ({ id, name }));
  });
  private readonly probes = toSignal(toObservable(this.matrix).pipe(switchMap(matrix =>
    this.service.urlObservations(matrix.projects.flatMap(row => row.urls ?? [])))));

  readonly groups = computed(() => this.workspaces().map(workspace => ({
    id: workspace.id, name: workspace.displayName,
  })));
  readonly tenants = computed(() => [...new Set(this.matrix().projects.flatMap(row =>
    row.tenantStatuses.map(status => status.tenant).filter((tenant): tenant is string => !!tenant)))].sort());

  readonly rows = computed<DashboardRow[]>(() => {
    const normalized = (path: string) => path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
    const registry = this.workspaces().flatMap(workspace => workspace.projects
      .map(project => {
        const name = this.projectNames().find(candidate => candidate === project.displayName
          || normalized(this.projectStorageByName().get(candidate) ?? '') === normalized(project.storageLocation));
        return name ? { id: project.id, name, groupId: workspace.id } : null;
      })
      .filter((project): project is { id: string; name: string; groupId: string } => project !== null));
    const registered = new Set(registry.map(project => project.name));
    const unassigned = this.projectNames().filter(name => !registered.has(name))
      .map(name => ({ id: name, name, groupId: 'unassigned' }));
    const matched = new Set<DeployMatrixRow>();
    const known = [...registry, ...unassigned].flatMap(project => {
      const matrixRow = this.matrix().projects.find(row => row.projectId === project.name || row.projectId === project.id);
      if (!matrixRow) return [];
      matched.add(matrixRow);
      return [{ project, matrixRow }];
    });
    // A watched project the shell does not list by that name is still shown.
    const unlisted = this.matrix().projects.filter(row => !matched.has(row))
      .map(matrixRow => ({ project: { id: matrixRow.projectId, name: matrixRow.projectId, groupId: 'unassigned' }, matrixRow }));
    return [...known, ...unlisted]
      .sort((a, b) => a.project.name.localeCompare(b.project.name))
      .flatMap(({ project, matrixRow }) => {
        const shared = { declared: matrixRow.environmentsDeclared !== false, error: matrixRow.error };
        const tenants = [...new Set(matrixRow.tenantStatuses.map(status => status.tenant).filter((tenant): tenant is string => !!tenant))];
        return [{ ...project, ...shared, tenant: null, installation: matrixRow.installation ?? null, instanceStatus: matrixRow.statuses.find(status => !!status.instance), urls: matrixRow.urls ?? [],
            statuses: matrixRow.statuses.filter(status => !matrixRow.installation || !status.instance) },
          ...tenants.map(tenant => ({ ...project, ...shared, id: `${project.id}:${tenant}`, tenant,
            installation: null, instanceStatus: undefined, urls: [], statuses: matrixRow.tenantStatuses.filter(status => status.tenant === tenant) }))];
      })
      .filter(project => this.tenantFilter() === 'all' || project.tenant === this.tenantFilter())
      .filter(project => this.groupFilter() === 'all' || project.groupId === this.groupFilter());
  });

  readonly installationRow = computed(() => ['all', 'production'].includes(this.environmentFilter())
    ? this.rows().find(row => row.installation) : undefined);
  readonly installationTitle = computed(() => this.installationRow()?.statuses.some(status =>
    status.environment.name.toLowerCase() === 'production') ? 'Running Studio build' : 'Production');
  readonly noTargets = computed(() => this.environmentFilter() === 'all' ? this.rows().filter(row =>
    !row.installation && !row.declared && !row.error && !row.urls.length && !row.statuses.length) : []);
  readonly matrixRows = computed(() => this.rows().filter(row => {
    const filter = this.environmentFilter();
    if (row.error) return true;
    return row.statuses.some(status => filter === 'all' || this.column(status) === filter)
      || row.urls.some(url => filter === 'all' || this.urlColumn(url) === filter);
  }));
  readonly visibleEnvironments = computed(() => {
    const filter = this.environmentFilter();
    if (filter !== 'all') return this.environments().filter(item => item.id === filter);
    const rows = this.matrixRows();
    const columns = this.environments().filter(item => !item.id.startsWith('url:')
      && (rows.some(row => row.statuses.length) || !rows.some(row => row.urls.length)));
    if (rows.some(row => row.urls.length)) columns.push({ id: 'urls', name: 'Registered URLs' });
    return columns;
  });
  readonly watchedProjects = computed(() => this.environmentFilter() === 'all' && this.tenantFilter() === 'all'
    ? [...new Set(this.matrixRows().filter(row => !row.installation && !row.error && row.statuses.length).map(row => row.name))]
    : []);

  column(status: EnvironmentStatus): string {
    return status.environment.kind === 'other' ? 'declared:' + status.environment.name.toLowerCase() : status.environment.kind;
  }
  urlColumn(url: DeploymentUrl): string { return 'url:' + url.name.toLowerCase(); }
  observation(url: DeploymentUrl): UrlObservation | undefined { return this.probes()?.get(url.projectId + '/' + url.id); }
  reachable(fact: UrlObservation): boolean {
    return fact.statusCode !== null && fact.statusCode >= 200 && fact.statusCode < 300;
  }
  reachability(fact: UrlObservation): string {
    if (fact.statusCode !== null) return 'HTTP ' + fact.statusCode + (this.reachable(fact) ? ' · Reachable' : ' · HTTP error');
    if (fact.kind === 'local-only') return 'Local only · Open on the serving machine';
    if (fact.kind === 'timeout') return 'Probe timed out';
    if (fact.kind === 'offline') return 'Unreachable';
    return 'Reachability not observed · Check URL probe access';
  }

  /** Why the table has no row: still loading, the read failed, nothing is watched, or the filters hide everything. */
  readonly emptyReason = computed(() => {
    const load = this.load();
    if (load.state === 'loading') return 'Loading deployments…';
    if (load.state === 'failed') return 'The deployment status could not be loaded. Reload the page to try again.';
    if (!load.value.projects.length) return 'This instance watches no project you have access to, so there is nothing to show yet.';
    return 'No project matches the selected filters.';
  });

  statusesFor(row: DashboardRow, column: string): EnvironmentStatus[] {
    return row.statuses.filter(status => this.column(status) === column);
  }

  cellLabel(row: DashboardRow, environment: string): string {
    return [row.name, row.tenant, environment, 'deployment'].filter(Boolean).join(' ');
  }

  readonly stateTone = dashboardStateTone;
  healthTone(health: string): StatusTone { return dashboardHealth(health).tone; }
  healthLabel(health: string): string { return dashboardHealth(health).label; }

  /** The instance reports its own last run, which can be stalled; every other environment its current deployment. */
  cellState(status: EnvironmentStatus): string | undefined {
    return status.instance ? status.instance.lastRun?.state : status.currentDeployment?.state;
  }

  stateLabel(state: string | undefined): string {
    if (!state) return 'Unknown';
    return state === 'awaiting-approval' ? 'Awaiting approval'
      : state.charAt(0).toUpperCase() + state.slice(1);
  }

  age(status: EnvironmentStatus): string {
    if (status.releaseUncertain) return 'Deploy time unknown';
    if (status.instance) {
      const since = status.instance.liveSinceUtc;
      return since ? `Live ${this.elapsed(since)}` : 'Not live yet';
    }
    const deployedAt = status.currentDeployment?.finishedAtUtc ?? status.currentDeployment?.startedAtUtc;
    if (!deployedAt) return 'Not deployed';
    const elapsed = this.elapsed(deployedAt);
    return elapsed.charAt(0).toUpperCase() + elapsed.slice(1);
  }

  /** The commit that is live, and the last deploy run where it is not simply done. */
  instanceNote(status: EnvironmentStatus): string {
    const commit = status.currentRelease?.commitSha.slice(0, 9);
    const run = status.instance?.lastRun;
    const deploy = !run || run.state === 'succeeded' ? null
      : `deploy of ${run.version} ${run.state}${run.step && run.state !== 'running' ? ` at ${run.step}` : ''}`;
    return ['This Studio instance', commit ? `commit ${commit}` : null, deploy].filter(Boolean).join(' · ');
  }

  private elapsed(at: string): string {
    const hours = Math.max(0, Math.floor((this.now() - Date.parse(at)) / 3_600_000));
    if (hours < 1) return 'less than an hour ago';
    if (hours < 24) return `${hours}h ago`;
    return `${Math.floor(hours / 24)}d ago`;
  }
}
