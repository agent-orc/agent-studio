import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import type { DeploymentState, EnvironmentStatus } from '../../models/deployment.model';
import { DEPLOYMENT_CLOCK, DeploymentStatusService } from '../../services/deployment-status.service';

interface DashboardRow {
  id: string;
  name: string;
  groupId: string;
  statuses: EnvironmentStatus[];
}

@Component({
  selector: 'app-workspace-deployment-dashboard',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-deployment-dashboard.html',
  styleUrl: './workspace-deployment-dashboard.scss',
})
export class WorkspaceDeploymentDashboardComponent {
  private readonly service = inject(DeploymentStatusService);
  private readonly now = inject(DEPLOYMENT_CLOCK);
  readonly projectNames = input.required<readonly string[]>();
  readonly projectStorageByName = input<ReadonlyMap<string, string>>(new Map());
  readonly workspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  readonly openDeployment = output<string>();
  readonly groupFilter = signal('all');
  readonly environmentFilter = signal('all');
  readonly environments = [
    { id: 'staging', name: 'Staging' },
    { id: 'production', name: 'Production' },
  ] as const;

  readonly groups = computed(() => this.workspaces().map(workspace => ({
    id: workspace.id, name: workspace.displayName,
  })));

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
      .map((project, index) => ({
        ...project, statuses: this.service.statuses(project.id, index),
      }))
      .filter(project => this.groupFilter() === 'all' || project.groupId === this.groupFilter());
  });

  readonly visibleEnvironments = computed(() => this.environments.filter(environment =>
    this.environmentFilter() === 'all' || environment.id === this.environmentFilter()));

  statusFor(row: DashboardRow, kind: 'staging' | 'production'): EnvironmentStatus | undefined {
    return row.statuses.find(status => status.environment.kind === kind);
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
