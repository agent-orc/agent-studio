import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { forkJoin, map, of, Subscription, switchMap } from 'rxjs';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { serverMessage } from '../../services/project-deploy-data.service';

/** One managed database as `GET .../environments/{id}/databases` returns it (AGT-3877). No value field exists. */
export interface ManagedDatabase {
  environmentId: string; name: string; engine: string; version: string; runningVersion: string | null; size: string;
  resources: { cpus: number; memoryMb: number; pidsLimit: number }; variable: string; credentialReference: string;
  state: string; declared: boolean; volumeBytes: number | null; observedAt: string | null; message: string | null;
  deletionRequestedBy: string | null; deletionRequestedAt: string | null; deletedAt: string | null;
}
interface EnvironmentList { environments: { declaration: { id: string; name: string } }[] }
interface EnvironmentDatabases { id: string; name: string; databases: ManagedDatabase[] }

const STATES: Record<string, { label: string; tone: StatusTone }> = {
  'running': { label: 'Running', tone: 'success' },
  'provisioning': { label: 'Provisioning', tone: 'running' },
  'pending-deploy': { label: 'Waiting for a deploy', tone: 'info' },
  'retained': { label: 'Retained, not declared', tone: 'neutral' },
  'deleting': { label: 'Deletion requested', tone: 'running' },
  'deleted': { label: 'Data deleted', tone: 'neutral' },
  'upgrade-refused': { label: 'Upgrade refused', tone: 'warning' },
  'unhealthy': { label: 'Unhealthy', tone: 'danger' },
  'failed': { label: 'Failed', tone: 'danger' },
};
export function databaseState(state: string): { label: string; tone: StatusTone } {
  return STATES[state] ?? { label: state, tone: 'neutral' };
}

export function formatBytes(bytes: number | null): string {
  if (bytes === null) return 'Not measured yet';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${unit === 0 ? value : value.toFixed(1)} ${units[unit]}`;
}

/**
 * Managed databases per environment in the Bereitstellung area. Deleting data
 * is an owner action in two steps: the owner opens the confirmation, types the
 * database name, then confirms. Only retained databases offer it.
 */
@Component({
  selector: 'app-managed-databases-panel',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  templateUrl: './managed-databases-panel.html',
  styleUrl: './managed-databases-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ManagedDatabasesPanelComponent {
  private readonly http = inject(HttpClient);
  private loadRequest?: Subscription;
  /** Advances on every project switch so responses started for another project are dropped. */
  private projectEpoch = 0;
  private displayedProject: string | null = null;
  readonly projectName = input.required<string>();
  readonly environments = signal<EnvironmentDatabases[]>([]);
  readonly withDatabases = computed(() => this.environments().filter(item => item.databases.length > 0));
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly confirming = signal<string | null>(null);
  readonly typed = signal('');
  readonly state = databaseState;
  readonly bytes = formatBytes;

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      untracked(() => this.load(project));
      onCleanup(() => this.loadRequest?.unsubscribe());
    });
  }

  key(database: ManagedDatabase): string { return `${database.environmentId}/${database.name}`; }

  startDeletion(database: ManagedDatabase): void {
    this.confirming.set(this.key(database));
    this.typed.set('');
    this.notice.set(null);
  }

  cancelDeletion(): void { this.confirming.set(null); this.typed.set(''); }

  type(event: Event): void { this.typed.set((event.target as HTMLInputElement).value); }

  confirmDeletion(database: ManagedDatabase): void {
    if (this.typed() !== database.name) return;
    const project = this.projectName();
    const epoch = this.projectEpoch;
    this.busy.set(true);
    this.error.set(null);
    this.http.post(`/api/projects/${encodeURIComponent(project)}/environments/${encodeURIComponent(database.environmentId)}`
      + `/databases/${encodeURIComponent(database.name)}/deletion`, { confirm: this.typed() }).subscribe({
      next: () => {
        if (epoch !== this.projectEpoch) return;
        this.busy.set(false);
        this.cancelDeletion();
        this.notice.set(`Deletion of ${database.name} requested. The run host deletes the volume on its next poll.`);
        this.load(project);
      },
      error: error => {
        if (epoch !== this.projectEpoch) return;
        this.busy.set(false);
        this.error.set(serverMessage(error) ?? 'The deletion could not be requested.');
      },
    });
  }

  load(project: string): void {
    this.loadRequest?.unsubscribe();
    if (project !== this.displayedProject) {
      this.displayedProject = project;
      this.projectEpoch++;
      this.environments.set([]);
      this.busy.set(false);
      this.notice.set(null);
      this.cancelDeletion();
    }
    const epoch = this.projectEpoch;
    this.loading.set(true);
    this.error.set(null);
    const base = `/api/projects/${encodeURIComponent(project)}/environments`;
    this.loadRequest = this.http.get<EnvironmentList>(base).pipe(
      switchMap(list => list.environments.length
        ? forkJoin(list.environments.map(environment => this.http.get<{ databases: ManagedDatabase[] }>(
          `${base}/${encodeURIComponent(environment.declaration.id)}/databases`).pipe(
          map(data => ({ id: environment.declaration.id, name: environment.declaration.name, databases: data.databases })))))
        : of([] as EnvironmentDatabases[])),
    ).subscribe({
      next: items => {
        if (epoch !== this.projectEpoch) return;
        this.environments.set(items);
        this.loading.set(false);
      },
      error: () => {
        if (epoch !== this.projectEpoch) return;
        this.loading.set(false);
        this.error.set('Managed databases could not be loaded.');
      },
    });
  }
}
