import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import type { DeploymentState, EnvironmentStatus } from '../../models/deployment.model';
import { DEPLOYMENT_CLOCK, DeploymentStatusService } from '../../services/deployment-status.service';
import type { DeployMatrixResponse } from '../../services/deployment-status.service';
import { DeliveryLanesComponent } from '../delivery-lanes/delivery-lanes';

interface DashboardRow {
  id: string;
  name: string;
  groupId: string;
  tenant: string | null;
  statuses: EnvironmentStatus[];
}

@Component({
  selector: 'app-workspace-deployment-dashboard',
  standalone: true,
  imports: [DeliveryLanesComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-deployment-dashboard.html',
  styleUrl: './workspace-deployment-dashboard.scss',
})
export class WorkspaceDeploymentDashboardComponent {
  private readonly service = inject(DeploymentStatusService);
  private readonly now = inject(DEPLOYMENT_CLOCK);
  private readonly matrix = toSignal(this.service.matrix().pipe(catchError(() => of<DeployMatrixResponse>({ environmentKinds: [], projects: [] }))),
    { initialValue: { environmentKinds: [], projects: [] } as DeployMatrixResponse });
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
    return [...registry, ...unassigned]
      .sort((a, b) => a.name.localeCompare(b.name))
      .flatMap(project => {
        const matrixRow = this.matrix().projects.find(row => row.projectId === project.name || row.projectId === project.id);
        if (!matrixRow) return [];
        const tenants = [...new Set(matrixRow.tenantStatuses.map(status => status.tenant).filter((tenant): tenant is string => !!tenant))];
        return [{ ...project, tenant: null, statuses: matrixRow.statuses },
          ...tenants.map(tenant => ({ ...project, id: `${project.id}:${tenant}`, tenant,
            statuses: matrixRow.tenantStatuses.filter(status => status.tenant === tenant) }))];
      })
      .filter(project => this.tenantFilter() === 'all' || project.tenant === this.tenantFilter())
      .filter(project => this.groupFilter() === 'all' || project.groupId === this.groupFilter());
  });

  readonly visibleEnvironments = computed(() => this.environments.filter(environment =>
    this.environmentFilter() === 'all' || environment.id === this.environmentFilter()));

  statusFor(row: DashboardRow, kind: 'staging' | 'production'): EnvironmentStatus | undefined {
    return row.statuses.find(status => status.environment.kind === kind);
  }

  cellLabel(row: DashboardRow, environment: string): string {
    return [row.name, row.tenant, environment, 'deployment'].filter(Boolean).join(' ');
  }

  stateLabel(state: DeploymentState | undefined): string {
    if (!state) return 'Unknown';
    return state === 'awaiting-approval' ? 'Awaiting approval'
      : state.charAt(0).toUpperCase() + state.slice(1);
  }

  age(status: EnvironmentStatus): string {
    const deployedAt = status.currentDeployment?.finishedAtUtc ?? status.currentDeployment?.startedAtUtc;
    if (!deployedAt) return 'Not deployed';
    const hours = Math.max(0, Math.floor((this.now() - Date.parse(deployedAt)) / 3_600_000));
    if (hours < 1) return 'Less than an hour ago';
    if (hours < 24) return `${hours}h ago`;
    return `${Math.floor(hours / 24)}d ago`;
  }
}
