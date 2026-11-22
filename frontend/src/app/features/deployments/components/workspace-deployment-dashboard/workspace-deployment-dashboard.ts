import { studioProjectSlug } from '../../../../services/studio-project-slug.util';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of } from 'rxjs';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import type { DeploymentState, EnvironmentStatus, InstanceRunState } from '../../models/deployment.model';
import { DEPLOYMENT_CLOCK, DeploymentStatusService } from '../../services/deployment-status.service';
import type { DeployMatrixResponse, DeployMatrixRow } from '../../services/deployment-status.service';
import { DeliveryLanesComponent } from '../delivery-lanes/delivery-lanes';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

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
  /** False when the project declares no environments; the row then says so. */
  declared: boolean;
  error: string | null;
}

@Component({
  selector: 'app-workspace-deployment-dashboard',
  standalone: true,
  imports: [DeliveryLanesComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-deployment-dashboard.html',
  styleUrl: './workspace-deployment-dashboard.scss',
})
export class WorkspaceDeploymentDashboardComponent {
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
  readonly environments = [
    { id: 'staging', name: 'Staging' },
    { id: 'production', name: 'Production' },
  ] as const;

  readonly groups = computed(() => this.workspaces().map(workspace => ({
    id: workspace.id, name: workspace.displayName,
  })));
  readonly tenants = computed(() => [...new Set(this.matrix().projects.flatMap(row =>
    row.tenantStatuses.map(status => status.tenant).filter((tenant): tenant is string => !!tenant)))].sort());

  /**
   * The matrix has one row per project this instance watches, so it also
   * decides which projects get delivery lanes: a project the workspace
   * registers but this instance does not watch is never requested.
   */
  readonly watchedProjects = computed(() => this.matrix().projects.map(row => row.projectId));

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
        return [{ ...project, ...shared, tenant: null, statuses: matrixRow.statuses },
          ...tenants.map(tenant => ({ ...project, ...shared, id: `${project.id}:${tenant}`, tenant,
            statuses: matrixRow.tenantStatuses.filter(status => status.tenant === tenant) }))];
      })
      .filter(project => this.tenantFilter() === 'all' || project.tenant === this.tenantFilter())
      .filter(project => this.groupFilter() === 'all' || project.groupId === this.groupFilter());
  });

  readonly visibleEnvironments = computed(() => this.environments.filter(environment =>
    this.environmentFilter() === 'all' || environment.id === this.environmentFilter()));

  /** Why the table has no row: still loading, the read failed, nothing is watched, or the filters hide everything. */
  readonly emptyReason = computed(() => {
    const load = this.load();
    if (load.state === 'loading') return 'Loading deployments…';
    if (load.state === 'failed') return 'The deployment status could not be loaded. Reload the page to try again.';
    if (!load.value.projects.length) return 'This instance watches no project you have access to, so there is nothing to show yet.';
    return 'No project matches the selected filters.';
  });

  statusFor(row: DashboardRow, kind: 'staging' | 'production'): EnvironmentStatus | undefined {
    return row.statuses.find(status => status.environment.kind === kind);
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
